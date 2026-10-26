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

    [MaxLength(300)]
    public string Bio { get; set; } = "";

    [MaxLength(60)]
    public string Location { get; set; } = "";

    [MaxLength(200)]
    public string Website { get; set; } = "";

    [MaxLength(200)]
    public string? AvatarPath { get; set; }

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

    [MaxLength(5000)]
    public string Content { get; set; } = "";

    [MaxLength(200)]
    public string? ImagePath { get; set; }

    public int? ParentId { get; set; }
    public Post? Parent { get; set; }
    public List<Post> Replies { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }

    /// <summary>Hidden by a moderator; only staff can see it.</summary>
    public bool IsHidden { get; set; }

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

public class SiteSetting
{
    [Key]
    [MaxLength(64)]
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}
