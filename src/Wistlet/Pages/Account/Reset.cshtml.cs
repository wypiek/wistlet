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
public class ResetModel(AccountService accounts, AuditService audit, AppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty, Required, DataType(DataType.Password), Display(Name = "New password")]
    public string Password { get; set; } = "";

    [BindProperty, Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords don't match."), Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";

    public bool Valid { get; private set; }
    public string UserName { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var token = await accounts.FindResetTokenAsync(Token);
        Valid = token is not null;
        UserName = token?.User.UserName ?? "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var token = await accounts.FindResetTokenAsync(Token);
        Valid = token is not null;
        if (token is null) return Page();
        UserName = token.User.UserName;

        if (AccountService.ValidatePassword(Password) is { } pwError)
            ModelState.AddModelError(nameof(Password), pwError);
        if (!ModelState.IsValid) return Page();

        await accounts.ResetPasswordAsync(token, Password);
        audit.Log("user.password-reset-by-email", $"@{token.User.UserName}");
        await db.SaveChangesAsync();

        // Not signed in here: accounts with two-factor login still need their code.
        TempData["Flash"] = "Your password has been changed. Log in with your new password.";
        return LocalRedirect("/account/login");
    }
}
