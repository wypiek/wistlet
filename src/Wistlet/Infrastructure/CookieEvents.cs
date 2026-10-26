using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Services;

namespace Wistlet.Infrastructure;

/// <summary>
/// Re-validates the auth cookie against the database on every request so that
/// bans, deletions, password changes and role changes take effect immediately.
/// </summary>
public class CookieEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var accountId = context.Principal?.GetAccountId();
        var pageId = context.Principal?.GetUserId();
        var stamp = context.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
        if (accountId is null || pageId is null || stamp is null)
        {
            await Reject(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var account = await db.Users.FirstOrDefaultAsync(u => u.Id == accountId);
        if (account is null || account.OwnerId is not null || account.SecurityStamp != stamp || account.IsCurrentlyBanned)
        {
            await Reject(context);
            return;
        }

        var page = account;
        if (pageId != accountId)
        {
            // The page acted as must still exist, belong to the account and not be banned; otherwise fall back to the account.
            page = await db.Users.FirstOrDefaultAsync(u => u.Id == pageId && u.OwnerId == account.Id);
            if (page is null || page.IsCurrentlyBanned)
            {
                context.ReplacePrincipal(AccountService.CreatePrincipal(account));
                context.ShouldRenew = true;
                page = account;
            }
        }

        var now = DateTime.UtcNow;
        var touched = false;
        foreach (var u in page == account ? [account] : new[] { account, page })
        {
            if (u.LastSeenAt is null || u.LastSeenAt < now.AddMinutes(-5))
            {
                u.LastSeenAt = now;
                touched = true;
            }
        }
        if (touched) await db.SaveChangesAsync();
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
