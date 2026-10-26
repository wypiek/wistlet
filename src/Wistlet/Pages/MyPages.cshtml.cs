using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>
/// An account's pages (cohost's projects): switch which page you act as, add pages, delete extra ones.
/// Posts, follows, likes, asks, messages and notifications all belong to the page you're acting as.
/// </summary>
[Authorize]
[EnableRateLimiting("write")]
public class MyPagesModel(AppDbContext db, AccountService accounts, UploadService uploads) : PageModel
{
    public record PageItem(User Page, int UnreadNotifications, int PendingAsks);

    public List<PageItem> Items { get; private set; } = [];
    public int CurrentId { get; private set; }
    public int AccountId { get; private set; }

    public async Task OnGetAsync()
    {
        AccountId = User.RequireAccountId();
        CurrentId = User.RequireUserId();
        var pages = await accounts.PagesOfAsync(AccountId);
        foreach (var p in pages)
        {
            Items.Add(new PageItem(p,
                await db.Notifications.CountAsync(n => n.UserId == p.Id && !n.IsRead),
                await db.Asks.CountAsync(a => a.RecipientId == p.Id && !a.IsAnswered)));
        }
    }

    /// <summary>Switches the page this browser acts as.</summary>
    public async Task<IActionResult> OnPostSwitchAsync(int id, string? returnUrl)
    {
        var accountId = User.RequireAccountId();
        var account = await db.Users.FirstAsync(u => u.Id == accountId);
        var page = id == accountId ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.OwnerId == accountId);
        if (id != accountId && page is null) return NotFound();
        if (page is { IsCurrentlyBanned: true })
        {
            TempData["Error"] = $"@{page.UserName} is banned.";
            return Redirect("/pages");
        }

        // Keep "remember me" as it was when switching.
        var auth = await HttpContext.AuthenticateAsync();
        await AccountService.SignInAsync(HttpContext, account, auth.Properties?.IsPersistent == true, page);
        TempData["Flash"] = $"You're now posting as @{(page ?? account).UserName}.";
        return LocalRedirect(Fmt.SafeReturnUrl(returnUrl, "/"));
    }

    public async Task<IActionResult> OnPostCreateAsync(string? userName, string? displayName)
    {
        var account = await db.Users.FirstAsync(u => u.Id == User.RequireAccountId());
        var (page, error) = await accounts.CreatePageAsync(account, userName ?? "", displayName);
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect("/pages");
        }
        var auth = await HttpContext.AuthenticateAsync();
        await AccountService.SignInAsync(HttpContext, account, auth.Properties?.IsPersistent == true, page);
        TempData["Flash"] = $"Created @{page!.UserName}. You're now posting as it; set it up below.";
        return Redirect("/settings");
    }

    /// <summary>Deletes one extra page (never the account's own page) after checking the account password.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, string? currentPassword, string? confirm)
    {
        var accountId = User.RequireAccountId();
        var account = await db.Users.FirstAsync(u => u.Id == accountId);
        var page = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.OwnerId == accountId);
        if (page is null) return NotFound();
        if (!accounts.VerifyPassword(account, currentPassword ?? ""))
        {
            TempData["Error"] = "Your current password is incorrect.";
            return Redirect("/pages");
        }
        if (!string.Equals(confirm?.Trim(), page.UserName, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = $"Type @{page.UserName}'s handle to confirm deleting it.";
            return Redirect("/pages");
        }

        var wasCurrent = User.GetUserId() == page.Id;
        await accounts.DeleteUserAsync(page, uploads);
        if (wasCurrent)
        {
            var auth = await HttpContext.AuthenticateAsync();
            await AccountService.SignInAsync(HttpContext, account, auth.Properties?.IsPersistent == true);
        }
        TempData["Flash"] = $"@{page.UserName} was deleted.";
        return Redirect("/pages");
    }
}
