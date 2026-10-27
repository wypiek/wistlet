using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;
using Wistlet.Services.Federation;

namespace Wistlet.Pages;

[Authorize]
[EnableRateLimiting("write")]
public class SettingsModel(AppDbContext db, AccountService accounts, UploadService uploads, TwoFactorService twoFactor,
    DataExportService export, SiteSettingsService settings, AuditService audit, IMemoryCache cache, FederationService federation) : PageModel
{
    private static readonly string[] Tabs = ["profile", "account", "display", "security", "privacy", "data", "blocked"];

    public string Tab { get; private set; } = "profile";
    public User Me { get; private set; } = null!;
    public List<UserSummary> Blocked { get; private set; } = [];

    /// <summary>While setting up two-factor login: the key to enter in the app, and the same as a QR code.</summary>
    public string? PendingKey { get; private set; }
    public string? PendingQrCode { get; private set; }

    /// <summary>Recovery codes, shown once right after they were made.</summary>
    public IReadOnlyList<string>? NewRecoveryCodes { get; private set; }

    public int RecoveryCodesLeft => TwoFactorService.RemainingRecoveryCodes(Me);

    private async Task<User> LoadAsync()
    {
        var id = User.RequireUserId();
        Me = await db.Users.FirstAsync(u => u.Id == id);
        return Me;
    }

    private IActionResult Done(string tab, string message)
    {
        TempData["Flash"] = message;
        return Redirect($"/settings?tab={tab}");
    }

    private IActionResult Fail(string tab, string message)
    {
        TempData["Error"] = message;
        return Redirect($"/settings?tab={tab}");
    }

    public async Task OnGetAsync(string? tab)
    {
        Tab = Tabs.Contains(tab) ? tab! : "profile";
        await LoadAsync();
        if (Tab == "security")
        {
            if (!Me.TwoFactorEnabled && twoFactor.Unprotect(Me.TwoFactorSecret) is { } key)
            {
                PendingKey = string.Join(' ', key.Chunk(4).Select(c => new string(c)));
                PendingQrCode = TwoFactorService.QrCodeDataUrl(TwoFactorService.AuthenticatorUri(settings.Current.SiteName, Me.UserName, key));
            }
            if (TempData["RecoveryCodes"] is string codes) NewRecoveryCodes = codes.Split(' ');
        }
        if (Tab == "blocked")
        {
            Blocked = await db.Blocks.Where(b => b.BlockerId == Me.Id)
                .Select(b => new UserSummary(b.Blocked.Id, b.Blocked.UserName, b.Blocked.DisplayName, b.Blocked.AvatarPath, b.Blocked.IsVerified, b.Blocked.Role, "", b.Blocked.AvatarShape))
                .ToListAsync();
        }
    }

    public async Task<IActionResult> OnPostProfileAsync(string? displayName, string? bio, string? location, string? website,
        IFormFile? avatar, bool removeAvatar, IFormFile? banner, bool removeBanner, string? pronouns, string? dek, string? avatarShape)
    {
        var me = await LoadAsync();
        displayName = (displayName ?? "").Trim();
        website = (website ?? "").Trim();
        if (displayName.Length is 0 or > 50) return Fail("profile", "Display names must be 1–50 characters.");
        if ((bio ?? "").Length > 5000) return Fail("profile", "Your description can be at most 5,000 characters.");
        if ((pronouns ?? "").Length > 40) return Fail("profile", "Pronouns can be at most 40 characters.");
        if ((dek ?? "").Length > 100) return Fail("profile", "The tagline can be at most 100 characters.");
        if ((location ?? "").Length > 60) return Fail("profile", "Location can be at most 60 characters.");
        if (website.Length > 0)
        {
            if (!website.Contains("://")) website = "https://" + website;
            if (website.Length > 200 || !Uri.TryCreate(website, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                return Fail("profile", "That website address doesn't look right.");
        }

        // Store both new images before touching the old ones, so a failed upload changes nothing.
        var (avatarUrl, avatarError) = await uploads.SaveImageAsync(avatar, "avatars", maxMbOverride: 2);
        if (avatarError is not null) return Fail("profile", avatarError);
        var (bannerUrl, bannerError) = await uploads.SaveImageAsync(banner, "banners", maxMbOverride: 4);
        if (bannerError is not null)
        {
            uploads.Delete(avatarUrl);
            return Fail("profile", bannerError);
        }

        if (avatarUrl is not null || removeAvatar)
        {
            uploads.Delete(me.AvatarPath);
            me.AvatarPath = avatarUrl;
        }
        if (bannerUrl is not null || removeBanner)
        {
            uploads.Delete(me.BannerPath);
            me.BannerPath = bannerUrl;
        }

        me.DisplayName = displayName;
        me.Bio = (bio ?? "").Trim();
        me.Location = (location ?? "").Trim();
        me.Website = website;
        me.Pronouns = (pronouns ?? "").Trim();
        me.Dek = (dek ?? "").Trim();
        me.AvatarShape = AvatarShapes.Resolve(avatarShape);
        await db.SaveChangesAsync();
        await federation.ProfileChangedAsync(me);
        return Done("profile", "Profile saved.");
    }

    public async Task<IActionResult> OnPostEmailAsync(string? email, string? currentPassword)
    {
        var me = await LoadAsync();
        email = (email ?? "").Trim();
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email) || email.Length is 0 or > 256)
            return Fail("account", "Please enter a valid e-mail address.");
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("account", "Your current password is incorrect.");
        me.Email = email;
        await db.SaveChangesAsync();
        await accounts.RevokeResetTokensAsync(me.Id);
        return Done("account", "E-mail address updated.");
    }

    public async Task<IActionResult> OnPostPasswordAsync(string? currentPassword, string? newPassword, string? confirmPassword)
    {
        var me = await LoadAsync();
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("account", "Your current password is incorrect.");
        if (AccountService.ValidatePassword(newPassword) is { } error)
            return Fail("account", error);
        if (newPassword != confirmPassword)
            return Fail("account", "The new passwords don't match.");

        accounts.SetPassword(me, newPassword!);
        await db.SaveChangesAsync();
        await accounts.RevokeResetTokensAsync(me.Id);
        // The security stamp changed, which signs out every other session; re-issue this one.
        await AccountService.SignInAsync(HttpContext, me, remember: true);
        return Done("account", "Password changed. You've been signed out everywhere else.");
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync()
    {
        var me = await LoadAsync();
        AccountService.RotateSecurityStamp(me);
        await db.SaveChangesAsync();
        await AccountService.SignInAsync(HttpContext, me, remember: true);
        return Done("account", "All other sessions have been signed out.");
    }

    public async Task<IActionResult> OnPostPrivacyAsync(MessagePrivacy messagePrivacy, bool showAdultContent, bool isAdultPage, bool hiddenFromGuests)
    {
        var me = await LoadAsync();
        if (!Enum.IsDefined(messagePrivacy)) return Fail("privacy", "Unknown option.");
        me.MessagePrivacy = messagePrivacy;
        me.ShowAdultContent = showAdultContent;
        me.IsAdultPage = isAdultPage;
        me.HiddenFromGuests = hiddenFromGuests;
        await db.SaveChangesAsync();
        await federation.ProfileChangedAsync(me);
        return Done("privacy", "Privacy settings saved.");
    }

    public async Task<IActionResult> OnPostDisplayAsync(string? theme, string? timelineMode)
    {
        var me = await LoadAsync();
        me.Theme = Themes.Resolve(theme);
        me.TimelineMode = TimelineModes.Resolve(timelineMode);
        await db.SaveChangesAsync();
        return Done("display", "Display settings saved.");
    }

    /// <summary>First step of two-factor setup: a new key to scan, not yet switched on.</summary>
    public async Task<IActionResult> OnPostTwoFactorStartAsync()
    {
        var me = await LoadAsync();
        if (me.TwoFactorEnabled) return Fail("security", "Two-factor login is already on.");
        me.TwoFactorSecret = twoFactor.Protect(TwoFactorService.GenerateKey());
        await db.SaveChangesAsync();
        return Redirect("/settings?tab=security");
    }

    public async Task<IActionResult> OnPostTwoFactorEnableAsync(string? code, string? currentPassword)
    {
        var me = await LoadAsync();
        if (me.TwoFactorEnabled) return Fail("security", "Two-factor login is already on.");
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("security", "Your current password is incorrect.");
        if (!twoFactor.VerifyAndConsume(me, code))
            return Fail("security", "That code didn't match. Check that your app shows codes for this account and try the newest one.");

        me.TwoFactorEnabled = true;
        var codes = TwoFactorService.ResetRecoveryCodes(me);
        // Other sessions were opened without the second factor.
        AccountService.RotateSecurityStamp(me);
        audit.Log("user.2fa-enable", $"@{me.UserName}");
        await db.SaveChangesAsync();
        await AccountService.SignInAsync(HttpContext, me, remember: true);
        TempData["RecoveryCodes"] = string.Join(' ', codes);
        return Done("security", "Two-factor login is on. Save your recovery codes now — they're only shown once.");
    }

    public async Task<IActionResult> OnPostTwoFactorCancelAsync()
    {
        var me = await LoadAsync();
        if (!me.TwoFactorEnabled)
        {
            TwoFactorService.Disable(me);
            await db.SaveChangesAsync();
        }
        return Redirect("/settings?tab=security");
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync(string? currentPassword)
    {
        var me = await LoadAsync();
        if (!me.TwoFactorEnabled) return Fail("security", "Two-factor login is off.");
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("security", "Your current password is incorrect.");
        var codes = TwoFactorService.ResetRecoveryCodes(me);
        await db.SaveChangesAsync();
        TempData["RecoveryCodes"] = string.Join(' ', codes);
        return Done("security", "New recovery codes made; the old ones no longer work.");
    }

    public async Task<IActionResult> OnPostTwoFactorDisableAsync(string? currentPassword)
    {
        var me = await LoadAsync();
        if (!me.TwoFactorEnabled) return Fail("security", "Two-factor login is already off.");
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("security", "Your current password is incorrect.");
        TwoFactorService.Disable(me);
        audit.Log("user.2fa-disable", $"@{me.UserName}");
        await db.SaveChangesAsync();
        return Done("security", "Two-factor login is off.");
    }

    /// <summary>Streams a zip of the user's data (see <see cref="DataExportService"/>).</summary>
    public async Task<IActionResult> OnPostExportAsync()
    {
        var me = await LoadAsync();
        // Exports read every file the user uploaded, so allow one a minute.
        var key = $"export:{me.Id}";
        if (cache.TryGetValue(key, out _))
            return Fail("data", "You just downloaded your data. Please wait a minute before trying again.");
        cache.Set(key, true, TimeSpan.FromMinutes(1));

        var name = $"{settings.Current.SiteName}-{me.UserName}-{DateTime.UtcNow:yyyy-MM-dd}.zip".ToLowerInvariant();
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
        await export.WriteZipAsync(me.Id, Response.Body, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    public async Task<IActionResult> OnPostDeleteAccountAsync(string? currentPassword, string? confirm)
    {
        var me = await LoadAsync();
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("account", "Your current password is incorrect.");
        if (!string.Equals(confirm?.Trim(), me.UserName, StringComparison.OrdinalIgnoreCase))
            return Fail("account", "Type your user name to confirm deleting your account.");
        if (me.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin) == 1)
            return Fail("account", "You're the only administrator. Promote someone else before deleting your account.");

        await federation.AccountDeletingAsync(me);
        var images = await PostService.AttachmentsAsync(db.Posts.Where(p => p.AuthorId == me.Id));
        images.Add(me.AvatarPath);
        images.Add(me.BannerPath);
        db.Users.Remove(me);
        await db.SaveChangesAsync();
        foreach (var img in images) uploads.Delete(img);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["Flash"] = "Your account has been deleted. Goodbye!";
        return Redirect("/");
    }
}
