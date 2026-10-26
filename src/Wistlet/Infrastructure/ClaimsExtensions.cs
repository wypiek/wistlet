using System.Security.Claims;
using Wistlet.Models;

namespace Wistlet.Infrastructure;

public static class AppClaims
{
    public const string SecurityStamp = "wistlet:stamp";

    /// <summary>The login account; the name identifier is the page currently acted as (often the same).</summary>
    public const string Account = "wistlet:account";
}

public static class Policies
{
    public const string Staff = "Staff";
    public const string Admin = "Admin";
}

public static class ClaimsExtensions
{
    public static int? GetUserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int RequireUserId(this ClaimsPrincipal principal) =>
        principal.GetUserId() ?? throw new InvalidOperationException("No signed-in user.");

    /// <summary>The signed-in login account (as opposed to the page being acted as, which <see cref="GetUserId"/> returns).</summary>
    public static int? GetAccountId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(AppClaims.Account), out var id) ? id : principal.GetUserId();

    public static int RequireAccountId(this ClaimsPrincipal principal) =>
        principal.GetAccountId() ?? throw new InvalidOperationException("No signed-in user.");

    public static bool IsStaff(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Moderator)) || principal.IsInRole(nameof(UserRole.Admin));

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Admin));
}
