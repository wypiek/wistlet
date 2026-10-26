using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

public class ReportsModel(AppDbContext db, PostService posts, AuditService audit) : PageModel
{
    public record Item(Report Report, PostView? Post);

    public string Status { get; private set; } = "open";
    public List<Item> Items { get; private set; } = [];

    public async Task OnGetAsync(string? status)
    {
        Status = status is "resolved" or "dismissed" or "all" ? status : "open";
        var query = db.Reports.AsNoTracking()
            .Include(r => r.Reporter).Include(r => r.TargetUser).Include(r => r.ResolvedBy)
            .AsQueryable();
        query = Status switch
        {
            "resolved" => query.Where(r => r.Status == ReportStatus.Resolved),
            "dismissed" => query.Where(r => r.Status == ReportStatus.Dismissed),
            "all" => query,
            _ => query.Where(r => r.Status == ReportStatus.Open),
        };
        var reports = await query.OrderByDescending(r => r.Id).Take(100).ToListAsync();

        var postIds = reports.Where(r => r.PostId != null).Select(r => r.PostId!.Value).Distinct().ToList();
        var postViews = (await posts.ProjectAsync(db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)), User.GetUserId()))
            .ToDictionary(p => p.Id);
        Items = reports.Select(r => new Item(r, r.PostId is int pid ? postViews.GetValueOrDefault(pid) : null)).ToList();
    }

    public async Task<IActionResult> OnPostResolveAsync(int id, string action, string? note, string? status)
    {
        var report = await db.Reports.Include(r => r.Post).ThenInclude(p => p!.Author).Include(r => r.TargetUser)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (report is null) return NotFound();

        var me = User.RequireAccountId();
        var target = report.Post is not null ? $"post #{report.Post.Id} by @{report.Post.Author.UserName}" : $"@{report.TargetUser?.UserName}";
        string message;
        switch (action)
        {
            case "dismiss":
                report.Status = ReportStatus.Dismissed;
                message = $"Report #{id} dismissed.";
                break;
            case "hide" when report.Post is not null:
                report.Post.IsHidden = true;
                report.Status = ReportStatus.Resolved;
                audit.Log("post.hide", target, $"via report #{id}");
                message = $"Post hidden and report #{id} resolved.";
                break;
            case "delete" when report.Post is not null:
                var postId = report.Post.Id;
                audit.Log("post.delete", target, $"via report #{id}");
                report.Status = ReportStatus.Resolved;
                report.ResolvedById = me;
                report.ResolvedAt = DateTime.UtcNow;
                report.ResolutionNote = note?.Trim();
                audit.Log("report.resolve", $"report #{id}", "post deleted");
                await db.SaveChangesAsync();
                // Deleting the post cascades to its reports, including this one.
                await posts.DeleteAsync(await db.Posts.FirstAsync(p => p.Id == postId));
                TempData["Flash"] = $"Post deleted and report #{id} closed.";
                return Redirect($"/admin/reports?status={status}");
            default:
                report.Status = ReportStatus.Resolved;
                message = $"Report #{id} marked as resolved.";
                break;
        }
        report.ResolvedById = me;
        report.ResolvedAt = DateTime.UtcNow;
        report.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        audit.Log(report.Status == ReportStatus.Dismissed ? "report.dismiss" : "report.resolve", $"report #{id}", $"{report.Category}: {target}");
        await db.SaveChangesAsync();
        TempData["Flash"] = message;
        return Redirect($"/admin/reports?status={status}");
    }

    public async Task<IActionResult> OnPostReopenAsync(int id, string? status)
    {
        var report = await db.Reports.FindAsync(id);
        if (report is null) return NotFound();
        report.Status = ReportStatus.Open;
        report.ResolvedAt = null;
        report.ResolvedById = null;
        audit.Log("report.reopen", $"report #{id}");
        await db.SaveChangesAsync();
        return Redirect($"/admin/reports?status={status}");
    }
}
