using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Account;

[AllowGuests]
[EnableRateLimiting("auth")]
public class RegisterModel(AccountService accounts, SiteSettingsService settings, AuditService audit, AppDbContext db) : PageModel
{
    [BindProperty, Required, Display(Name = "User name")]
    public string UserName { get; set; } = "";

    [BindProperty, StringLength(50), Display(Name = "Display name")]
    public string? DisplayName { get; set; }

    [BindProperty, Required, EmailAddress, StringLength(256), Display(Name = "E-mail")]
    public string Email { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords don't match."), Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public bool Closed => !settings.Current.RegistrationOpen;

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed) return Page();

        UserName = UserName.Trim();
        if (await accounts.ValidateNewUserNameAsync(UserName) is { } nameError)
            ModelState.AddModelError(nameof(UserName), nameError);
        if (AccountService.ValidatePassword(Password) is { } pwError)
            ModelState.AddModelError(nameof(Password), pwError);
        if (!ModelState.IsValid) return Page();

        var user = await accounts.CreateUserAsync(UserName, Email, Password, DisplayName);
        audit.Log("user.register", $"@{user.UserName}", user.Role == UserRole.Admin ? "First account — granted admin." : null);
        await db.SaveChangesAsync();

        await AccountService.SignInAsync(HttpContext, user, remember: true);
        TempData["Flash"] = user.Role == UserRole.Admin
            ? $"Welcome, @{user.UserName}! As the first user you're the administrator — the Control Panel is in the Start menu."
            : $"Welcome to {settings.Current.SiteName}, @{user.UserName}!";
        return LocalRedirect(Fmt.SafeReturnUrl(ReturnUrl, "/people"));
    }
}
