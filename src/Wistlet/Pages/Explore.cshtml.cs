using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class ExploreModel(PostService posts) : PageModel
{
    public string Tab { get; private set; } = "latest";
    public List<PostView> Pinned { get; private set; } = [];
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }

    public async Task OnGetAsync(string? tab, int? before)
    {
        var me = User.GetUserId();
        var visible = posts.Visible(me, User.IsStaff());
        Tab = tab is "top" or "media" ? tab : "latest";

        if (before is null && Tab == "latest")
            Pinned = await posts.ProjectAsync(visible.Where(p => p.IsPinned).OrderByDescending(p => p.Id), me, 5);

        var topLevel = visible.Where(p => p.ParentId == null);
        switch (Tab)
        {
            case "top":
                var since = DateTime.UtcNow.AddDays(-7);
                Posts = await posts.ProjectAsync(
                    topLevel.Where(p => p.CreatedAt >= since)
                        .OrderByDescending(p => p.Likes.Count + p.Replies.Count * 2)
                        .ThenByDescending(p => p.Id),
                    me, 30);
                break;
            case "media":
                var (media, nextMedia) = await posts.PageAsync(visible.Where(p => p.ImagePath != null), me, before);
                Posts = media;
                MoreUrl = nextMedia is int nm ? $"/explore?tab=media&before={nm}" : null;
                break;
            default:
                var (latest, next) = await posts.PageAsync(topLevel.Where(p => !p.IsPinned || before != null), me, before);
                Posts = latest;
                MoreUrl = next is int n ? $"/explore?before={n}" : null;
                break;
        }
    }
}
