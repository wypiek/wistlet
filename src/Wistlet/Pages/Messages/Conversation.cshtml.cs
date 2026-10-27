using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Messages;

[Authorize]
[EnableRateLimiting("write")]
public class ConversationModel(AppDbContext db, MessagingService messaging, SiteSettingsService settings, LiveEvents live, IMemoryCache cache) : PageModel
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
        LastReadByOther = Messages.LastOrDefault(m => m.SenderId == me && m.ReadAt != null)?.Id;

        await MarkReadAsync();
        return Page();
    }

    /// <summary>The newest of my messages the other person has read, for the "seen" line.</summary>
    public int? LastReadByOther { get; private set; }

    /// <summary>Sending without JavaScript: a normal form post.</summary>
    public async Task<IActionResult> OnPostAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        var (_, error) = await SendAsync();
        if (error is not null) TempData["Error"] = error;
        return Redirect($"/messages/{Other.UserName}");
    }

    /// <summary>Sending from the page's script: answers with the new message, or the reason it wasn't sent.</summary>
    public async Task<IActionResult> OnPostSendAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        var (message, error) = await SendAsync();
        return error is not null
            ? new JsonResult(new { error }) { StatusCode = StatusCodes.Status400BadRequest }
            : new JsonResult(MessageEvent(message!, Other.UserName, mine: true));
    }

    /// <summary>The page saw new messages arrive while open.</summary>
    public async Task<IActionResult> OnPostReadAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        await MarkReadAsync();
        return new NoContentResult();
    }

    /// <summary>Tells the other person "… is typing". At most one every two seconds goes through.</summary>
    public async Task<IActionResult> OnPostTypingAsync(string userName)
    {
        if (!await LoadAsync(userName)) return NotFound();
        var key = $"typing:{Me}:{Other.Id}";
        if (CannotSendReason is null && !cache.TryGetValue(key, out _))
        {
            cache.Set(key, true, TimeSpan.FromSeconds(2));
            live.Publish(Other.Id, "typing", new { with = User.Identity!.Name });
        }
        return new NoContentResult();
    }

    private async Task<(Message? Message, string? Error)> SendAsync()
    {
        Body = (Body ?? "").Trim();
        if (CannotSendReason is not null) return (null, CannotSendReason);
        if (Body.Length is 0 or > 2000) return (null, "Messages must be between 1 and 2000 characters.");

        var message = new Message { SenderId = Me, RecipientId = Other.Id, Body = Body };
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var unread = await db.Messages.CountAsync(m => m.RecipientId == Other.Id && m.ReadAt == null);
        live.Publish(Other.Id, "dm", MessageEvent(message, User.Identity!.Name!, mine: false, unread));
        // The sender's other tabs show it too.
        live.Publish(Me, "dm", MessageEvent(message, Other.UserName, mine: true));
        return (message, null);
    }

    /// <summary>Marks the other person's messages to me as read, and tells them and my other tabs.</summary>
    private async Task MarkReadAsync()
    {
        var me = Me;
        var lastId = await db.Messages.Where(m => m.SenderId == Other.Id && m.RecipientId == me && m.ReadAt == null)
            .MaxAsync(m => (int?)m.Id);
        if (lastId is null) return;
        await db.Messages
            .Where(m => m.SenderId == Other.Id && m.RecipientId == me && m.ReadAt == null && m.Id <= lastId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, DateTime.UtcNow));
        live.Publish(Other.Id, "read", new { with = User.Identity!.Name, upTo = lastId });
        live.Publish(me, "unread", new { count = await db.Messages.CountAsync(m => m.RecipientId == me && m.ReadAt == null) });
    }

    /// <summary>
    /// What a tab needs to show a message: <c>with</c> is the other person in the conversation, as
    /// seen by whoever gets the event, and <c>html</c> is the body formatted like on the page.
    /// </summary>
    private object MessageEvent(Message m, string with, bool mine, int? unread = null)
    {
        using var writer = new StringWriter();
        ContentFormatter.Format(m.Body).WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return new
        {
            id = m.Id,
            with,
            mine,
            html = writer.ToString(),
            at = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
            preview = m.Body.Length > 80 ? m.Body[..80] + "…" : m.Body,
            unread,
        };
    }
}
