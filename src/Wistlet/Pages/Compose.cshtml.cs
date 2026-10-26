using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
public class ComposeModel(PostService posts) : PageModel
{
    /// <summary>The post being shared with added content, if any.</summary>
    public PostView? Sharing { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? share)
    {
        if (share is int id)
        {
            var me = User.GetUserId();
            Sharing = (await posts.ProjectAsync(posts.Visible(me, User.IsStaff()).Where(p => p.Id == id && p.ParentId == null), me)).FirstOrDefault();
            if (Sharing is null) return NotFound();
        }
        return Page();
    }
}
