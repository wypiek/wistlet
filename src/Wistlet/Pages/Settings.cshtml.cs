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
    private static readonly string[] Tabs = ["profile", "account", "privacy", "tags", "blocked"];

    public string Tab { get; private set; } = "profile";
    public User Me { get; private set; } = null!;
    public List<UserSummary> Blocked { get; private set; } = [];
    public List<ContactCardItem> ContactCard { get; private set; } = [];

    /// <summary>The viewer's tag preferences, by kind.</summary>
    public ILookup<TagPreferenceKind, string> TagPrefs { get; private set; } = Enumerable.Empty<TagPreference>().ToLookup(t => t.Kind, t => t.Tag);

    /// <summary>The login account (e-mail, password, sessions); <see cref="Me"/> is the page being edited.</summary>
    public User Account { get; private set; } = null!;

    /// <summary>The page currently acted as, whose profile and preferences these settings change.</summary>
    private async Task<User> LoadAsync()
    {
        var id = User.RequireUserId();
        Me = await db.Users.FirstAsync(u => u.Id == id);
        return Me;
    }

    private async Task<User> LoadAccountAsync()
    {
        var id = User.RequireAccountId();
        Account = await db.Users.FirstAsync(u => u.Id == id);
        return Account;
    }

    /// <summary>Re-issues this browser's cookie after the security stamp changed, staying on the same page.</summary>
    private async Task ReSignInAsync(User account)
    {
        var pageId = User.RequireUserId();
        var page = pageId == account.Id ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == pageId && u.OwnerId == account.Id);
        await AccountService.SignInAsync(HttpContext, account, remember: true, page);
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
        await LoadAccountAsync();
        ContactCard = ContactCardItem.Parse(Me.ContactCard);
        if (Tab == "tags")
        {
            var prefs = await db.TagPreferences.AsNoTracking().Where(t => t.UserId == Me.Id).OrderBy(t => t.Tag).ToListAsync();
            TagPrefs = prefs.ToLookup(t => t.Kind, t => t.Tag);
        }
        if (Tab == "blocked")
        {
            Blocked = await db.Blocks.Where(b => b.BlockerId == Me.Id)
                .Select(b => new UserSummary(b.Blocked.Id, b.Blocked.UserName, b.Blocked.DisplayName, b.Blocked.AvatarPath, b.Blocked.IsVerified, b.Blocked.Role, "", b.Blocked.AvatarShape))
                .ToListAsync();
        }
    }

    public async Task<IActionResult> OnPostProfileAsync(string? displayName, string? bio, string? location, string? website,
        IFormFile? avatar, bool removeAvatar, IFormFile? banner, bool removeBanner, string? pronouns, string? dek, string? avatarShape,
        List<string>? contactService, List<string>? contactValue, List<string>? contactPrivate, string? pinnedTags)
    {
        var me = await LoadAsync();
        displayName = (displayName ?? "").Trim();
        website = (website ?? "").Trim();
        if (displayName.Length is 0 or > 50) return Fail("profile", "Display names must be 1–50 characters.");
        if ((bio ?? "").Length > 5000) return Fail("profile", "Your description can be at most 5,000 characters.");
        if ((pronouns ?? "").Length > 40) return Fail("profile", "Pronouns can be at most 40 characters.");
        if ((dek ?? "").Length > 100) return Fail("profile", "The tagline can be at most 100 characters.");
        if ((location ?? "").Length > 60) return Fail("profile", "Location can be at most 60 characters.");

        // Contact card rows: service + value; rows whose index is listed in contactPrivate are followers-only.
        var card = new List<ContactCardItem>();
        for (var i = 0; i < (contactService?.Count ?? 0); i++)
        {
            var service = (contactService![i] ?? "").Trim();
            var value = i < (contactValue?.Count ?? 0) ? (contactValue![i] ?? "").Trim() : "";
            if (service.Length == 0 && value.Length == 0) continue;
            if (service.Length is 0 or > 40 || value.Length is 0 or > 200)
                return Fail("profile", "Each contact card line needs a service (up to 40 characters) and a value (up to 200).");
            card.Add(new ContactCardItem(service, value, contactPrivate?.Contains(i.ToString()) == true));
        }
        if (card.Count > ContactCardItem.MaxItems)
            return Fail("profile", $"A contact card can have at most {ContactCardItem.MaxItems} lines.");
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
        me.ContactCard = card.Count > 0 ? ContactCardItem.Serialize(card) : "";
        me.PinnedTags = string.Join(", ", PostRenderer.ParseTags(pinnedTags).Take(10));
        await db.SaveChangesAsync();
        return Done("profile", "Profile saved.");
    }

    public async Task<IActionResult> OnPostEmailAsync(string? email, string? currentPassword)
    {
        var me = await LoadAccountAsync();
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
        var me = await LoadAccountAsync();
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("account", "Your current password is incorrect.");
        if (AccountService.ValidatePassword(newPassword) is { } error)
            return Fail("account", error);
        if (newPassword != confirmPassword)
            return Fail("account", "The new passwords don't match.");

        accounts.SetPassword(me, newPassword!);
        await db.SaveChangesAsync();
        // The security stamp changed, which signs out every other session; re-issue this one.
        await ReSignInAsync(me);
        return Done("account", "Password changed. You've been signed out everywhere else.");
    }

    public async Task<IActionResult> OnPostSignOutEverywhereAsync()
    {
        var me = await LoadAccountAsync();
        AccountService.RotateSecurityStamp(me);
        await db.SaveChangesAsync();
        await ReSignInAsync(me);
        return Done("account", "All other sessions have been signed out.");
    }

    public async Task<IActionResult> OnPostPrivacyAsync(MessagePrivacy messagePrivacy, bool showAdultContent, bool isPrivate,
        bool asksEnabled, bool anonAsksAllowed, bool loggedOutAsksAllowed)
    {
        var me = await LoadAsync();
        if (!Enum.IsDefined(messagePrivacy)) return Fail("privacy", "Unknown option.");
        me.MessagePrivacy = messagePrivacy;
        me.ShowAdultContent = showAdultContent;
        me.AsksEnabled = asksEnabled;
        me.AnonAsksAllowed = anonAsksAllowed;
        me.LoggedOutAsksAllowed = loggedOutAsksAllowed;

        // Going public lets everyone who asked to follow in.
        if (me.IsPrivate && !isPrivate)
        {
            var waiting = await db.FollowRequests.Where(f => f.FolloweeId == me.Id).ToListAsync();
            foreach (var r in waiting)
                if (!await db.Follows.AnyAsync(f => f.FollowerId == r.FollowerId && f.FolloweeId == me.Id))
                    db.Follows.Add(new Follow { FollowerId = r.FollowerId, FolloweeId = me.Id });
            db.FollowRequests.RemoveRange(waiting);
            await db.Notifications.Where(n => n.UserId == me.Id && n.Type == NotificationType.FollowRequest).ExecuteDeleteAsync();
        }
        me.IsPrivate = isPrivate;
        await db.SaveChangesAsync();
        return Done("privacy", "Privacy settings saved.");
    }

    /// <summary>Replaces the followed, silenced and CW'd tag lists (each comma separated).</summary>
    public async Task<IActionResult> OnPostTagsAsync(string? followed, string? silenced, string? warned)
    {
        var me = await LoadAsync();
        var lists = new[] { (TagPreferenceKind.Follow, followed), (TagPreferenceKind.Silence, silenced), (TagPreferenceKind.Warn, warned) };
        await db.TagPreferences.Where(t => t.UserId == me.Id).ExecuteDeleteAsync();
        foreach (var (kind, text) in lists)
            foreach (var tag in PostRenderer.ParseTags(text).Take(200))
                db.TagPreferences.Add(new TagPreference { UserId = me.Id, Tag = tag, Kind = kind });
        await db.SaveChangesAsync();
        return Done("tags", "Tag settings saved.");
    }

    public async Task<IActionResult> OnPostDeleteAccountAsync(string? currentPassword, string? confirm)
    {
        var me = await LoadAccountAsync();
        if (!accounts.VerifyPassword(me, currentPassword ?? ""))
            return Fail("account", "Your current password is incorrect.");
        if (!string.Equals(confirm?.Trim(), me.UserName, StringComparison.OrdinalIgnoreCase))
            return Fail("account", "Type your user name to confirm deleting your account.");
        if (me.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin) == 1)
            return Fail("account", "You're the only administrator. Promote someone else before deleting your account.");

        await accounts.DeleteUserAsync(me, uploads);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["Flash"] = "Your account has been deleted. Goodbye!";
        return Redirect("/");
    }
}
