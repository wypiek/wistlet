using System.ComponentModel.DataAnnotations;

namespace Wistlet.Models;

public enum UserRole
{
    User = 0,
    Moderator = 1,
    Admin = 2,
}

public enum MessagePrivacy
{
    Everyone = 0,
    PeopleIFollow = 1,
    Nobody = 2,
}

public class User
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string UserName { get; set; } = "";

    /// <summary>Lower-cased user name, used for unique lookups.</summary>
    [MaxLength(32)]
    public string NormalizedUserName { get; set; } = "";

    [MaxLength(50)]
    public string DisplayName { get; set; } = "";

    [MaxLength(256)]
    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    /// <summary>Rotated whenever existing sessions must be invalidated (password change, ban, role change).</summary>
    [MaxLength(64)]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Profile description (Markdown, rendered like a post).</summary>
    [MaxLength(5000)]
    public string Bio { get; set; } = "";

    [MaxLength(60)]
    public string Location { get; set; } = "";

    [MaxLength(200)]
    public string Website { get; set; } = "";

    [MaxLength(200)]
    public string? AvatarPath { get; set; }

    /// <summary>One of cohost's avatar shapes: squircle, circle, roundrect, egg, capsule-big, capsule-small.</summary>
    [MaxLength(20)]
    public string AvatarShape { get; set; } = "squircle";

    [MaxLength(40)]
    public string Pronouns { get; set; } = "";

    /// <summary>Short line under the name (cohost's "dek").</summary>
    [MaxLength(100)]
    public string Dek { get; set; } = "";

    /// <summary>Whether 18+ posts appear in this user's feeds.</summary>
    public bool ShowAdultContent { get; set; }

    /// <summary>Marks the whole page 18+: all its posts count as 18+ and the profile asks viewers to confirm their age first.</summary>
    public bool IsAdultPage { get; set; }

    /// <summary>Only logged-in people can see the page and its posts.</summary>
    public bool HiddenFromGuests { get; set; }

    /// <summary>TOTP key for two-factor login, encrypted with ASP.NET Core data protection.</summary>
    [MaxLength(500)]
    public string? TwoFactorSecret { get; set; }

    public bool TwoFactorEnabled { get; set; }

    /// <summary>Last accepted TOTP time step, so a code can't be used twice.</summary>
    public long TwoFactorLastStep { get; set; }

    /// <summary>Space-separated SHA-256 hashes of the unused recovery codes.</summary>
    [MaxLength(1000)]
    public string? RecoveryCodes { get; set; }

    /// <summary>Wide header image shown at the top of the profile.</summary>
    [MaxLength(200)]
    public string? BannerPath { get; set; }

    public UserRole Role { get; set; } = UserRole.User;
    public bool IsVerified { get; set; }
    public bool IsBanned { get; set; }

    [MaxLength(300)]
    public string? BanReason { get; set; }

    public DateTime? BannedUntil { get; set; }
    public MessagePrivacy MessagePrivacy { get; set; } = MessagePrivacy.Everyone;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }

    public List<Post> Posts { get; set; } = [];

    public bool IsStaff => Role >= UserRole.Moderator;

    /// <summary>True if the ban is currently in effect (temporary bans expire on their own).</summary>
    public bool IsCurrentlyBanned => IsBanned && (BannedUntil is null || BannedUntil > DateTime.UtcNow);
}

public class Post
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;

    /// <summary>Optional title shown above the body.</summary>
    [MaxLength(140)]
    public string? Headline { get; set; }

    /// <summary>Markdown source (raw HTML with inline styles allowed).</summary>
    [MaxLength(200_000)]
    public string Content { get; set; } = "";

    /// <summary>Sanitized HTML rendered from <see cref="Content"/>.</summary>
    public string RenderedHtml { get; set; } = "";

    /// <summary>Rendered part before the read-more marker, if the post has one.</summary>
    public string? RenderedPreviewHtml { get; set; }

    /// <summary><see cref="Services.PostRenderer.Version"/> used for the stored HTML.</summary>
    public int RenderVersion { get; set; }

    /// <summary>Comma-separated content warnings; posts with any are collapsed.</summary>
    [MaxLength(500)]
    public string? ContentWarnings { get; set; }

    /// <summary>18+ posts are collapsed, and only shown in feeds to people who opted in.</summary>
    public bool IsAdult { get; set; }

    /// <summary>True for shares ("rechosts"). <see cref="SharedPostId"/> is null if the shared post was deleted.</summary>
    public bool IsShare { get; set; }
    public int? SharedPostId { get; set; }
    public Post? SharedPost { get; set; }

    [MaxLength(200)]
    public string? ImagePath { get; set; }

    /// <summary>Attached audio file, played with the post's audio player.</summary>
    [MaxLength(200)]
    public string? AudioPath { get; set; }

    [MaxLength(140)]
    public string? AudioTitle { get; set; }

    [MaxLength(140)]
    public string? AudioArtist { get; set; }

    public int? ParentId { get; set; }
    public Post? Parent { get; set; }

    /// <summary>For comments: the top-level post of the thread (so whole threads can be counted).</summary>
    public int? RootId { get; set; }
    public List<Post> Replies { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }

    /// <summary>Hidden by a moderator; only staff can see it.</summary>
    public bool IsHidden { get; set; }

    /// <summary>For comments: hidden by the author of the post it's on. Only they, the commenter and staff still see it.</summary>
    public bool HiddenByPostAuthor { get; set; }

    /// <summary>Pinned by staff to the top of Explore.</summary>
    public bool IsPinned { get; set; }

    public List<Like> Likes { get; set; } = [];
    public List<Bookmark> Bookmarks { get; set; } = [];
    public List<PostTag> Tags { get; set; } = [];
}

public class PostTag
{
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    [MaxLength(64)]
    public string Tag { get; set; } = "";

    /// <summary>Order the author typed the tags in.</summary>
    public int Position { get; set; }
}

public class Like
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Bookmark
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Follow
{
    public int FollowerId { get; set; }
    public User Follower { get; set; } = null!;
    public int FolloweeId { get; set; }
    public User Followee { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Block
{
    public int BlockerId { get; set; }
    public User Blocker { get; set; } = null!;
    public int BlockedId { get; set; }
    public User Blocked { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum NotificationType
{
    Like = 0,
    Reply = 1,
    Follow = 2,
    Mention = 3,
    System = 4,
    Share = 5,
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public int? ActorId { get; set; }
    public User? Actor { get; set; }
    public NotificationType Type { get; set; }
    public int? PostId { get; set; }
    public Post? Post { get; set; }

    [MaxLength(500)]
    public string? Message { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Message
{
    public int Id { get; set; }
    public int SenderId { get; set; }
    public User Sender { get; set; } = null!;
    public int RecipientId { get; set; }
    public User Recipient { get; set; } = null!;

    [MaxLength(2000)]
    public string Body { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public enum ReportStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2,
}

public class Report
{
    public int Id { get; set; }
    public int ReporterId { get; set; }
    public User Reporter { get; set; } = null!;
    public int? PostId { get; set; }
    public Post? Post { get; set; }
    public int? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    [MaxLength(40)]
    public string Category { get; set; } = "";

    [MaxLength(1000)]
    public string Reason { get; set; } = "";

    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? ResolvedById { get; set; }
    public User? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }

    [MaxLength(500)]
    public string? ResolutionNote { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public int? ActorId { get; set; }
    public User? Actor { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = "";

    [MaxLength(200)]
    public string Target { get; set; } = "";

    [MaxLength(1000)]
    public string? Details { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the token sent by e-mail (the token itself is never stored).</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class SiteSetting
{
    [Key]
    [MaxLength(64)]
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

public static class AvatarShapes
{
    public static readonly IReadOnlyList<string> All = ["squircle", "circle", "roundrect", "egg", "capsule-big", "capsule-small"];

    public static string Resolve(string? shape) => shape is not null && All.Contains(shape) ? shape : "squircle";
}
