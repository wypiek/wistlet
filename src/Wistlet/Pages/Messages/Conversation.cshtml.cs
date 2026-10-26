using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Messages;

[Authorize]
[EnableRateLimiting("write")]
public class ConversationModel(AppDbContext db, MessagingService messaging, SiteSettingsService settings) : PageModel
{
    public User Other { get; private set; } = null!;
    public List<Message> Messages { get; private set; } = [];
    public string? CannotSendReason { get; private set; }
    public int Me => User.RequireUserId();

    [BindProperty, Required, StringLength(2000)]
    public string Body { get; set; } = "";

    private async Task<bool> LoadAsync(string userName)
    {
        if (!settings.Current.AllowDirectMessages) return false;
        var normalized = AccountService.Normalize(userName);
        var other = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (other is null || other.Id == Me) return false;
        Other = other;
        CannotSendReason = await messaging.CannotMessageReasonAsync(Me, other);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        var me = Me;
        Messages = await db.Messages.AsNoTracking()
            .Where(m => (m.SenderId == me && m.RecipientId == Other.Id) || (m.SenderId == Other.Id && m.RecipientId == me))
            .OrderByDescending(m => m.Id).Take(200)
            .ToListAsync();
        Messages.Reverse();

        await db.Messages
            .Where(m => m.SenderId == Other.Id && m.RecipientId == me && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, DateTime.UtcNow));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        Body = Body.Trim();
        if (CannotSendReason is not null)
        {
            TempData["Error"] = CannotSendReason;
        }
        else if (Body.Length is 0 or > 2000)
        {
            TempData["Error"] = "Messages must be between 1 and 2000 characters.";
        }
        else
        {
            db.Messages.Add(new Message { SenderId = Me, RecipientId = Other.Id, Body = Body });
            await db.SaveChangesAsync();
        }
        return Redirect($"/messages/{Other.UserName}");
    }
}
