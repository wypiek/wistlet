using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Services;

namespace Wistlet.Pages.Account;

/// <summary>The second login step for accounts with two-factor authentication turned on.</summary>
[AllowGuests]
[EnableRateLimiting("auth")]
public class TwoFactorModel(AccountService accounts, TwoFactorService twoFactor, AppDbContext db, IMemoryCache cache, AuditService audit) : PageModel
{
    /// <summary>Wrong codes allowed per account within <see cref="FailureWindow"/>, on top of the per-address rate limit.</summary>
    private const int MaxFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    [BindProperty, Required, Display(Name = "Code")]
    public string Code { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(Fmt.SafeReturnUrl(ReturnUrl));
        return await accounts.GetTwoFactorLoginAsync(HttpContext) is null ? Expired() : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await accounts.GetTwoFactorLoginAsync(HttpContext) is not var (user, remember)) return Expired();
        if (!ModelState.IsValid) return Page();

        var failuresKey = $"2fa-failures:{user.Id}";
        var failures = cache.GetOrCreate(failuresKey, e => { e.AbsoluteExpirationRelativeToNow = FailureWindow; return new StrongBox<int>(); })!;
        if (failures.Value >= MaxFailures)
        {
            ModelState.AddModelError("", "Too many wrong codes. Wait a few minutes and try again.");
            return Page();
        }

        // Six digits (perhaps with a space in the middle) is an app code; anything else a recovery code.
        var isAppCode = Code.Count(char.IsAsciiDigit) == 6 && Code.Trim().Length <= 7;
        var usedRecoveryCode = !isAppCode && TwoFactorService.RedeemRecoveryCode(user, Code);
        if (!usedRecoveryCode && !(isAppCode && twoFactor.VerifyAndConsume(user, Code)))
        {
            Interlocked.Increment(ref failures.Value);
            ModelState.AddModelError("", "That code isn't right. Codes change every 30 seconds — check your device's clock if this keeps happening.");
            return Page();
        }

        cache.Remove(failuresKey);
        user.LastSeenAt = DateTime.UtcNow;
        if (usedRecoveryCode)
            audit.Log("user.2fa-recovery-code", $"@{user.UserName}", $"{TwoFactorService.RemainingRecoveryCodes(user)} recovery codes left");
        await db.SaveChangesAsync();

        AccountService.EndTwoFactorLogin(HttpContext);
        await AccountService.SignInAsync(HttpContext, user, remember);
        if (usedRecoveryCode)
            TempData["Flash"] = $"You used a recovery code; {TwoFactorService.RemainingRecoveryCodes(user)} are left. You can make new ones under Settings → Security.";
        return LocalRedirect(Fmt.SafeReturnUrl(ReturnUrl));
    }

    private IActionResult Expired()
    {
        TempData["Error"] = "Your login timed out. Please log in again.";
        return LocalRedirect("/account/login" + (ReturnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(ReturnUrl)));
    }
}
