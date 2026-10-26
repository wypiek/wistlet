using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Pages;

[Authorize]
public class NotificationsModel(AppDbContext db) : PageModel
{
    public record Item(int Id, NotificationType Type, bool IsRead, DateTime CreatedAt, UserSummary? Actor, int? PostId, string? PostExcerpt, string? Message);

    public List<Item> Items { get; private set; } = [];

    /// <summary>People waiting for approval to follow the viewer's private page.</summary>
    public List<UserSummary> FollowRequests { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.RequireUserId();
        FollowRequests = await db.FollowRequests.AsNoTracking()
            .Where(f => f.FolloweeId == me)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new UserSummary(f.Follower.Id, f.Follower.UserName, f.Follower.DisplayName, f.Follower.AvatarPath, f.Follower.IsVerified, f.Follower.Role, "", f.Follower.AvatarShape))
            .ToListAsync();
        Items = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == me)
            .OrderByDescending(n => n.Id)
            .Take(100)
            .Select(n => new Item(
                n.Id, n.Type, n.IsRead, n.CreatedAt,
                n.Actor == null ? null : new UserSummary(n.Actor.Id, n.Actor.UserName, n.Actor.DisplayName, n.Actor.AvatarPath, n.Actor.IsVerified, n.Actor.Role, "", n.Actor.AvatarShape),
                n.PostId,
                n.Post == null ? null : n.Post.Content.Substring(0, Math.Min(140, n.Post.Content.Length)),
                n.Message))
            .ToListAsync();

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
