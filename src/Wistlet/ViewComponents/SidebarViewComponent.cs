using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.ViewComponents;

public class SidebarViewComponent(PostService posts, AppDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var me = HttpContext.User.GetUserId();
        var trending = await posts.TrendingAsync(10);
        var followed = me is int id
            ? await db.TagPreferences.AsNoTracking().Where(t => t.UserId == id && t.Kind == TagPreferenceKind.Follow).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync()
            : [];
        return View(new SidebarModel(me is not null, trending) { FollowedTags = followed });
    }
}
