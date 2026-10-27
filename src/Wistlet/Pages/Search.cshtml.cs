using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class SearchModel(AppDbContext db, PostService posts) : PageModel
{
    public string Query { get; private set; } = "";
    public List<UserSummary> Users { get; private set; } = [];
    public List<TrendingTag> Tags { get; private set; } = [];
    public List<PostView> Posts { get; private set; } = [];

    public async Task OnGetAsync(string? q)
    {
        Query = (q ?? "").Trim();
        if (Query.Length == 0) return;
        if (Query.Length > 100) Query = Query[..100];

        var me = User.GetUserId();
        var term = Query.TrimStart('@', '#').ToLowerInvariant();
        var now = DateTime.UtcNow;

        Users = await db.Users.AsNoTracking()
            .Where(u => !(u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now)))
            .Where(u => u.NormalizedUserName.Contains(term) || u.DisplayName.ToLower().Contains(term))
            .Where(u => me != null || !u.HiddenFromGuests)
            .OrderBy(u => u.NormalizedUserName == term ? 0 : 1).ThenBy(u => u.UserName)
            .Take(8)
            .Select(u => new UserSummary(u.Id, u.UserName, u.DisplayName, u.AvatarPath, u.IsVerified, u.Role, u.Bio, u.AvatarShape))
            .ToListAsync();

        Tags = (await db.PostTags.AsNoTracking()
            .Where(t => t.Tag.StartsWith(term))
            .GroupBy(t => t.Tag)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count)
            .Take(8)
            .ToListAsync())
            .Select(t => new TrendingTag(t.Tag, t.Count)).ToList();

        var lowered = Query.ToLower();
        Posts = await posts.ProjectAsync(
            (await posts.FeedAsync(me, User.IsStaff()))
                .Where(p => p.Content.ToLower().Contains(lowered) || (p.Headline != null && p.Headline.ToLower().Contains(lowered)))
                .OrderByDescending(p => p.Id),
            me, 40);
    }
}
