using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
public class DraftsModel(PostService posts) : PageModel
{
    public List<PostView> Drafts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.RequireUserId();
        Drafts = await posts.ProjectAsync(
            posts.Visible(me, User.IsStaff(), includeDrafts: true).Where(p => p.AuthorId == me && p.IsDraft).OrderByDescending(p => p.Id), me, 100);
    }
}
