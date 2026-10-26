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
    /// <summary>The comments under one post of the page (a share shows one section per post in its chain, as on cohost).</summary>
    public record CommentSection(PostView Target, string Anchor, List<PostView> TopLevel, ILookup<int, PostView> Children, int Count, bool CanComment);

    public PostView Post { get; private set; } = null!;
    public List<CommentSection> Sections { get; private set; } = [];

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

        // Drafts can be opened by anyone who has the link (as on cohost), but appear nowhere else.
        var found = await posts.ProjectAsync(posts.Visible(me, User.IsStaff(), includeDrafts: true).Where(p => p.Id == id), me);
        if (found.Count == 0) return NotFound();
        Post = found[0];
        if (Post.IsDraft) return Page();

        // Each shared post that added something gets its own section, oldest first, then this post.
        var targets = Post.ShareTree.Where(t => !t.IsTransparentShare)
            .Select(t => (Post: t, Anchor: $"post-{Post.Id}-from-{t.Id}"))
            .ToList();
        if (!Post.IsTransparentShare || Post.ReplyCount > 0 || targets.Count == 0)
            targets.Add((Post, $"post-{Post.Id}"));

        foreach (var (target, anchor) in targets)
        {
            var ids = (await posts.SubtreeIdsAsync(target.Id)).Where(c => c != target.Id).ToList();
            var comments = await posts.ProjectAsync(visible.Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id), me);
            Sections.Add(new CommentSection(
                target, anchor,
                comments.Where(c => c.ParentId == target.Id).ToList(),
                comments.Where(c => c.ParentId != target.Id).ToLookup(c => c.ParentId!.Value),
                comments.Count,
                !target.CommentsLocked || target.Author.Id == me));
        }
        return Page();
    }
}
