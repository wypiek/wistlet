using System.Security.Claims;
using Wistlet.Models;

namespace Wistlet.Infrastructure;

public static class AppClaims
{
    public const string SecurityStamp = "wistlet:stamp";
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

    public static bool IsStaff(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Moderator)) || principal.IsInRole(nameof(UserRole.Admin));

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Admin));
}
