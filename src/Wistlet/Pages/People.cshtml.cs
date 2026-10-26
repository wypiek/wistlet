using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Pages;

public class PeopleModel(AppDbContext db) : PageModel
{
    public string Sort { get; private set; } = "popular";
    public List<UserCardModel> People { get; private set; } = [];

    public async Task OnGetAsync(string? sort)
    {
        Sort = sort is "new" or "active" ? sort : "popular";
        var me = User.GetUserId() ?? -1;
        var now = DateTime.UtcNow;
        var users = db.Users.AsNoTracking()
            .Where(u => !(u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now)))
            .Where(u => !db.Blocks.Any(b => (b.BlockerId == me && b.BlockedId == u.Id) || (b.BlockerId == u.Id && b.BlockedId == me)));
        users = Sort switch
        {
            "new" => users.OrderByDescending(u => u.CreatedAt),
            "active" => users.OrderByDescending(u => u.LastSeenAt),
            _ => users.OrderByDescending(u => db.Follows.Count(f => f.FolloweeId == u.Id)).ThenByDescending(u => u.Id),
        };
        var list = await users.Take(100).Select(u => new
        {
            Summary = new UserSummary(u.Id, u.UserName, u.DisplayName, u.AvatarPath, u.IsVerified, u.Role, u.Bio),
            Following = db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == u.Id),
        }).ToListAsync();
        People = list.Select(x => new UserCardModel(x.Summary, me > 0, x.Following)).ToList();
    }
}
