using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
public class NotificationsModel(AppDbContext db, NotificationService notifications) : PageModel
{
    public List<NotificationDay> Days { get; private set; } = [];
    public int UnreadCount { get; private set; }

    public async Task OnGetAsync()
    {
        var me = User.RequireUserId();
        Days = await notifications.GetGroupedAsync(me);
        UnreadCount = Days.Sum(d => d.Groups.Count(g => g.IsUnread));

        // Viewing the list marks everything as read.
        await db.Notifications.Where(n => n.UserId == me && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        var me = User.RequireUserId();
        await db.Notifications.Where(n => n.UserId == me).ExecuteDeleteAsync();
        TempData["Flash"] = "Notifications cleared.";
        return RedirectToPage();
    }
}
