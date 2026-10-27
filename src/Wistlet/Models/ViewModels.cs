namespace Wistlet.Models;

public record UserSummary(
    int Id,
    string UserName,
    string DisplayName,
    string? AvatarPath,
    bool IsVerified,
    UserRole Role,
    string Bio = "",
    string AvatarShape = "squircle")
{
    public string AvatarUrl => AvatarPath ?? $"/avatar/{UserName}.svg";
}

public class PostView
{
    public int Id { get; init; }
    public string? Headline { get; init; }
    public string Content { get; init; } = "";
    public string Html { get; init; } = "";
    public string? PreviewHtml { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? ContentWarnings { get; init; }
    /// <summary>The post's own 18+ flag.</summary>
    public bool IsAdult { get; init; }

    /// <summary>The author marked their whole page 18+.</summary>
    public bool IsAdultPage { get; init; }

    /// <summary>Shown collapsed behind the 18+ notice.</summary>
    public bool ShowsAsAdult => IsAdult || IsAdultPage;

    public bool IsShare { get; init; }
    public int? SharedPostId { get; init; }
    public string? ImagePath { get; init; }
    public string? AudioPath { get; init; }
    public string? AudioTitle { get; init; }
    public string? AudioArtist { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? EditedAt { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>A comment hidden by the author of the post it's on.</summary>
    public bool HiddenByPostAuthor { get; init; }

    public bool IsPinned { get; init; }
    public int? ParentId { get; init; }
    public string? ParentAuthorUserName { get; init; }
    public UserSummary Author { get; init; } = null!;
    public int LikeCount { get; init; }
    public int ReplyCount { get; init; }
    public bool LikedByMe { get; init; }
    public bool BookmarkedByMe { get; init; }

    /// <summary>For shares: the shared post (itself possibly a share), when it is still visible.</summary>
    public PostView? SharedFrom { get; set; }

    /// <summary>A share with no added text, headline or image ("transparent share" on cohost).</summary>
    public bool IsTransparentShare => IsShare && string.IsNullOrWhiteSpace(Content) && string.IsNullOrWhiteSpace(Headline) && ImagePath is null && AudioPath is null;

    public IReadOnlyList<string> Warnings => Services.PostRenderer.ParseWarnings(ContentWarnings);

    /// <summary>The share chain, oldest post first, ending with this post.</summary>
    public IReadOnlyList<PostView> ShareTree
    {
        get
        {
            var chain = new List<PostView>();
            for (var p = SharedFrom; p is not null && chain.Count < 10; p = p.SharedFrom) chain.Insert(0, p);
            return chain;
        }
    }

    /// <summary>The post a share ultimately points at (or this post).</summary>
    public PostView Root => ShareTree.Count > 0 ? ShareTree[0] : this;
}

/// <summary>Everything the composer submits.</summary>
public record PostDraft(
    string? Content,
    string? Headline = null,
    string? Tags = null,
    string? ContentWarnings = null,
    bool IsAdult = false,
    IFormFile? Image = null,
    int? ParentId = null,
    int? ShareOfId = null,
    IFormFile? Audio = null,
    string? AudioTitle = null,
    string? AudioArtist = null);

public record TrendingTag(string Tag, int Count);

/// <summary>Options passed to the shared post partial.</summary>
public record PostListModel(IReadOnlyList<PostView> Posts, string? EmptyText = null, string? MoreUrl = null, bool Preview = true);

public record ComposerModel(string ReturnUrl, int? ParentId = null, string Placeholder = "what's on your mind?", string ButtonText = "post")
{
    /// <summary>Set to edit an existing post; the form then shows its current values.</summary>
    public PostView? Editing { get; init; }

    /// <summary>Set when sharing a post with added content.</summary>
    public int? ShareOfId { get; init; }

    /// <summary>Comments are plain bodies without headline, tags or warnings.</summary>
    public bool IsComment { get; init; }

    /// <summary>Show headline, tags and warning fields expanded.</summary>
    public bool Expanded { get; init; }
}

public record UserCardModel(UserSummary User, bool ShowFollow = true, bool IsFollowing = false);

public record SidebarModel(bool SignedIn, IReadOnlyList<TrendingTag> Trending);

/// <summary>Who-did-what notifications of one kind about one post (or new followers), merged as on cohost.</summary>
public class NotificationGroup
{
    public NotificationType Type { get; init; }
    public int? PostId { get; init; }
    public string? PostExcerpt { get; init; }
    public string? Message { get; init; }
    public DateTime LatestAt { get; set; }
    public bool IsUnread { get; set; }

    /// <summary>Distinct actors, most recent first.</summary>
    public List<UserSummary> Actors { get; } = [];

    /// <summary>For comments, shares and mentions: the post that caused the notification.</summary>
    public int? ActivityPostId { get; init; }
    public string? ActivityExcerpt { get; init; }
}

public record NotificationDay(string Label, IReadOnlyList<NotificationGroup> Groups);

/// <summary>A navigation tab: a link to <see cref="Path"/>, optionally with one query parameter.</summary>
public record TabItem(string Text, string Path, bool Selected, string? Name = null, string? Value = null);

public record TabsModel(string Label, IReadOnlyList<TabItem> Tabs);

public record UserListModel(IReadOnlyList<UserCardModel> Users, string? EmptyText = null, bool Compact = false);

public record IconModel(string Name, bool Filled = false);

public record PostHeaderModel(PostView Post, PostView? SharedFrom = null);

/// <summary>Post contents; <paramref name="Expand"/> opens read-more posts (used on the post's own page).</summary>
public record PostContentsModel(PostView Post, bool Expand = false);

public record CommentTreeModel(PostView Comment, ILookup<int, PostView> Children, PostView Root, int Depth = 0);

public record PostBodyModel(PostView Post, bool Expand, bool SkipHeadline, string Uid);
