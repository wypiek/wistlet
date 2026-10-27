using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Services;

public partial class AccountService(AppDbContext db, IPasswordHasher<User> hasher, IDataProtectionProvider protection)
{
    public const int MinPasswordLength = 8;

    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>How long someone has to enter their two-factor code after giving the right password.</summary>
    private static readonly TimeSpan TwoFactorWindow = TimeSpan.FromMinutes(10);

    private const string TwoFactorCookie = "wistlet.2fa";

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

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a single-use password reset token and returns it. Only its hash is stored, and only the newest one works.</summary>
    public async Task<string> CreateResetTokenAsync(User user)
    {
        var now = DateTime.UtcNow;
        await db.PasswordResetTokens.Where(t => t.UserId == user.Id || t.ExpiresAt < now).ExecuteDeleteAsync();
        var token = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = HashToken(token), ExpiresAt = now + ResetTokenLifetime });
        await db.SaveChangesAsync();
        return token;
    }

    /// <summary>The unused, unexpired reset token matching <paramref name="token"/>, with its user.</summary>
    public async Task<PasswordResetToken?> FindResetTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = HashToken(token);
        var now = DateTime.UtcNow;
        return await db.PasswordResetTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now);
    }

    /// <summary>Sets the new password, uses up the token and signs the account out everywhere.</summary>
    public async Task ResetPasswordAsync(PasswordResetToken token, string newPassword)
    {
        SetPassword(token.User, newPassword);
        token.UsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await RevokeResetTokensAsync(token.UserId);
    }

    /// <summary>Invalidates outstanding reset links, e.g. after the password or e-mail address changed.</summary>
    public Task RevokeResetTokensAsync(int userId) =>
        db.PasswordResetTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();

    private IDataProtector TwoFactorProtector => protection.CreateProtector("Wistlet.TwoFactorLogin.v1");

    /// <summary>
    /// Remembers, in a short-lived encrypted cookie, who passed the password step and is now
    /// being asked for their two-factor code.
    /// </summary>
    public void BeginTwoFactorLogin(HttpContext http, User user, bool remember)
    {
        var expires = DateTimeOffset.UtcNow + TwoFactorWindow;
        var payload = $"{user.Id}|{user.SecurityStamp}|{(remember ? 1 : 0)}|{expires.ToUnixTimeSeconds()}";
        http.Response.Cookies.Append(TwoFactorCookie, TwoFactorProtector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Expires = expires,
            Path = "/account",
        });
    }

    /// <summary>The user waiting for the two-factor step, or null if there is none (or it expired).</summary>
    public async Task<(User User, bool Remember)?> GetTwoFactorLoginAsync(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(TwoFactorCookie, out var cookie)) return null;
        string payload;
        try { payload = TwoFactorProtector.Unprotect(cookie); }
        catch (CryptographicException) { return null; }

        var parts = payload.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var id) || !long.TryParse(parts[3], out var expires)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        // A password change since then (or a reset) cancels the pending login.
        if (user is null || user.SecurityStamp != parts[1] || !user.TwoFactorEnabled) return null;
        return (user, parts[2] == "1");
    }

    public static void EndTwoFactorLogin(HttpContext http) =>
        http.Response.Cookies.Delete(TwoFactorCookie, new CookieOptions { Path = "/account" });

    public static Task SignInAsync(HttpContext http, User user, bool remember) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user),
            new AuthenticationProperties
            {
                IsPersistent = remember,
                ExpiresUtc = remember ? DateTimeOffset.UtcNow.AddDays(30) : null,
            });
}
