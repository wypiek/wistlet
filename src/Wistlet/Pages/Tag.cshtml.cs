using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class TagModel(PostService posts) : PageModel
{
    public string Tag { get; private set; } = "";
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }

    public async Task OnGetAsync(string tag, int? before)
    {
        Tag = tag.Trim().TrimStart('#').ToLowerInvariant();
        var me = User.GetUserId();
        var query = posts.Visible(me, User.IsStaff()).Where(p => p.Tags.Any(t => t.Tag == Tag));
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/tag/{Uri.EscapeDataString(Tag)}?before={n}" : null;
    }
}
