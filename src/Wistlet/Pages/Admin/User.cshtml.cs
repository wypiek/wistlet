using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

public class UserModel(AppDbContext db, AuditService audit, UploadService uploads, NotificationService notifications, PostService posts) : PageModel
{
    public User Target { get; private set; } = null!;
    public bool CanManage { get; private set; }
    public bool IsSelf { get; private set; }
    public int PostCount { get; private set; }
    public int FollowerCount { get; private set; }
    public int FollowingCount { get; private set; }
    public List<PostView> RecentPosts { get; private set; } = [];
    public List<Report> Reports { get; private set; } = [];
    public List<AuditLog> History { get; private set; } = [];

    private async Task<bool> LoadAsync(int id)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (target is null) return false;
        Target = target;
        IsSelf = target.Id == User.GetUserId();
        // Admins manage everyone; moderators only manage regular users.
        CanManage = !IsSelf && (User.IsAdmin() || target.Role == UserRole.User);
        return true;
    }

    private IActionResult Back(string message)
    {
        TempData["Flash"] = message;
        return Redirect($"/admin/user/{Target.Id}");
    }

    private IActionResult Denied(string message)
    {
        TempData["Error"] = message;
        return Redirect($"/admin/user/{Target.Id}");
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        PostCount = await db.Posts.CountAsync(p => p.AuthorId == id);
        FollowerCount = await db.Follows.CountAsync(f => f.FolloweeId == id);
        FollowingCount = await db.Follows.CountAsync(f => f.FollowerId == id);
        RecentPosts = await posts.ProjectAsync(db.Posts.AsNoTracking().Where(p => p.AuthorId == id).OrderByDescending(p => p.Id), User.GetUserId(), 10);
        Reports = await db.Reports.AsNoTracking()
            .Include(r => r.Reporter)
            .Where(r => r.TargetUserId == id || r.Post!.AuthorId == id)
            .OrderByDescending(r => r.Id).Take(20).ToListAsync();
        var handle = $"@{Target.UserName}";
        History = await db.AuditLogs.AsNoTracking().Include(a => a.Actor)
            .Where(a => a.Target == handle || a.Target.EndsWith(" by " + handle))
            .OrderByDescending(a => a.Id).Take(20).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(int id, string? displayName, string? email, bool isVerified, UserRole? role)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage && !(IsSelf && User.IsAdmin())) return Denied("You can't edit this account.");

        var changes = new List<string>();
        displayName = displayName?.Trim();
        if (!string.IsNullOrEmpty(displayName) && displayName.Length <= 50 && displayName != Target.DisplayName)
        {
            changes.Add($"display name “{Target.DisplayName}” → “{displayName}”");
            Target.DisplayName = displayName;
        }
        email = email?.Trim() ?? "";
        if (email != Target.Email && email.Length <= 256)
        {
            changes.Add("e-mail changed");
            Target.Email = email;
        }
        if (isVerified != Target.IsVerified)
        {
            changes.Add(isVerified ? "verified" : "unverified");
            Target.IsVerified = isVerified;
        }
        if (role is UserRole newRole && newRole != Target.Role && Enum.IsDefined(newRole))
        {
            if (!User.IsAdmin()) return Denied("Only administrators can change roles.");
            if (Target.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin) == 1)
                return Denied("This is the only administrator. Promote someone else first.");
            changes.Add($"role {Target.Role} → {newRole}");
            Target.Role = newRole;
            AccountService.RotateSecurityStamp(Target);
        }

        if (changes.Count == 0) return Back("Nothing changed.");
        audit.Log("user.edit", $"@{Target.UserName}", string.Join("; ", changes));
        await db.SaveChangesAsync();
        return Back("User updated.");
    }

    public async Task<IActionResult> OnPostBanAsync(int id, string? reason, int days)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't ban this account.");

        Target.IsBanned = true;
        Target.BanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(300, reason.Trim().Length)];
        Target.BannedUntil = days > 0 ? DateTime.UtcNow.AddDays(days) : null;
        AccountService.RotateSecurityStamp(Target);
        audit.Log("user.ban", $"@{Target.UserName}", $"{(days > 0 ? $"{days} days" : "permanent")}{(Target.BanReason is null ? "" : $": {Target.BanReason}")}");
        await db.SaveChangesAsync();
        return Back($"@{Target.UserName} has been banned {(days > 0 ? $"for {days} days" : "permanently")}.");
    }

    public async Task<IActionResult> OnPostUnbanAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        Target.IsBanned = false;
        Target.BannedUntil = null;
        Target.BanReason = null;
        audit.Log("user.unban", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        return Back($"@{Target.UserName} has been unbanned.");
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id, [FromServices] AccountService accounts)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        var temp = AccountService.GenerateTemporaryPassword();
        accounts.SetPassword(Target, temp);
        audit.Log("user.reset-password", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        TempData["TempPassword"] = temp;
        return Back("Password reset. Give the user the temporary password below — it won't be shown again.");
    }

    public async Task<IActionResult> OnPostSignOutAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        AccountService.RotateSecurityStamp(Target);
        audit.Log("user.sign-out", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        return Back("All of this user's sessions were signed out.");
    }

    public async Task<IActionResult> OnPostRemoveAvatarAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        uploads.Delete(Target.AvatarPath);
        Target.AvatarPath = null;
        audit.Log("user.remove-avatar", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        return Back("Profile picture removed.");
    }

    public async Task<IActionResult> OnPostRemoveBannerAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        uploads.Delete(Target.BannerPath);
        Target.BannerPath = null;
        audit.Log("user.remove-banner", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        return Back("Profile banner removed.");
    }

    public async Task<IActionResult> OnPostClearProfileAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        Target.Bio = "";
        Target.Location = "";
        Target.Website = "";
        audit.Log("user.clear-profile", $"@{Target.UserName}");
        await db.SaveChangesAsync();
        return Back("Bio, location and website cleared.");
    }

    public async Task<IActionResult> OnPostWarnAsync(int id, string? message)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        message = message?.Trim();
        if (string.IsNullOrEmpty(message)) return Denied("Write a message first.");
        if (message.Length > 500) message = message[..500];
        await notifications.NotifyAsync(Target.Id, null, NotificationType.System, message: message);
        audit.Log("user.warn", $"@{Target.UserName}", message);
        await db.SaveChangesAsync();
        return Back("Message sent to the user's notifications.");
    }

    public async Task<IActionResult> OnPostDeletePostsAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!CanManage) return Denied("You can't manage this account.");
        var images = await db.PostAttachments.Where(a => a.Post.AuthorId == id).Select(a => (string?)a.Path).ToListAsync();
        var count = await db.Posts.Where(p => p.AuthorId == id).ExecuteDeleteAsync();
        foreach (var img in images) uploads.Delete(img);
        audit.Log("user.delete-posts", $"@{Target.UserName}", $"{count} posts");
        await db.SaveChangesAsync();
        return Back($"Deleted {count} posts.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, string? confirm)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!User.IsAdmin() || IsSelf) return Denied("Only administrators can delete other accounts.");
        if (!string.Equals(confirm?.Trim(), Target.UserName, StringComparison.OrdinalIgnoreCase))
            return Denied("Type the user name to confirm deletion.");
        if (Target.Role == UserRole.Admin && await db.Users.CountAsync(u => u.Role == UserRole.Admin) == 1)
            return Denied("You can't delete the only administrator.");

        var images = await db.PostAttachments.Where(a => a.Post.AuthorId == id).Select(a => (string?)a.Path).ToListAsync();
        images.Add(Target.AvatarPath);
        images.Add(Target.BannerPath);
        audit.Log("user.delete", $"@{Target.UserName}", $"e-mail {Target.Email}");
        db.Users.Remove(Target);
        await db.SaveChangesAsync();
        foreach (var img in images) uploads.Delete(img);
        TempData["Flash"] = $"@{Target.UserName} was deleted.";
        return Redirect("/admin/users");
    }
}
