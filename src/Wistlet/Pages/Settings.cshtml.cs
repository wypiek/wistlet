using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

[Authorize]
[EnableRateLimiting("write")]
public class SettingsModel(AppDbContext db, AccountService accounts, UploadService uploads) : PageModel
{
    private static readonly string[] Tabs = ["profile", "account", "privacy", "blocked"];

    public string Tab { get; private set; } = "profile";
    public User Me { get; private set; } = null!;
    public List<UserSummary> Blocked { get; private set; } = [];

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

    public async Task<IActionResult> OnPostPrivacyAsync(MessagePrivacy messagePrivacy, bool showAdultContent)
    {
        var me = await LoadAsync();
        if (!Enum.IsDefined(messagePrivacy)) return Fail("privacy", "Unknown option.");
        me.MessagePrivacy = messagePrivacy;
        me.ShowAdultContent = showAdultContent;
        await db.SaveChangesAsync();
        return Done("privacy", "Privacy settings saved.");
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

        var images = await db.Posts.Where(p => p.AuthorId == me.Id && p.ImagePath != null).Select(p => p.ImagePath).ToListAsync();
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
