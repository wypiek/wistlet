using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Account;

[AllowGuests]
[EnableRateLimiting("auth")]
public class ForgotModel(AccountService accounts, EmailService email, SiteSettingsService settings, AppDbContext db, ILogger<ForgotModel> logger) : PageModel
{
    [BindProperty, Required, StringLength(256), Display(Name = "User name or e-mail")]
    public string Login { get; set; } = "";

    public bool Available => email.IsAvailable;
    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!Available) return Page();
        if (!ModelState.IsValid) return Page();

        // Several accounts may share an address; each gets its own link.
        var login = Login.Trim();
        List<User> users;
        if (login.Contains('@'))
        {
            var lower = login.ToLowerInvariant();
            users = await db.Users.Where(u => u.Email.ToLower() == lower && u.Domain == null).Take(5).ToListAsync();
        }
        else
        {
            users = await accounts.FindByUserNameAsync(login) is { } u ? [u] : [];
        }

        foreach (var user in users.Where(u => !u.IsCurrentlyBanned && u.Email.Contains('@')))
        {
            var token = await accounts.CreateResetTokenAsync(user);
            var link = email.AbsoluteUrl(Request, $"/account/reset?token={Uri.EscapeDataString(token)}");
            var site = settings.Current.SiteName;
            try
            {
                await email.SendAsync(user.Email, $"Reset your {site} password",
                    $"Hi @{user.UserName},\n\n" +
                    $"Someone (hopefully you) asked to reset the password for your {site} account. " +
                    $"To choose a new password, open this link within {(int)AccountService.ResetTokenLifetime.TotalMinutes} minutes:\n\n" +
                    $"{link}\n\n" +
                    "If you didn't ask for this, you can ignore this e-mail; your password stays as it is.\n");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not send the password reset e-mail for user {UserId}", user.Id);
                ModelState.AddModelError("", "We couldn't send the e-mail just now. Please try again later.");
                return Page();
            }
        }

        // The same answer whether or not an account matched, so this can't be used to find accounts.
        Sent = true;
        return Page();
    }
}
