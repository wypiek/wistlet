using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Pages.Admin;

public class PostsModel(AppDbContext db) : PageModel
{
    public const int PageSize = 50;

    public record Row(int Id, string Content, string? ImagePath, DateTime CreatedAt, bool IsHidden, bool IsPinned, int? ParentId,
        int AuthorId, string AuthorUserName, int Likes, int Replies, int Reports);

    public List<Row> Rows { get; private set; } = [];
    public string? Q { get; private set; }
    public string Filter { get; private set; } = "all";
    public int PageNumber { get; private set; } = 1;
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public string PageUrl(int page) => $"/admin/posts?q={Uri.EscapeDataString(Q ?? "")}&filter={Filter}&p={page}";
    public string Self => PageUrl(PageNumber);

    public async Task OnGetAsync(string? q, string? filter, int p = 1)
    {
        Q = q?.Trim();
        Filter = filter is "hidden" or "pinned" or "reported" or "media" ? filter : "all";
        PageNumber = Math.Max(1, p);

        var query = db.Posts.AsNoTracking();
        if (!string.IsNullOrEmpty(Q))
        {
            var term = Q.ToLower();
            var handle = term.TrimStart('@');
            query = query.Where(x => x.Content.ToLower().Contains(term) || x.Author.NormalizedUserName == handle);
        }
        query = Filter switch
        {
            "hidden" => query.Where(x => x.IsHidden),
            "pinned" => query.Where(x => x.IsPinned),
            "media" => query.Where(x => x.ImagePath != null),
            "reported" => query.Where(x => db.Reports.Any(r => r.PostId == x.Id && r.Status == ReportStatus.Open)),
            _ => query,
        };

        Total = await query.CountAsync();
        Rows = await query.OrderByDescending(x => x.Id)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize)
            .Select(x => new Row(x.Id, x.Content, x.ImagePath, x.CreatedAt, x.IsHidden, x.IsPinned, x.ParentId,
                x.AuthorId, x.Author.UserName, x.Likes.Count, x.Replies.Count,
                db.Reports.Count(r => r.PostId == x.Id && r.Status == ReportStatus.Open)))
            .ToListAsync();
    }
}
