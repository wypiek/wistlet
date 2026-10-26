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
        var visible = posts.Visible(me, User.IsStaff());
        IQueryable<Post> feed;
        if (me is int id)
        {
            FollowsAnyone = await db.Follows.AnyAsync(f => f.FollowerId == id);
            feed = visible.Where(p => p.AuthorId == id || db.Follows.Any(f => f.FollowerId == id && f.FolloweeId == p.AuthorId));
        }
        else
        {
            feed = visible.Where(p => p.ParentId == null);
        }

        var (page, next) = await posts.PageAsync(feed, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/?before={n}" : null;
    }
}
