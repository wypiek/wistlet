using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Services;

public partial class AccountService(AppDbContext db, IPasswordHasher<User> hasher)
{
    public const int MinPasswordLength = 8;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "root", "system", "support", "moderator", "mod", "staff",
        "wistlet", "api", "settings", "explore", "notifications", "messages", "account",
        "login", "logout", "register", "about", "help", "null", "undefined",
    };

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex UserNameRegex();

    public static string Normalize(string userName) => userName.Trim().ToLowerInvariant();

    public async Task<string?> ValidateNewUserNameAsync(string userName, bool allowReserved = false)
    {
        if (!UserNameRegex().IsMatch(userName))
            return "User names must be 3–20 characters: letters, numbers and underscores only.";
        if (!allowReserved && ReservedNames.Contains(userName))
            return "That user name is reserved.";
        var normalized = Normalize(userName);
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized))
            return "That user name is already taken.";
        return null;
    }

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength
            ? $"Passwords must be at least {MinPasswordLength} characters long."
            : null;

    public async Task<User> CreateUserAsync(string userName, string email, string password, string? displayName = null, UserRole? role = null)
    {
        // The first account registered on a fresh install becomes the administrator.
        // (The admin panel refuses to remove the last admin, so this only happens once.)
        var isFirst = role is null && !await db.Users.AnyAsync(u => u.Role == UserRole.Admin);
        var user = new User
        {
            UserName = userName.Trim(),
            NormalizedUserName = Normalize(userName),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            Email = email.Trim(),
            Role = role ?? (isFirst ? UserRole.Admin : UserRole.User),
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public Task<User?> FindByUserNameAsync(string userName)
    {
        var normalized = Normalize(userName);
        return db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
    }

    /// <summary>Accepts either a user name or an e-mail address.</summary>
    public async Task<User?> FindByLoginAsync(string login)
    {
        login = login.Trim();
        if (login.Contains('@'))
        {
            var lower = login.ToLowerInvariant();
            return await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == lower);
        }
        return await FindByUserNameAsync(login);
    }

    public bool VerifyPassword(User user, string password)
    {
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, password);
        return result != PasswordVerificationResult.Failed;
    }

    public void SetPassword(User user, string password)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        RotateSecurityStamp(user);
    }

    public static void RotateSecurityStamp(User user) => user.SecurityStamp = Guid.NewGuid().ToString("N");

    public static string GenerateTemporaryPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(14, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }

    public static ClaimsPrincipal CreatePrincipal(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AppClaims.SecurityStamp, user.SecurityStamp),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, User user, bool remember) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user),
            new AuthenticationProperties
            {
                IsPersistent = remember,
                ExpiresUtc = remember ? DateTimeOffset.UtcNow.AddDays(30) : null,
            });
}
