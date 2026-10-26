using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class PostModel(AppDbContext db, PostService posts) : PageModel
{
    public PostView Post { get; private set; } = null!;
    public List<PostView> TopLevelComments { get; private set; } = [];
    public ILookup<int, PostView> Children { get; private set; } = Enumerable.Empty<PostView>().ToLookup(p => 0);
    public int CommentCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var me = User.GetUserId();
        var visible = posts.Visible(me, User.IsStaff());

        // Comments live on their post's page: send links to a comment there.
        var rootId = id;
        for (var i = 0; i < 50; i++)
        {
            var parent = await db.Posts.AsNoTracking().Where(p => p.Id == rootId).Select(p => p.ParentId).FirstOrDefaultAsync();
            if (parent is not int pid) break;
            rootId = pid;
        }
        if (rootId != id) return Redirect($"/post/{rootId}#post-{id}");

        var found = await posts.ProjectAsync(visible.Where(p => p.Id == id), me);
        if (found.Count == 0) return NotFound();
        Post = found[0];

        var ids = (await posts.SubtreeIdsAsync(id)).Where(c => c != id).ToList();
        var comments = await posts.ProjectAsync(visible.Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id), me);
        CommentCount = comments.Count;
        Children = comments.Where(c => c.ParentId != id).ToLookup(c => c.ParentId!.Value);
        TopLevelComments = comments.Where(c => c.ParentId == id).ToList();
        return Page();
    }
}
