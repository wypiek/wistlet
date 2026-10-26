using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

public class PostService(AppDbContext db, SiteSettingsService settings, UploadService uploads, NotificationService notifications, EmojiService emoji)
{
    /// <summary>Most images one post can carry.</summary>
    public const int MaxAttachments = 10;

    /// <summary>
    /// Posts a viewer may see: hides drafts (unless <paramref name="includeDrafts"/>), moderator-hidden posts
    /// (unless staff), posts by banned users, posts on private pages the viewer doesn't follow, and posts
    /// across a block in either direction.
    /// </summary>
    public IQueryable<Post> Visible(int? viewerId, bool isStaff, bool includeDrafts = false)
    {
        var now = DateTime.UtcNow;
        var me = viewerId ?? -1;
        var q = db.Posts.AsNoTracking()
            .Where(p => !(p.Author.IsBanned && (p.Author.BannedUntil == null || p.Author.BannedUntil > now)));
        if (!includeDrafts) q = q.Where(p => !p.IsDraft);
        if (!isStaff)
        {
            q = q.Where(p => !p.IsHidden);
            q = q.Where(p => !p.Author.IsPrivate || p.AuthorId == me || db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == p.AuthorId));
        }
        if (viewerId is not null)
        {
            q = q.Where(p =>
                !db.Blocks.Any(b => (b.BlockerId == me && b.BlockedId == p.AuthorId) ||
                                    (b.BlockerId == p.AuthorId && b.BlockedId == me)));
        }
        return q;
    }

    /// <summary>
    /// Feed posts: top-level posts and shares (comments stay on their post, as on cohost),
    /// without other people's 18+ posts unless the viewer opted in to seeing them, and without
    /// posts carrying a tag the viewer silenced.
    /// </summary>
    public async Task<IQueryable<Post>> FeedAsync(int? viewerId, bool isStaff)
    {
        var q = Visible(viewerId, isStaff).Where(p => p.ParentId == null);
        var me = viewerId ?? -1;
        if (!await ShowsAdultAsync(viewerId))
            q = q.Where(p => p.AuthorId == me || (!p.IsAdult && !(p.SharedPost != null && p.SharedPost.IsAdult)));
        if (viewerId is not null)
        {
            var silenced = db.TagPreferences.Where(t => t.UserId == me && t.Kind == TagPreferenceKind.Silence).Select(t => t.Tag);
            q = q.Where(p => p.AuthorId == me ||
                             (!p.Tags.Any(t => silenced.Contains(t.Tag)) &&
                              !(p.SharedPost != null && p.SharedPost.Tags.Any(t => silenced.Contains(t.Tag)))));
        }
        return q;
    }

    public async Task<bool> ShowsAdultAsync(int? viewerId) =>
        viewerId is int id && await db.Users.AnyAsync(u => u.Id == id && u.ShowAdultContent);

    public async Task<List<PostView>> ProjectAsync(IQueryable<Post> query, int? viewerId, int? take = null)
    {
        if (take is int t) query = query.Take(t);
        var me = viewerId ?? -1;
        var posts = await query.Select(p => new PostView
        {
            Id = p.Id,
            Headline = p.Headline,
            Content = p.Content,
            Html = p.RenderedHtml,
            PreviewHtml = p.RenderedPreviewHtml,
            Tags = p.Tags.OrderBy(x => x.Position).Select(x => x.Tag).ToList(),
            ContentWarnings = p.ContentWarnings,
            IsAdult = p.IsAdult,
            IsShare = p.IsShare,
            SharedPostId = p.SharedPostId,
            Attachments = p.Attachments.OrderBy(a => a.Position).Select(a => new AttachmentView(a.Id, a.Path, a.AltText)).ToList(),
            Ask = p.Ask == null ? null : new AskView(p.Ask.Id, p.Ask.Content,
                p.Ask.IsAnonymous || p.Ask.Sender == null ? null
                    : new UserSummary(p.Ask.Sender.Id, p.Ask.Sender.UserName, p.Ask.Sender.DisplayName, p.Ask.Sender.AvatarPath, p.Ask.Sender.IsVerified, p.Ask.Sender.Role, "", p.Ask.Sender.AvatarShape),
                p.Ask.CreatedAt),
            IsDraft = p.IsDraft,
            CommentsLocked = p.CommentsLocked,
            SharesLocked = p.SharesLocked,
            PinnedToProfile = p.PinnedToProfile,
            AuthorIsPrivate = p.Author.IsPrivate,
            CreatedAt = p.CreatedAt,
            EditedAt = p.EditedAt,
            IsHidden = p.IsHidden,
            IsPinned = p.IsPinned,
            ParentId = p.ParentId,
            ParentAuthorUserName = p.Parent != null ? p.Parent.Author.UserName : null,
            Author = new UserSummary(p.Author.Id, p.Author.UserName, p.Author.DisplayName, p.Author.AvatarPath, p.Author.IsVerified, p.Author.Role, "", p.Author.AvatarShape),
            LikeCount = p.Likes.Count,
            ReplyCount = db.Posts.Count(c => (c.RootId == p.Id || c.ParentId == p.Id) && !c.IsHidden),
            LikedByMe = p.Likes.Any(l => l.UserId == me),
            BookmarkedByMe = p.Bookmarks.Any(b => b.UserId == me),
        }).ToListAsync();

        await AttachSharedPostsAsync(posts, viewerId);
        await ApplyTagWarningsAsync(posts, viewerId);
        return posts;
    }

    /// <summary>Marks posts carrying one of the viewer's CW'd tags, so they start collapsed.</summary>
    private async Task ApplyTagWarningsAsync(List<PostView> posts, int? viewerId)
    {
        if (viewerId is not int me || posts.Count == 0) return;
        var warned = await db.TagPreferences.Where(t => t.UserId == me && t.Kind == TagPreferenceKind.Warn).Select(t => t.Tag).ToListAsync();
        if (warned.Count == 0) return;
        foreach (var post in posts)
            for (var p = post; p is not null; p = p.SharedFrom)
                if (p.Author.Id != me) p.TagWarnings = p.Tags.Where(warned.Contains).ToList();
    }

    /// <summary>Loads the posts that shares point at, following share-of-share chains.</summary>
    private async Task AttachSharedPostsAsync(List<PostView> posts, int? viewerId)
    {
        var pending = posts;
        for (var depth = 0; depth < 10; depth++)
        {
            var ids = pending.Where(p => p.SharedPostId is not null).Select(p => p.SharedPostId!.Value).Distinct().ToList();
            if (ids.Count == 0) return;
            var shared = await ProjectShallowAsync(Visible(viewerId, false).Where(p => ids.Contains(p.Id)));
            var byId = shared.ToDictionary(p => p.Id);
            foreach (var post in pending)
                if (post.SharedPostId is int sid && byId.TryGetValue(sid, out var source)) post.SharedFrom = source;
            pending = shared;
        }
    }

    private Task<List<PostView>> ProjectShallowAsync(IQueryable<Post> query) =>
        query.Select(p => new PostView
        {
            Id = p.Id,
            Headline = p.Headline,
            Content = p.Content,
            Html = p.RenderedHtml,
            PreviewHtml = p.RenderedPreviewHtml,
            Tags = p.Tags.OrderBy(x => x.Position).Select(x => x.Tag).ToList(),
            ContentWarnings = p.ContentWarnings,
            IsAdult = p.IsAdult,
            IsShare = p.IsShare,
            SharedPostId = p.SharedPostId,
            Attachments = p.Attachments.OrderBy(a => a.Position).Select(a => new AttachmentView(a.Id, a.Path, a.AltText)).ToList(),
            Ask = p.Ask == null ? null : new AskView(p.Ask.Id, p.Ask.Content,
                p.Ask.IsAnonymous || p.Ask.Sender == null ? null
                    : new UserSummary(p.Ask.Sender.Id, p.Ask.Sender.UserName, p.Ask.Sender.DisplayName, p.Ask.Sender.AvatarPath, p.Ask.Sender.IsVerified, p.Ask.Sender.Role, "", p.Ask.Sender.AvatarShape),
                p.Ask.CreatedAt),
            SharesLocked = p.SharesLocked,
            CreatedAt = p.CreatedAt,
            EditedAt = p.EditedAt,
            IsHidden = p.IsHidden,
            Author = new UserSummary(p.Author.Id, p.Author.UserName, p.Author.DisplayName, p.Author.AvatarPath, p.Author.IsVerified, p.Author.Role, "", p.Author.AvatarShape),
        }).ToListAsync();

    /// <summary>Applies keyset pagination (newest first) and returns one page plus the cursor for the next.</summary>
    public async Task<(List<PostView> Posts, int? NextCursor)> PageAsync(IQueryable<Post> query, int? viewerId, int? before)
    {
        var size = settings.Current.PostsPerPage;
        if (before is int b) query = query.Where(p => p.Id < b);
        var posts = await ProjectAsync(query.OrderByDescending(p => p.Id), viewerId, size + 1);
        int? next = null;
        if (posts.Count > size)
        {
            posts.RemoveAt(size);
            next = posts[^1].Id;
        }
        return (posts, next);
    }

    /// <summary>Renders Markdown with the site's custom emoji.</summary>
    public PostRenderer.Result Render(string content) => PostRenderer.Render(content, emoji.Current);

    /// <summary>Checks the text parts of a draft against the site limits. Returns an error message or null.</summary>
    private string? Validate(string content, string? headline, IReadOnlyList<string> tags, IReadOnlyList<string> warnings, IEnumerable<string>? altTexts = null)
    {
        var cfg = settings.Current;
        if (content.Length > cfg.MaxPostLength)
            return $"Posts can be at most {cfg.MaxPostLength:N0} characters.";
        if ((headline ?? "").Length > 140)
            return "Headlines can be at most 140 characters.";
        // Empty description boxes arrive as null.
        var alts = altTexts?.Select(a => a ?? "").ToList() ?? [];
        if (alts.Any(a => a.Length > 1000))
            return "Image descriptions can be at most 1,000 characters.";
        var text = string.Join('\n', [content, headline ?? "", .. tags, .. warnings, .. alts]);
        return cfg.GetBannedWords().Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? "Your post contains a word that isn't allowed on this site."
            : null;
    }

    /// <summary>Stores uploaded images as attachments (positions continue after <paramref name="startAt"/>).</summary>
    private async Task<(List<PostAttachment> Saved, string? Error)> SaveAttachmentsAsync(IReadOnlyList<IFormFile> images, IReadOnlyList<string>? alts, int startAt)
    {
        var saved = new List<PostAttachment>();
        for (var i = 0; i < images.Count; i++)
        {
            var (url, error) = await uploads.SaveImageAsync(images[i], "posts");
            if (error is not null)
            {
                foreach (var s in saved) uploads.Delete(s.Path);
                return ([], error);
            }
            if (url is null) continue;
            var alt = alts is not null && i < alts.Count ? (alts[i] ?? "").Trim() : "";
            saved.Add(new PostAttachment { Path = url, AltText = alt, Position = startAt + saved.Count });
        }
        return (saved, null);
    }

    private static string Normalize(string? content) => (content ?? "").Trim().Replace("\r\n", "\n");

    public async Task<(Post? Post, string? Error)> CreateAsync(int authorId, PostDraft draft)
    {
        var cfg = settings.Current;
        var content = Normalize(draft.Content);
        var headline = string.IsNullOrWhiteSpace(draft.Headline) ? null : draft.Headline.Trim();
        var isComment = draft.ParentId is not null;
        var tags = isComment ? [] : PostRenderer.ParseTags(draft.Tags);
        var warnings = isComment ? [] : PostRenderer.ParseWarnings(draft.ContentWarnings);
        var images = (draft.Images ?? []).Where(f => f is { Length: > 0 }).ToList();

        if (content.Length == 0 && headline is null && images.Count == 0 && draft.ShareOfId is null && draft.AskId is null)
            return (null, "Write something first.");
        if (images.Count > MaxAttachments)
            return (null, $"A post can have at most {MaxAttachments} images.");
        if (Validate(content, headline, tags, warnings, draft.AltTexts) is { } invalid)
            return (null, invalid);

        Post? parent = null;
        if (draft.ParentId is int pid)
        {
            parent = await Visible(authorId, false).FirstOrDefaultAsync(p => p.Id == pid);
            if (parent is null) return (null, "The post you're commenting on doesn't exist anymore.");
            var rootId = parent.RootId ?? parent.Id;
            var root = await db.Posts.AsNoTracking().Where(p => p.Id == rootId).Select(p => new { p.AuthorId, p.CommentsLocked }).FirstAsync();
            if (root.CommentsLocked && root.AuthorId != authorId) return (null, "Comments on this post are locked.");
        }

        Post? shared = null;
        if (draft.ShareOfId is int sid && !isComment)
        {
            shared = await Visible(authorId, false).Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == sid);
            if (shared is null) return (null, "That post can't be shared.");
            // Sharing a share that added nothing shares the post it points at.
            if (shared is { IsShare: true, SharedPostId: int inner } && shared.Content.Length == 0 && shared.Headline is null &&
                shared.AskId is null && !await db.PostAttachments.AnyAsync(a => a.PostId == shared.Id))
            {
                shared = await Visible(authorId, false).Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == inner) ?? shared;
            }
            if (shared.SharesLocked && shared.AuthorId != authorId) return (null, "The author turned off sharing for this post.");
            if (shared.Author.IsPrivate && shared.AuthorId != authorId) return (null, "Posts from private pages can't be shared.");
        }

        Ask? ask = null;
        if (draft.AskId is int askId && !isComment)
        {
            ask = await db.Asks.FirstOrDefaultAsync(a => a.Id == askId && a.RecipientId == authorId && !a.IsAnswered);
            if (ask is null) return (null, "That ask has already been answered or deleted.");
        }

        if (images.Count > 0 && !cfg.AllowImageUploads) return (null, "Image uploads are currently disabled.");
        var (attachments, uploadError) = await SaveAttachmentsAsync(images, draft.AltTexts, 0);
        if (uploadError is not null) return (null, uploadError);

        var rendered = Render(content);
        var isDraft = draft.IsDraft && !isComment;
        var post = new Post
        {
            AuthorId = authorId,
            Headline = isComment ? null : headline,
            Content = content,
            RenderedHtml = rendered.Html,
            RenderedPreviewHtml = rendered.PreviewHtml,
            RenderVersion = PostRenderer.Version,
            ContentWarnings = warnings.Count > 0 ? string.Join(", ", warnings) : null,
            IsAdult = !isComment && draft.IsAdult,
            ParentId = draft.ParentId,
            RootId = parent is null ? null : parent.RootId ?? parent.Id,
            IsShare = shared is not null,
            SharedPostId = shared?.Id,
            AskId = ask?.Id,
            IsDraft = isDraft,
            CommentsLocked = !isComment && draft.CommentsLocked,
            SharesLocked = !isComment && draft.SharesLocked,
            Attachments = attachments,
        };
        for (var i = 0; i < tags.Count; i++) post.Tags.Add(new PostTag { Tag = tags[i], Position = i });
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        if (!isDraft)
        {
            if (ask is not null) ask.IsAnswered = true;
            await NotifyPublishedAsync(post, parent?.AuthorId, shared?.AuthorId, ask);
            await db.SaveChangesAsync();
        }
        return (post, null);
    }

    /// <summary>Notifications for a post that just went live: comment, share, answered ask and mentions.</summary>
    private async Task NotifyPublishedAsync(Post post, int? parentAuthorId, int? sharedAuthorId, Ask? ask)
    {
        if (parentAuthorId is int pa)
            await notifications.NotifyAsync(pa, post.AuthorId, NotificationType.Reply, post.Id);
        if (sharedAuthorId is int sa)
            await notifications.NotifyAsync(sa, post.AuthorId, NotificationType.Share, post.Id);
        if (ask?.SenderId is int asker)
            await notifications.NotifyAsync(asker, post.AuthorId, NotificationType.AskAnswered, post.Id);

        var mentioned = PostRenderer.ExtractMentions(post.Content);
        if (mentioned.Count > 0)
        {
            var ids = await db.Users.Where(u => mentioned.Contains(u.NormalizedUserName)).Select(u => u.Id).ToListAsync();
            foreach (var id in ids.Where(id => id != parentAuthorId))
                await notifications.NotifyAsync(id, post.AuthorId, NotificationType.Mention, post.Id);
        }
    }

    /// <summary>
    /// Publishes a draft. It becomes a new post (so it lands at the top of feeds, which are ordered by id)
    /// carrying over the draft's text, tags, images and ask; the draft itself goes away.
    /// </summary>
    public async Task<(Post? Post, string? Error)> PublishDraftAsync(Post draft)
    {
        if (!draft.IsDraft) return (draft, null);
        Ask? ask = null;
        if (draft.AskId is int askId)
        {
            ask = await db.Asks.FirstOrDefaultAsync(a => a.Id == askId && !a.IsAnswered);
            if (ask is null) return (null, "The ask this draft answers was already answered or deleted.");
        }
        int? sharedAuthor = null;
        if (draft.SharedPostId is int sid)
        {
            var shared = await Visible(draft.AuthorId, false).FirstOrDefaultAsync(p => p.Id == sid);
            if (shared is null) return (null, "The post this draft shares isn't available any more.");
            sharedAuthor = shared.AuthorId;
        }

        var post = new Post
        {
            AuthorId = draft.AuthorId,
            Headline = draft.Headline,
            Content = draft.Content,
            RenderedHtml = draft.RenderedHtml,
            RenderedPreviewHtml = draft.RenderedPreviewHtml,
            RenderVersion = draft.RenderVersion,
            ContentWarnings = draft.ContentWarnings,
            IsAdult = draft.IsAdult,
            IsShare = draft.IsShare,
            SharedPostId = draft.SharedPostId,
            AskId = draft.AskId,
            CommentsLocked = draft.CommentsLocked,
            SharesLocked = draft.SharesLocked,
        };
        var tags = await db.PostTags.Where(t => t.PostId == draft.Id).OrderBy(t => t.Position).ToListAsync();
        foreach (var t in tags) post.Tags.Add(new PostTag { Tag = t.Tag, Position = t.Position });
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        await db.PostAttachments.Where(a => a.PostId == draft.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PostId, post.Id));
        db.Posts.Remove(draft);
        if (ask is not null) ask.IsAnswered = true;
        await NotifyPublishedAsync(post, null, sharedAuthor, ask);
        await db.SaveChangesAsync();
        return (post, null);
    }

    /// <summary>Updates an existing post: text, tags, warnings, locks, image descriptions and images.</summary>
    public async Task<string?> UpdateAsync(Post post, PostDraft draft)
    {
        var content = Normalize(draft.Content);
        var headline = string.IsNullOrWhiteSpace(draft.Headline) ? null : draft.Headline.Trim();
        var isComment = post.ParentId is not null;
        var tags = isComment ? [] : PostRenderer.ParseTags(draft.Tags);
        var warnings = isComment ? [] : PostRenderer.ParseWarnings(draft.ContentWarnings);
        var images = (draft.Images ?? []).Where(f => f is { Length: > 0 }).ToList();

        var existing = await db.PostAttachments.Where(a => a.PostId == post.Id).OrderBy(a => a.Position).ToListAsync();
        var removing = existing.Where(a => draft.RemoveAttachments?.Contains(a.Id) == true).ToList();
        var keeping = existing.Except(removing).ToList();

        if (content.Length == 0 && headline is null && keeping.Count + images.Count == 0 && !post.IsShare && post.AskId is null)
            return "A post can't be empty.";
        if (keeping.Count + images.Count > MaxAttachments)
            return $"A post can have at most {MaxAttachments} images.";
        var alts = (draft.AltTexts ?? []).Concat(draft.ExistingAltTexts?.Values ?? []);
        if (Validate(content, headline, tags, warnings, alts) is { } invalid)
            return invalid;
        if (images.Count > 0 && !settings.Current.AllowImageUploads)
            return "Image uploads are currently disabled.";

        var (added, uploadError) = await SaveAttachmentsAsync(images, draft.AltTexts, keeping.Count);
        if (uploadError is not null) return uploadError;

        var rendered = Render(content);
        post.Content = content;
        post.Headline = isComment ? null : headline;
        post.RenderedHtml = rendered.Html;
        post.RenderedPreviewHtml = rendered.PreviewHtml;
        post.RenderVersion = PostRenderer.Version;
        post.ContentWarnings = warnings.Count > 0 ? string.Join(", ", warnings) : null;
        post.IsAdult = !isComment && draft.IsAdult;
        post.CommentsLocked = !isComment && draft.CommentsLocked;
        post.SharesLocked = !isComment && draft.SharesLocked;
        if (!post.IsDraft) post.EditedAt = DateTime.UtcNow;

        for (var i = 0; i < keeping.Count; i++)
        {
            keeping[i].Position = i;
            if (draft.ExistingAltTexts?.TryGetValue(keeping[i].Id, out var alt) == true) keeping[i].AltText = (alt ?? "").Trim();
        }
        db.PostAttachments.RemoveRange(removing);
        foreach (var a in added) { a.PostId = post.Id; db.PostAttachments.Add(a); }

        db.PostTags.RemoveRange(db.PostTags.Where(t => t.PostId == post.Id));
        for (var i = 0; i < tags.Count; i++) db.PostTags.Add(new PostTag { PostId = post.Id, Tag = tags[i], Position = i });
        await db.SaveChangesAsync();
        foreach (var a in removing) uploads.Delete(a.Path);
        return null;
    }

    /// <summary>Deletes a post with all its comments and their uploaded images.</summary>
    public async Task DeleteAsync(Post post)
    {
        var ids = await SubtreeIdsAsync(post.Id);
        var images = await db.PostAttachments.Where(a => ids.Contains(a.PostId)).Select(a => a.Path).ToListAsync();
        db.Posts.Remove(post);
        await db.SaveChangesAsync();
        foreach (var img in images) uploads.Delete(img);
    }

    /// <summary>Every image a user has attached to posts (for deleting their account or posts).</summary>
    public Task<List<string>> ImagePathsOfAsync(int userId) =>
        db.PostAttachments.Where(a => a.Post.AuthorId == userId).Select(a => a.Path).ToListAsync();

    /// <summary>The post and every comment below it, at any depth.</summary>
    public async Task<List<int>> SubtreeIdsAsync(int rootId)
    {
        var all = new List<int> { rootId };
        var frontier = new List<int> { rootId };
        for (var depth = 0; depth < 50 && frontier.Count > 0; depth++)
        {
            frontier = await db.Posts.Where(p => p.ParentId != null && frontier.Contains(p.ParentId.Value)).Select(p => p.Id).ToListAsync();
            all.AddRange(frontier);
        }
        return all;
    }

    /// <summary>Re-renders stored posts whose HTML came from an older renderer version.</summary>
    public async Task<int> RerenderOutdatedAsync()
    {
        var count = 0;
        while (true)
        {
            var batch = await db.Posts.Where(p => p.RenderVersion < PostRenderer.Version).Take(200).ToListAsync();
            if (batch.Count == 0) return count;
            foreach (var post in batch)
            {
                var r = Render(post.Content);
                post.RenderedHtml = r.Html;
                post.RenderedPreviewHtml = r.PreviewHtml;
                post.RenderVersion = PostRenderer.Version;
            }
            await db.SaveChangesAsync();
            count += batch.Count;
        }
    }

    /// <summary>Re-renders posts that use an emoji shortcode (after the emoji was added, changed or removed).</summary>
    public async Task RerenderUsingEmojiAsync(string shortcode)
    {
        var token = $":{shortcode}:";
        await db.Posts.Where(p => p.Content.Contains(token)).ExecuteUpdateAsync(s => s.SetProperty(p => p.RenderVersion, 0));
        await RerenderOutdatedAsync();
    }

    public async Task<List<TrendingTag>> TrendingAsync(int count = 8)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var rows = await db.PostTags.AsNoTracking()
            .Where(t => t.Post.CreatedAt >= since && !t.Post.IsHidden && !t.Post.IsAdult && !t.Post.IsDraft && !t.Post.Author.IsPrivate)
            .GroupBy(t => t.Tag)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag)
            .Take(count)
            .ToListAsync();
        return rows.Select(r => new TrendingTag(r.Tag, r.Count)).ToList();
    }
}
