using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>The home timeline, or for logged-out visitors the cohost-style welcome page.</summary>
[AllowGuests]
public class IndexModel(AppDbContext db, PostService posts, TimelineService timeline) : PageModel
{
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }
    public bool FollowsAnyone { get; private set; }

    /// <summary>"latest" (people you follow, newest first) or "foryou" (ranked).</summary>
    public string Tab { get; private set; } = TimelineModes.Latest;

    public async Task OnGetAsync(string? tab, int? before, int page = 0)
    {
        if (User.GetUserId() is not int id) return;

        Tab = tab is null
            ? TimelineModes.Resolve(await db.Users.Where(u => u.Id == id).Select(u => u.TimelineMode).FirstOrDefaultAsync())
            : TimelineModes.Resolve(tab);
        FollowsAnyone = await db.Follows.AnyAsync(f => f.FollowerId == id);

        if (Tab == TimelineModes.ForYou)
        {
            page = Math.Clamp(page, 0, 50);
            var (ranked, hasMore) = await timeline.ForYouAsync(id, User.IsStaff(), page);
            Posts = ranked;
            MoreUrl = hasMore ? $"/?tab={TimelineModes.ForYou}&page={page + 1}" : null;
            return;
        }

        var feed = (await posts.FeedAsync(id, User.IsStaff()))
            .Where(p => p.AuthorId == id || db.Follows.Any(f => f.FollowerId == id && f.FolloweeId == p.AuthorId));
        var (latest, next) = await posts.PageAsync(feed, id, before);
        Posts = latest;
        MoreUrl = next is int n ? $"/?tab={TimelineModes.Latest}&before={n}" : null;
    }
}
