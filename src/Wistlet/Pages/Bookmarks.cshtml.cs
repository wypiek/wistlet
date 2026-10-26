using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
public class BookmarksModel(AppDbContext db, PostService posts) : PageModel
{
    public List<PostView> Posts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.RequireUserId();
        var query = posts.Visible(me, User.IsStaff())
            .Where(p => p.Bookmarks.Any(b => b.UserId == me))
            .OrderByDescending(p => db.Bookmarks.Where(b => b.UserId == me && b.PostId == p.Id).Select(b => b.CreatedAt).First());
        Posts = await posts.ProjectAsync(query, me, 200);
    }
}
