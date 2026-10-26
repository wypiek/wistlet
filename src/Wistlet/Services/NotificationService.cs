using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

public class NotificationService(AppDbContext db)
{
    /// <summary>Queues a notification (saved with the next SaveChanges). Self-notifications and blocked actors are skipped.</summary>
    public async Task NotifyAsync(int recipientId, int? actorId, NotificationType type, int? postId = null, string? message = null)
    {
        if (actorId == recipientId) return;
        if (actorId is int a && await db.Blocks.AnyAsync(b => b.BlockerId == recipientId && b.BlockedId == a)) return;

        // Collapse repeated like/follow notifications (e.g. like → unlike → like).
        if (type is NotificationType.Like or NotificationType.Follow &&
            await db.Notifications.AnyAsync(n => n.UserId == recipientId && n.ActorId == actorId && n.Type == type && n.PostId == postId))
            return;

        db.Notifications.Add(new Notification
        {
            UserId = recipientId,
            ActorId = actorId,
            Type = type,
            PostId = postId,
            Message = message,
        });
    }

    public Task<int> UnreadCountAsync(int userId) =>
        db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
}
