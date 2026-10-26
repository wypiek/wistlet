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

    /// <summary>Most pages one account can have, counting the account's own page.</summary>
    public const int MaxPagesPerAccount = 10;

    /// <summary>Adds an extra page to an account. It has no login of its own; the account switches to it.</summary>
    public async Task<(User? Page, string? Error)> CreatePageAsync(User account, string userName, string? displayName)
    {
        if (account.OwnerId is not null) return (null, "Switch to your main page to create pages.");
        if (await db.Users.CountAsync(u => u.OwnerId == account.Id) >= MaxPagesPerAccount - 1)
            return (null, $"An account can have at most {MaxPagesPerAccount} pages.");
        userName = userName.Trim();
        if (await ValidateNewUserNameAsync(userName) is { } error) return (null, error);
        displayName = displayName?.Trim();
        if (displayName is { Length: > 50 }) return (null, "Display names can be at most 50 characters.");
        var page = new User
        {
            UserName = userName,
            NormalizedUserName = Normalize(userName),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
            OwnerId = account.Id,
            Role = UserRole.User,
        };
        db.Users.Add(page);
        await db.SaveChangesAsync();
        return (page, null);
    }

    /// <summary>The pages an account can act as: its own page first, then its extra pages.</summary>
    public Task<List<User>> PagesOfAsync(int accountId) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == accountId || u.OwnerId == accountId)
            .OrderBy(u => u.OwnerId != null).ThenBy(u => u.Id)
            .ToListAsync();

    /// <summary>Deletes a page (for an account: with all of its extra pages) and every file they uploaded.</summary>
    public async Task DeleteUserAsync(User user, UploadService uploads)
    {
        var ids = await db.Users.Where(u => u.Id == user.Id || u.OwnerId == user.Id).Select(u => u.Id).ToListAsync();
        var files = await db.PostAttachments.Where(a => ids.Contains(a.Post.AuthorId)).Select(a => (string?)a.Path).ToListAsync();
        files.AddRange(await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.AvatarPath).ToListAsync());
        files.AddRange(await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.BannerPath).ToListAsync());
        db.Users.RemoveRange(await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync());
        await db.SaveChangesAsync();
        foreach (var f in files) uploads.Delete(f);
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
            return await db.Users.FirstOrDefaultAsync(u => u.OwnerId == null && u.Email.ToLower() == lower);
        }
        // Extra pages have no password of their own: you log in with the account that owns them.
        var user = await FindByUserNameAsync(login);
        return user is { OwnerId: null } ? user : null;
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

    /// <summary>Claims for a signed-in account acting as one of its pages (the account itself by default).</summary>
    public static ClaimsPrincipal CreatePrincipal(User account, User? page = null)
    {
        page ??= account;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, page.Id.ToString()),
            new(ClaimTypes.Name, page.UserName),
            new(AppClaims.Account, account.Id.ToString()),
            new(ClaimTypes.Role, account.Role.ToString()),
            new(AppClaims.SecurityStamp, account.SecurityStamp),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, User user, bool remember, User? page = null) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user, page),
            new AuthenticationProperties
            {
                IsPersistent = remember,
                ExpiresUtc = remember ? DateTimeOffset.UtcNow.AddDays(30) : null,
            });
}
