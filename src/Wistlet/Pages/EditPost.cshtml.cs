using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
public class EditPostModel(PostService posts) : PageModel
{
    public PostView Post { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var me = User.RequireUserId();
        var found = (await posts.ProjectAsync(posts.Visible(me, User.IsStaff(), includeDrafts: true).Where(p => p.Id == id), me)).FirstOrDefault();
        if (found is null) return NotFound();
        if (found.Author.Id != me) return Forbid();
        Post = found;
        return Page();
    }
}
