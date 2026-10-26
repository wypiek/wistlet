using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Pages.Admin;

public class UsersModel(AppDbContext db) : PageModel
{
    public const int PageSize = 50;

    public record Row(User User, int Posts, int Followers, int Reports, string? OwnerName);

    public List<Row> Rows { get; private set; } = [];
    public string? Q { get; private set; }
    public string Status { get; private set; } = "all";
    public string Sort { get; private set; } = "new";
    public int PageNumber { get; private set; } = 1;
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);

    public string PageUrl(int page) =>
        $"/admin/users?q={Uri.EscapeDataString(Q ?? "")}&status={Status}&sort={Sort}&p={page}";

    public async Task OnGetAsync(string? q, string? status, string? sort, int p = 1)
    {
        Q = q?.Trim();
        Status = status is "banned" or "staff" or "verified" ? status : "all";
        Sort = sort is "name" or "active" or "old" ? sort : "new";
        PageNumber = Math.Max(1, p);

        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(Q))
        {
            var term = Q.TrimStart('@').ToLower();
            users = users.Where(u => u.NormalizedUserName.Contains(term) || u.DisplayName.ToLower().Contains(term) || u.Email.ToLower().Contains(term));
        }
        users = Status switch
        {
            "banned" => users.Where(u => u.IsBanned),
            "staff" => users.Where(u => u.Role != UserRole.User),
            "verified" => users.Where(u => u.IsVerified),
            _ => users,
        };
        users = Sort switch
        {
            "name" => users.OrderBy(u => u.NormalizedUserName),
            "active" => users.OrderByDescending(u => u.LastSeenAt),
            "old" => users.OrderBy(u => u.Id),
            _ => users.OrderByDescending(u => u.Id),
        };

        Total = await users.CountAsync();
        Rows = await users.Skip((PageNumber - 1) * PageSize).Take(PageSize)
            .Select(u => new Row(u,
                db.Posts.Count(x => x.AuthorId == u.Id),
                db.Follows.Count(f => f.FolloweeId == u.Id),
                db.Reports.Count(r => r.Status == ReportStatus.Open && (r.TargetUserId == u.Id || r.Post!.AuthorId == u.Id)),
                u.Owner != null ? u.Owner.UserName : null))
            .ToListAsync();
    }
}
