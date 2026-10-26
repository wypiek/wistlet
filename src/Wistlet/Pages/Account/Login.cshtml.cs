using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Services;

namespace Wistlet.Pages.Account;

[AllowGuests]
[EnableRateLimiting("auth")]
public class LoginModel(AccountService accounts, AppDbContext db) : PageModel
{
    [BindProperty, Required, Display(Name = "User name or e-mail")]
    public string Login { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty, Display(Name = "Keep me signed in")]
    public bool Remember { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(Fmt.SafeReturnUrl(ReturnUrl)) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var user = await accounts.FindByLoginAsync(Login);
        if (user is null || !accounts.VerifyPassword(user, Password))
        {
            ModelState.AddModelError("", "The user name or password is incorrect.");
            return Page();
        }
        if (user.IsCurrentlyBanned)
        {
            var until = user.BannedUntil is DateTime d ? $" until {Fmt.Date(d)}" : "";
            ModelState.AddModelError("", $"This account has been suspended{until}." +
                (string.IsNullOrWhiteSpace(user.BanReason) ? "" : $" Reason: {user.BanReason}"));
            return Page();
        }

        // Expired temporary bans clear themselves on the next successful login.
        if (user.IsBanned) { user.IsBanned = false; user.BannedUntil = null; user.BanReason = null; }
        user.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await AccountService.SignInAsync(HttpContext, user, Remember);
        return LocalRedirect(Fmt.SafeReturnUrl(ReturnUrl));
    }
}
