namespace Wistlet.Models;

public record UserSummary(
    int Id,
    string UserName,
    string DisplayName,
    string? AvatarPath,
    bool IsVerified,
    UserRole Role,
    string Bio = "")
{
    public string AvatarUrl => AvatarPath ?? $"/avatar/{UserName}.svg";
}

public class PostView
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public string? ImagePath { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? EditedAt { get; init; }
    public bool IsHidden { get; init; }
    public bool IsPinned { get; init; }
    public int? ParentId { get; init; }
    public string? ParentAuthorUserName { get; init; }
    public UserSummary Author { get; init; } = null!;
    public int LikeCount { get; init; }
    public int ReplyCount { get; init; }
    public bool LikedByMe { get; init; }
    public bool BookmarkedByMe { get; init; }
}

public record TrendingTag(string Tag, int Count);

/// <summary>Options passed to the shared post partial.</summary>
public record PostListModel(IReadOnlyList<PostView> Posts, string? EmptyText = null, string? MoreUrl = null);

public record ComposerModel(string ReturnUrl, int? ParentId = null, string Placeholder = "what's on your mind?", string ButtonText = "post");

public record UserCardModel(UserSummary User, bool ShowFollow = true, bool IsFollowing = false);

public record SidebarModel(bool SignedIn, IReadOnlyList<TrendingTag> Trending);

/// <summary>A navigation tab. Rendered as a 7.css tab button that submits a GET form to <see cref="Path"/>.</summary>
public record TabItem(string Text, string Path, bool Selected, string? Name = null, string? Value = null);

public record TabsModel(string Label, IReadOnlyList<TabItem> Tabs);

public record UserListModel(IReadOnlyList<UserCardModel> Users, string? EmptyText = null, bool Compact = false);
