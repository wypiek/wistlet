using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class PostModel(PostService posts) : PageModel
{
    public PostView Post { get; private set; } = null!;
    public List<PostView> Ancestors { get; private set; } = [];
    public List<PostView> Replies { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var me = User.GetUserId();
        var visible = posts.Visible(me, User.IsStaff());

        var found = await posts.ProjectAsync(visible.Where(p => p.Id == id), me);
        if (found.Count == 0) return NotFound();
        Post = found[0];

        // Walk up the reply chain (bounded, in case of very deep threads).
        var parentId = Post.ParentId;
        while (parentId is int pid && Ancestors.Count < 10)
        {
            var parent = await posts.ProjectAsync(visible.Where(p => p.Id == pid), me);
            if (parent.Count == 0) break;
            Ancestors.Insert(0, parent[0]);
            parentId = parent[0].ParentId;
        }

        Replies = await posts.ProjectAsync(visible.Where(p => p.ParentId == id).OrderBy(p => p.Id), me, 200);
        return Page();
    }
}
