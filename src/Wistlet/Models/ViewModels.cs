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
    public bool IsAdult { get; init; }
    public bool IsShare { get; init; }
    public int? SharedPostId { get; init; }
    public IReadOnlyList<AttachmentView> Attachments { get; init; } = [];
    public AskView? Ask { get; init; }
    public bool IsDraft { get; init; }
    public bool CommentsLocked { get; init; }
    public bool SharesLocked { get; init; }
    public bool PinnedToProfile { get; init; }
    public bool AuthorIsPrivate { get; init; }

    /// <summary>Whether other people may share this post (not locked, not on a private page, not a comment or draft).</summary>
    public bool Shareable => !SharesLocked && !AuthorIsPrivate && ParentId is null && !IsDraft;
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

    /// <summary>For shares: the shared post (itself possibly a share), when it is still visible.</summary>
    public PostView? SharedFrom { get; set; }

    /// <summary>A share with no added text, headline or image ("transparent share" on cohost).</summary>
    public bool IsTransparentShare => IsShare && string.IsNullOrWhiteSpace(Content) && string.IsNullOrWhiteSpace(Headline) && Attachments.Count == 0 && Ask is null;

    /// <summary>Tags the viewer asked to have collapsed ("CW'd tags"), set per viewer after loading.</summary>
    public IReadOnlyList<string> TagWarnings { get; set; } = [];

    /// <summary>The author's content warnings plus the viewer's CW'd tags on this post.</summary>
    public IReadOnlyList<string> Warnings =>
        [.. Services.PostRenderer.ParseWarnings(ContentWarnings), .. TagWarnings.Select(t => "#" + t)];

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

public record AttachmentView(int Id, string Url, string AltText);

/// <summary>An ask shown on top of its answer; <see cref="Asker"/> is null for anonymous asks.</summary>
public record AskView(int Id, string Content, UserSummary? Asker, DateTime CreatedAt);

/// <summary>Everything the composer submits.</summary>
public record PostDraft(
    string? Content,
    string? Headline = null,
    string? Tags = null,
    string? ContentWarnings = null,
    bool IsAdult = false,
    IReadOnlyList<IFormFile>? Images = null,
    int? ParentId = null,
    int? ShareOfId = null)
{
    /// <summary>Alt text for each new image, in the same order.</summary>
    public IReadOnlyList<string>? AltTexts { get; init; }

    /// <summary>When editing: new alt text for existing attachments, by attachment id.</summary>
    public IReadOnlyDictionary<int, string>? ExistingAltTexts { get; init; }

    /// <summary>When editing: attachments to remove.</summary>
    public IReadOnlyList<int>? RemoveAttachments { get; init; }

    public bool IsDraft { get; init; }
    public bool CommentsLocked { get; init; }
    public bool SharesLocked { get; init; }

    /// <summary>The ask this post answers.</summary>
    public int? AskId { get; init; }
}

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

    /// <summary>Set when answering an ask; it is shown above the composer.</summary>
    public AskView? Answering { get; init; }
}

public record UserCardModel(UserSummary User, bool ShowFollow = true, bool IsFollowing = false);

public record SidebarModel(bool SignedIn, IReadOnlyList<TrendingTag> Trending)
{
    public IReadOnlyList<string> FollowedTags { get; init; } = [];
}

/// <summary>A navigation tab: a link to <see cref="Path"/>, optionally with one query parameter.</summary>
public record TabItem(string Text, string Path, bool Selected, string? Name = null, string? Value = null);

public record TabsModel(string Label, IReadOnlyList<TabItem> Tabs);

public record UserListModel(IReadOnlyList<UserCardModel> Users, string? EmptyText = null, bool Compact = false);

public record IconModel(string Name, bool Filled = false);

public record PostHeaderModel(PostView Post, PostView? SharedFrom = null);

/// <summary>Post contents; <paramref name="Expand"/> opens read-more posts (used on the post's own page).</summary>
public record PostContentsModel(PostView Post, bool Expand = false);

public record CommentTreeModel(PostView Comment, ILookup<int, PostView> Children, int RootId, int Depth = 0, bool CanReply = true);

public record PostBodyModel(PostView Post, bool Expand, bool SkipHeadline, string Uid);
