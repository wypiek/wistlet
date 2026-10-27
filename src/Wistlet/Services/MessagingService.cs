using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

public class MessagingService(AppDbContext db, SiteSettingsService settings)
{
    /// <summary>Returns null when <paramref name="senderId"/> may message <paramref name="recipient"/>, otherwise the reason why not.</summary>
    public async Task<string?> CannotMessageReasonAsync(int senderId, User recipient)
    {
        if (!settings.Current.AllowDirectMessages) return "Direct messages are turned off on this site.";
        if (recipient.Id == senderId) return "You can't message yourself.";
        if (recipient.IsRemote) return "Direct messages don't reach other Wistlet sites.";
        if (recipient.IsCurrentlyBanned) return "This account is suspended.";
        if (await db.Blocks.AnyAsync(b => (b.BlockerId == senderId && b.BlockedId == recipient.Id) || (b.BlockerId == recipient.Id && b.BlockedId == senderId)))
            return "You can't message this account.";

        // Anyone may reply in a conversation the other person started.
        if (await db.Messages.AnyAsync(m => m.SenderId == recipient.Id && m.RecipientId == senderId)) return null;

        return recipient.MessagePrivacy switch
        {
            MessagePrivacy.Everyone => null,
            MessagePrivacy.PeopleIFollow when await db.Follows.AnyAsync(f => f.FollowerId == recipient.Id && f.FolloweeId == senderId) => null,
            MessagePrivacy.PeopleIFollow => $"@{recipient.UserName} only accepts messages from people they follow.",
            _ => $"@{recipient.UserName} isn't accepting messages.",
        };
    }
}
