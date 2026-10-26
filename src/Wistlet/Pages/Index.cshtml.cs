using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class IndexModel(AppDbContext db, PostService posts, TagService tagService) : PageModel
{
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }
    public bool FollowsAnyone { get; private set; }

    /// <summary>The viewer's timeline filters (cohost's dashboard settings).</summary>
    public bool HideShares { get; private set; }
    public bool HideReplies { get; private set; }
    public bool HideAsks { get; private set; }

    /// <summary>Posts from this one down were already seen; a "caught up" marker goes above it.</summary>
    public int? CaughtUpBeforeId { get; private set; }

    /// <summary>Newest post on the first page, for the "new posts" check.</summary>
    public int? NewestId { get; private set; }

    /// <summary>The home timeline: your own posts, people you follow, and posts with a tag you follow, filtered as you chose.</summary>
    private async Task<IQueryable<Post>> FeedAsync(int? me)
    {
        var feed = await posts.FeedAsync(me, User.IsStaff());
        if (me is not int id) return feed;

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
        HideShares = user.FeedHideShares;
        HideReplies = user.FeedHideReplies;
        HideAsks = user.FeedHideAsks;

        var followedTags = tagService.Expand(await db.TagPreferences
            .Where(t => t.UserId == id && t.Kind == TagPreferenceKind.Follow).Select(t => t.Tag).ToListAsync());
        feed = feed.Where(p => p.AuthorId == id ||
                               db.Follows.Any(f => f.FollowerId == id && f.FolloweeId == p.AuthorId) ||
                               p.Tags.Any(t => followedTags.Contains(t.Tag)));
        // A share that added nothing is a "share"; one with its own text, headline, images or ask is a "reply".
        if (HideShares)
            feed = feed.Where(p => !(p.IsShare && p.Content == "" && p.Headline == null && p.AskId == null && !p.Attachments.Any()));
        if (HideReplies)
            feed = feed.Where(p => !(p.IsShare && !(p.Content == "" && p.Headline == null && p.AskId == null && !p.Attachments.Any())));
        if (HideAsks)
            feed = feed.Where(p => p.AskId == null);
        return feed;
    }

    public async Task OnGetAsync(int? before)
    {
        var me = User.GetUserId();
        var feed = await FeedAsync(me);
        if (me is int id)
        {
            FollowsAnyone = await db.Follows.AnyAsync(f => f.FollowerId == id) ||
                            await db.TagPreferences.AnyAsync(t => t.UserId == id && t.Kind == TagPreferenceKind.Follow);
        }

        var (page, next) = await posts.PageAsync(feed, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/?before={n}" : null;

        if (before is null && Posts.Count > 0)
        {
            NewestId = Posts.Max(p => p.Id);
            if (me is int uid)
            {
                // Mark where the last visit ended, then remember how far this one goes.
                var seen = await db.Users.Where(u => u.Id == uid).Select(u => u.FeedSeenUpTo).FirstAsync();
                if (seen is int s && Posts[0].Id > s)
                    CaughtUpBeforeId = Posts.FirstOrDefault(p => p.Id <= s)?.Id;
                if (seen is null || NewestId > seen)
                    await db.Users.Where(u => u.Id == uid).ExecuteUpdateAsync(x => x.SetProperty(u => u.FeedSeenUpTo, NewestId));
            }
        }
    }

    /// <summary>How many timeline posts are newer than <paramref name="after"/> (polled by the page for its "new posts" button).</summary>
    public async Task<IActionResult> OnGetNewCountAsync(int after)
    {
        var me = User.GetUserId();
        if (me is null) return new JsonResult(new { count = 0 });
        var feed = await FeedAsync(me);
        var count = await feed.CountAsync(p => p.Id > after && p.AuthorId != me);
        return new JsonResult(new { count });
    }

    /// <summary>Turns one of the timeline filters on or off for the page you're posting as.</summary>
    public async Task<IActionResult> OnPostFilterAsync(string? which, bool hide)
    {
        if (User.GetUserId() is not int me) return Redirect("/account/login");
        var user = await db.Users.FirstAsync(u => u.Id == me);
        switch (which)
        {
            case "shares": user.FeedHideShares = hide; break;
            case "replies": user.FeedHideReplies = hide; break;
            case "asks": user.FeedHideAsks = hide; break;
        }
        await db.SaveChangesAsync();
        return Redirect("/");
    }
}
