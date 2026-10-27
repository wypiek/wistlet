using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

public partial class NotificationService(AppDbContext db)
{
    /// <summary>Queues a notification (saved with the next SaveChanges). Self-notifications and blocked actors are skipped.</summary>
    public async Task NotifyAsync(int recipientId, int? actorId, NotificationType type, int? postId = null, string? message = null)
    {
        if (actorId == recipientId) return;
        // Accounts on other instances get their notifications there.
        if (await db.Users.AnyAsync(u => u.Id == recipientId && u.Domain != null)) return;
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

    /// <summary>The user's recent notifications, grouped by day and merged as on cohost.</summary>
    public async Task<List<NotificationDay>> GetGroupedAsync(int userId, int limit = 300)
    {
        var rows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.Id)
            .Take(limit)
            .Select(n => new NotificationRow(
                n.Id, n.Type, n.IsRead, n.CreatedAt,
                n.Actor == null ? null : new UserSummary(n.Actor.Id, n.Actor.UserName, n.Actor.DisplayName, n.Actor.AvatarPath, n.Actor.IsVerified, n.Actor.Role, "", n.Actor.AvatarShape),
                n.PostId,
                // Shares point at the shared post, comments at the post they're on.
                n.Post == null ? null : n.Post.SharedPostId ?? n.Post.ParentId,
                // Excerpts come from the rendered HTML (so Markdown syntax doesn't show) or the headline.
                n.Post == null ? null : n.Post.Headline ?? n.Post.RenderedHtml.Substring(0, Math.Min(2000, n.Post.RenderedHtml.Length)),
                n.Post == null ? null
                    : n.Post.SharedPost != null ? n.Post.SharedPost.Headline ?? n.Post.SharedPost.RenderedHtml.Substring(0, Math.Min(2000, n.Post.SharedPost.RenderedHtml.Length))
                    : n.Post.Parent != null ? n.Post.Parent.Headline ?? n.Post.Parent.RenderedHtml.Substring(0, Math.Min(2000, n.Post.Parent.RenderedHtml.Length))
                    : null,
                n.Message))
            .ToListAsync();
        return Group(rows, DateTime.UtcNow);
    }

    /// <summary>
    /// Merges notifications per day: likes and shares of the same post become one entry
    /// ("a, b and 3 others liked your post"), as do new followers; comments, mentions and
    /// moderator messages stay separate. Rows must be newest first.
    /// </summary>
    public static List<NotificationDay> Group(IEnumerable<NotificationRow> rows, DateTime nowUtc)
    {
        var days = new List<(DateTime Day, List<NotificationGroup> Groups, Dictionary<string, NotificationGroup> ByKey)>();
        foreach (var row in rows)
        {
            var day = row.CreatedAt.Date;
            if (days.Count == 0 || days[^1].Day != day) days.Add((day, [], []));
            var (_, groups, byKey) = days[^1];

            var key = row.Type switch
            {
                NotificationType.Like => $"like:{row.PostId}",
                NotificationType.Share => $"share:{row.TargetPostId ?? row.PostId}",
                NotificationType.Follow => "follow",
                _ => null,
            };
            if (key is null || !byKey.TryGetValue(key, out var group))
            {
                var isActivity = row.Type is NotificationType.Reply or NotificationType.Mention;
                group = new NotificationGroup
                {
                    Type = row.Type,
                    PostId = row.Type switch
                    {
                        NotificationType.Share or NotificationType.Reply => row.TargetPostId ?? row.PostId,
                        _ => row.PostId,
                    },
                    PostExcerpt = Excerpt(row.Type is NotificationType.Share or NotificationType.Reply ? row.TargetExcerpt : row.PostExcerpt),
                    ActivityPostId = isActivity ? row.PostId : null,
                    ActivityExcerpt = isActivity ? Excerpt(row.PostExcerpt, 280) : null,
                    Message = row.Message,
                    LatestAt = row.CreatedAt,
                };
                groups.Add(group);
                if (key is not null) byKey[key] = group;
            }
            if (!row.IsRead) group.IsUnread = true;
            if (row.Actor is not null && group.Actors.All(a => a.Id != row.Actor.Id)) group.Actors.Add(row.Actor);
        }

        return days.Select(d => new NotificationDay(DayLabel(d.Day, nowUtc), d.Groups)).ToList();
    }

    private static string DayLabel(DateTime day, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        if (day == today) return "today";
        if (day == today.AddDays(-1)) return "yesterday";
        return day.ToString(day.Year == today.Year ? "dddd, MMMM d" : "MMMM d, yyyy", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    /// <summary>A short plain-text line from a post's rendered HTML (possibly cut off mid-tag) or headline.</summary>
    public static string? Excerpt(string? html, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    // A tag, or the start of one cut off at the end.
    [GeneratedRegex("<[^>]*(>|$)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>One stored notification, flattened for grouping.</summary>
/// <param name="PostId">The post the notification links to (the liked post, the comment, the share, the mention).</param>
/// <param name="TargetPostId">For shares: the shared post; for comments: the post commented on.</param>
public record NotificationRow(
    int Id,
    NotificationType Type,
    bool IsRead,
    DateTime CreatedAt,
    UserSummary? Actor,
    int? PostId,
    int? TargetPostId,
    string? PostExcerpt,
    string? TargetExcerpt,
    string? Message);
