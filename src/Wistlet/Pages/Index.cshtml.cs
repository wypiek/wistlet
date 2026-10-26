using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class IndexModel(AppDbContext db, PostService posts) : PageModel
{
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }
    public bool FollowsAnyone { get; private set; }

    public async Task OnGetAsync(int? before)
    {
        var me = User.GetUserId();
        var feed = await posts.FeedAsync(me, User.IsStaff());
        if (me is int id)
        {
            FollowsAnyone = await db.Follows.AnyAsync(f => f.FollowerId == id) ||
                            await db.TagPreferences.AnyAsync(t => t.UserId == id && t.Kind == TagPreferenceKind.Follow);
            // Your own posts, people you follow, and posts with a tag you follow.
            var followedTags = db.TagPreferences.Where(t => t.UserId == id && t.Kind == TagPreferenceKind.Follow).Select(t => t.Tag);
            feed = feed.Where(p => p.AuthorId == id ||
                                   db.Follows.Any(f => f.FollowerId == id && f.FolloweeId == p.AuthorId) ||
                                   p.Tags.Any(t => followedTags.Contains(t.Tag)));
        }

        var (page, next) = await posts.PageAsync(feed, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/?before={n}" : null;
    }
}
