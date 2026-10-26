using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
[EnableRateLimiting("write")]
public class ReportModel(AppDbContext db, PostService posts) : PageModel
{
    public static readonly string[] Categories = ["Spam", "Harassment or bullying", "Hate speech", "Violence or threats", "Adult content", "Impersonation", "Misinformation", "Other"];

    [BindProperty(SupportsGet = true)] public int? PostId { get; set; }
    [BindProperty(SupportsGet = true)] public int? UserId { get; set; }
    [BindProperty, Required] public string Category { get; set; } = "";
    [BindProperty, StringLength(1000)] public string? Reason { get; set; }

    public PostView? Post { get; private set; }
    public User? Target { get; private set; }

    private async Task<bool> LoadAsync()
    {
        var me = User.RequireUserId();
        if (PostId is int pid)
        {
            var found = await posts.ProjectAsync(posts.Visible(me, User.IsStaff()).Where(p => p.Id == pid), me);
            Post = found.FirstOrDefault();
            return Post is not null;
        }
        if (UserId is int uid)
        {
            Target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid);
            return Target is not null;
        }
        return false;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return NotFound();
        if (!Categories.Contains(Category)) ModelState.AddModelError(nameof(Category), "Pick a category.");
        if (!ModelState.IsValid) return Page();

        var me = User.RequireUserId();
        var duplicate = await db.Reports.AnyAsync(r => r.ReporterId == me && r.Status == ReportStatus.Open &&
                                                       r.PostId == PostId && r.TargetUserId == (PostId == null ? UserId : null));
        if (!duplicate)
        {
            db.Reports.Add(new Report
            {
                ReporterId = me,
                PostId = Post?.Id,
                TargetUserId = Post is null ? Target?.Id : null,
                Category = Category,
                Reason = (Reason ?? "").Trim(),
            });
            await db.SaveChangesAsync();
        }
        TempData["Flash"] = "Thanks — your report was sent to the moderators.";
        return Redirect(Post is not null ? $"/post/{Post.Id}" : $"/u/{Target!.UserName}");
    }
}
