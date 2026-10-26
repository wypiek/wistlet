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

/// <summary>
/// A page (cohost's "project"): a handle with a profile, posts and follows. A page with no
/// <see cref="OwnerId"/> is also a login account; an account can own further pages, which have
/// no e-mail or password of their own and are used by switching to them.
/// </summary>
public class User
{
    public int Id { get; set; }

    /// <summary>For extra pages: the account that owns this page. Null for accounts themselves.</summary>
    public int? OwnerId { get; set; }
    public User? Owner { get; set; }

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

    /// <summary>Wide header image shown at the top of the profile.</summary>
    [MaxLength(200)]
    public string? BannerPath { get; set; }

    /// <summary>Private pages: only approved followers (and staff) see the posts; follows become requests.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Whether people can send this page asks.</summary>
    public bool AsksEnabled { get; set; } = true;

    /// <summary>Whether signed-in people may ask without showing who they are.</summary>
    public bool AnonAsksAllowed { get; set; } = true;

    /// <summary>Whether people who aren't signed in may send (anonymous) asks.</summary>
    public bool LoggedOutAsksAllowed { get; set; }

    /// <summary>The profile's contact card, as JSON (see <see cref="ContactCardItem"/>).</summary>
    [MaxLength(4000)]
    public string ContactCard { get; set; } = "";

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

    /// <summary>True for extra pages, which belong to another account and can't log in.</summary>
    public bool IsExtraPage => OwnerId is not null;

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

    /// <summary>Drafts are only reachable by their link; publishing turns them into a new post.</summary>
    public bool IsDraft { get; set; }

    /// <summary>Nobody but the author can comment.</summary>
    public bool CommentsLocked { get; set; }

    /// <summary>Nobody can share the post.</summary>
    public bool SharesLocked { get; set; }

    /// <summary>Pinned by its author to the top of their page.</summary>
    public bool PinnedToProfile { get; set; }

    /// <summary>For answers: the ask the post replies to (shown on top of it).</summary>
    public int? AskId { get; set; }
    public Ask? Ask { get; set; }

    public List<PostAttachment> Attachments { get; set; } = [];

    /// <summary>Links on their own line, one per line, shown as embeds (from <see cref="Services.PostRenderer.Result.EmbedUrls"/>).</summary>
    [MaxLength(10_000)]
    public string EmbedUrls { get; set; } = "";

    public int? ParentId { get; set; }
    public Post? Parent { get; set; }

    /// <summary>For comments: the top-level post of the thread (so whole threads can be counted).</summary>
    public int? RootId { get; set; }
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

    /// <summary>Order the author typed the tags in.</summary>
    public int Position { get; set; }
}

public class PostAttachment
{
    public int Id { get; set; }
    public int PostId { get; set; }
    public Post Post { get; set; } = null!;

    [MaxLength(200)]
    public string Path { get; set; } = "";

    /// <summary>Image description for screen readers (and the "alt" badge).</summary>
    [MaxLength(1000)]
    public string AltText { get; set; } = "";

    public int Position { get; set; }
}

/// <summary>A question sent to a page. It stays in the recipient's inbox until answered (as a post) or deleted.</summary>
public class Ask
{
    public int Id { get; set; }
    public int RecipientId { get; set; }
    public User Recipient { get; set; } = null!;

    /// <summary>Who sent it, if they were signed in (kept for moderation even for anonymous asks).</summary>
    public int? SenderId { get; set; }
    public User? Sender { get; set; }

    /// <summary>Shown as "anonymous user asked:".</summary>
    public bool IsAnonymous { get; set; }

    [MaxLength(1000)]
    public string Content { get; set; } = "";

    public bool IsAnswered { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum LinkPreviewStatus
{
    Pending = 0,
    Ok = 1,
    Failed = 2,
}

/// <summary>What a linked page says about itself (OpenGraph / title tags), fetched once and cached.</summary>
public class LinkPreview
{
    public int Id { get; set; }

    [MaxLength(2000)]
    public string Url { get; set; } = "";

    public LinkPreviewStatus Status { get; set; }

    [MaxLength(300)]
    public string? Title { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(2000)]
    public string? ImageUrl { get; set; }

    [MaxLength(200)]
    public string? SiteName { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A follow waiting for a private page to approve it.</summary>
public class FollowRequest
{
    public int FollowerId { get; set; }
    public User Follower { get; set; } = null!;
    public int FolloweeId { get; set; }
    public User Followee { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum TagPreferenceKind
{
    /// <summary>Posts with the tag show up on the home feed.</summary>
    Follow = 0,
    /// <summary>Posts with the tag are left out of feeds.</summary>
    Silence = 1,
    /// <summary>Posts with the tag start collapsed, like a content warning.</summary>
    Warn = 2,
}

public class TagPreference
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    [MaxLength(64)]
    public string Tag { get; set; } = "";

    public TagPreferenceKind Kind { get; set; }
}

/// <summary>Site emoji, written as <c>:shortcode:</c> in posts.</summary>
public class CustomEmoji
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Shortcode { get; set; } = "";

    [MaxLength(200)]
    public string Path { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
    AskAnswered = 6,
    FollowRequest = 7,
    FollowAccepted = 8,
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

/// <summary>One line of a profile's contact card.</summary>
public record ContactCardItem(string Service, string Value, bool FollowersOnly)
{
    public const int MaxItems = 10;

    public static List<ContactCardItem> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<ContactCardItem>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<ContactCardItem> items) => System.Text.Json.JsonSerializer.Serialize(items);
}

public static class AvatarShapes
{
    public static readonly IReadOnlyList<string> All = ["squircle", "circle", "roundrect", "egg", "capsule-big", "capsule-small"];

    public static string Resolve(string? shape) => shape is not null && All.Contains(shape) ? shape : "squircle";
}
