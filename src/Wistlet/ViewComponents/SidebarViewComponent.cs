using Microsoft.AspNetCore.Mvc;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.ViewComponents;

public class SidebarViewComponent(PostService posts) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var me = HttpContext.User.GetUserId();
        var trending = await posts.TrendingAsync(10, forGuest: me is null);
        return View(new SidebarModel(me is not null, trending));
    }
}
