using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;

namespace Wistlet.Infrastructure;

/// <summary>
/// Re-validates the auth cookie against the database on every request so that
/// bans, deletions, password changes and role changes take effect immediately.
/// </summary>
public class CookieEvents : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.GetUserId();
        var stamp = context.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
        if (id is null || stamp is null)
        {
            await Reject(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null || user.SecurityStamp != stamp || user.IsCurrentlyBanned)
        {
            await Reject(context);
            return;
        }

        if (user.LastSeenAt is null || user.LastSeenAt < DateTime.UtcNow.AddMinutes(-5))
        {
            user.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
