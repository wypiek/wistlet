using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>Sends an ask to a page. Works signed out too when the page allows it (the ask is then anonymous).</summary>
[EnableRateLimiting("ask")]
public class AskModel(AppDbContext db, SiteSettingsService settings) : PageModel
{
    public const int MaxLength = 1000;

    public User Recipient { get; private set; } = null!;
    public bool MustBeAnonymous { get; private set; }

    private async Task<(User? User, string? Error)> LoadAsync(string userName)
    {
        var normalized = AccountService.Normalize(userName);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user is null || user.IsCurrentlyBanned) return (null, null);
        Recipient = user;
        var me = User.GetUserId();
        MustBeAnonymous = me is null;
        if (!user.AsksEnabled) return (user, $"@{user.UserName} isn't accepting asks.");
        if (me == user.Id) return (user, "You can't send yourself an ask.");
        if (me is null && !user.LoggedOutAsksAllowed) return (user, $"Log in to ask @{user.UserName} something.");
        if (me is int id && await db.Blocks.AnyAsync(b => (b.BlockerId == id && b.BlockedId == user.Id) || (b.BlockerId == user.Id && b.BlockedId == id)))
            return (user, $"You can't send asks to @{user.UserName}.");
        return (user, null);
    }

    public async Task<IActionResult> OnGetAsync(string userName)
    {
        var (user, error) = await LoadAsync(userName);
        if (user is null) return NotFound();
        if (error is not null) TempData["Error"] = error;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string userName, string? content, bool anonymous)
    {
        var (user, error) = await LoadAsync(userName);
        if (user is null) return NotFound();
        var back = $"/u/{user.UserName}";
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect(back);
        }

        content = (content ?? "").Trim().Replace("\r\n", "\n");
        if (content.Length == 0) error = "Write your ask first.";
        else if (content.Length > MaxLength) error = $"Asks can be at most {MaxLength:N0} characters.";
        else if (settings.Current.GetBannedWords().Any(w => content.Contains(w, StringComparison.OrdinalIgnoreCase)))
            error = "Your ask contains a word that isn't allowed on this site.";
        else if (anonymous && !MustBeAnonymous && !user.AnonAsksAllowed) error = $"@{user.UserName} doesn't accept anonymous asks.";
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect(back);
        }

        db.Asks.Add(new Ask
        {
            RecipientId = user.Id,
            SenderId = User.GetUserId(),
            IsAnonymous = anonymous || MustBeAnonymous,
            Content = content,
        });
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Ask sent! If @{user.UserName} answers it, it'll show up on their page.";
        return Redirect(back);
    }
}
