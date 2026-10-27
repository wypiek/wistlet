using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>The home timeline, or for logged-out visitors the cohost-style welcome page.</summary>
[AllowGuests]
public class IndexModel(AppDbContext db, PostService posts) : PageModel
{
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }
    public bool FollowsAnyone { get; private set; }

    public async Task OnGetAsync(int? before)
    {
        if (User.GetUserId() is not int id) return;

        var feed = await posts.FeedAsync(id, User.IsStaff());
        FollowsAnyone = await db.Follows.AnyAsync(f => f.FollowerId == id);
        feed = feed.Where(p => p.AuthorId == id || db.Follows.Any(f => f.FollowerId == id && f.FolloweeId == p.AuthorId));

        var (page, next) = await posts.PageAsync(feed, id, before);
        Posts = page;
        MoreUrl = next is int n ? $"/?before={n}" : null;
    }
}
