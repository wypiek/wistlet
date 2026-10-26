using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Messages;

[Authorize]
public class IndexModel(AppDbContext db, SiteSettingsService settings) : PageModel
{
    public record Conversation(UserSummary With, string LastBody, DateTime LastAt, bool LastIsMine, int Unread);

    public List<Conversation> Conversations { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!settings.Current.AllowDirectMessages) return NotFound();
        var me = User.RequireUserId();

        var latest = await db.Messages
            .Where(m => m.SenderId == me || m.RecipientId == me)
            .GroupBy(m => m.SenderId == me ? m.RecipientId : m.SenderId)
            .Select(g => new { Other = g.Key, LastId = g.Max(m => m.Id) })
            .ToListAsync();
        var ids = latest.Select(x => x.LastId).ToList();

        var messages = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.SenderId, m.RecipientId, m.Body, m.CreatedAt })
            .ToListAsync();
        var otherIds = latest.Select(x => x.Other).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => otherIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.UserName, u.DisplayName, u.AvatarPath, u.IsVerified, u.Role, "", u.AvatarShape))
            .ToDictionaryAsync(u => u.Id);
        var unread = await db.Messages
            .Where(m => m.RecipientId == me && m.ReadAt == null)
            .GroupBy(m => m.SenderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        Conversations = messages
            .Select(m =>
            {
                var other = m.SenderId == me ? m.RecipientId : m.SenderId;
                return users.TryGetValue(other, out var u)
                    ? new Conversation(u, m.Body, m.CreatedAt, m.SenderId == me, unread.GetValueOrDefault(other))
                    : null;
            })
            .OfType<Conversation>()
            .OrderByDescending(c => c.LastAt)
            .ToList();
        return Page();
    }

    public IActionResult OnPostStart(string? userName)
    {
        userName = (userName ?? "").Trim().TrimStart('@');
        return userName.Length == 0 ? RedirectToPage() : Redirect($"/messages/{Uri.EscapeDataString(userName)}");
    }
}
