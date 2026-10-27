using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;
using Wistlet.Services.Federation;

namespace Wistlet.Services;

public class PostService(AppDbContext db, SiteSettingsService settings, UploadService uploads, NotificationService notifications, FederationService federation)
{
    /// <summary>
    /// Posts a viewer may see: hides moderator-hidden posts (unless staff), posts by
    /// banned users, posts across a block in either direction, pages hidden from
    /// logged-out visitors (for guests), and comments the post's author hid (except
    /// from that author and the commenter).
    /// </summary>
    public IQueryable<Post> Visible(int? viewerId, bool isStaff)
    {
        var now = DateTime.UtcNow;
        var q = db.Posts.AsNoTracking()
            .Where(p => !(p.Author.IsBanned && (p.Author.BannedUntil == null || p.Author.BannedUntil > now)));
        if (!isStaff) q = q.Where(p => !p.IsHidden);
        // Content from instances an admin blocked.
        q = q.Where(p => p.Author.Domain == null || !db.RemoteInstances.Any(i => i.Domain == p.Author.Domain && i.IsBlocked));
        if (viewerId is int me)
        {
            q = q.Where(p =>
                !db.Blocks.Any(b => (b.BlockerId == me && b.BlockedId == p.AuthorId) ||
                                    (b.BlockerId == p.AuthorId && b.BlockedId == me)));
            if (!isStaff)
                q = q.Where(p => !p.HiddenByPostAuthor || p.AuthorId == me || db.Posts.Any(r => r.Id == p.RootId && r.AuthorId == me));
        }
        else
        {
            q = q.Where(p => !p.Author.HiddenFromGuests && !p.HiddenByPostAuthor);
        }
        return q;
    }

    /// <summary>
    /// Feed posts: top-level posts and shares (comments stay on their post, as on cohost),
    /// without other people's 18+ posts (or posts from 18+ pages) unless the viewer opted in to seeing them.
    /// </summary>
    public async Task<IQueryable<Post>> FeedAsync(int? viewerId, bool isStaff)
    {
        var q = Visible(viewerId, isStaff).Where(p => p.ParentId == null);
        if (!await ShowsAdultAsync(viewerId))
        {
            var me = viewerId ?? -1;
            q = q.Where(p => p.AuthorId == me ||
                             (!p.IsAdult && !p.Author.IsAdultPage &&
                              !(p.SharedPost != null && (p.SharedPost.IsAdult || p.SharedPost.Author.IsAdultPage))));
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
            IsAdultPage = p.Author.IsAdultPage,
            IsShare = p.IsShare,
            SharedPostId = p.SharedPostId,
            ImagePath = p.ImagePath,
            AudioPath = p.AudioPath,
            AudioTitle = p.AudioTitle,
            AudioArtist = p.AudioArtist,
            CreatedAt = p.CreatedAt,
            EditedAt = p.EditedAt,
            IsHidden = p.IsHidden,
            HiddenByPostAuthor = p.HiddenByPostAuthor,
            IsPinned = p.IsPinned,
            RemoteId = p.RemoteId,
            ParentId = p.ParentId,
            ParentAuthorUserName = p.Parent != null ? p.Parent.Author.UserName : null,
            Author = new UserSummary(p.Author.Id, p.Author.UserName, p.Author.DisplayName, p.Author.AvatarPath, p.Author.IsVerified, p.Author.Role, "", p.Author.AvatarShape),
            LikeCount = p.Likes.Count,
            ReplyCount = db.Posts.Count(c => (c.RootId == p.Id || c.ParentId == p.Id) && !c.IsHidden && !c.HiddenByPostAuthor),
            LikedByMe = p.Likes.Any(l => l.UserId == me),
            BookmarkedByMe = p.Bookmarks.Any(b => b.UserId == me),
        }).ToListAsync();

        await AttachSharedPostsAsync(posts, viewerId);
        return posts;
    }

    /// <summary>Loads the posts that shares point at, following share-of-share chains.</summary>
    private async Task AttachSharedPostsAsync(List<PostView> posts, int? viewerId)
    {
        var pending = posts;
        var staff = false;
        for (var depth = 0; depth < 10; depth++)
        {
            var ids = pending.Where(p => p.SharedPostId is not null).Select(p => p.SharedPostId!.Value).Distinct().ToList();
            if (ids.Count == 0) return;
            var shared = await ProjectShallowAsync(Visible(viewerId, staff).Where(p => ids.Contains(p.Id)), viewerId);
            var byId = shared.ToDictionary(p => p.Id);
            foreach (var post in pending)
                if (post.SharedPostId is int sid && byId.TryGetValue(sid, out var source)) post.SharedFrom = source;
            pending = shared;
        }
    }

    private Task<List<PostView>> ProjectShallowAsync(IQueryable<Post> query, int? viewerId) =>
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
            IsAdultPage = p.Author.IsAdultPage,
            IsShare = p.IsShare,
            SharedPostId = p.SharedPostId,
            ImagePath = p.ImagePath,
            AudioPath = p.AudioPath,
            AudioTitle = p.AudioTitle,
            AudioArtist = p.AudioArtist,
            CreatedAt = p.CreatedAt,
            EditedAt = p.EditedAt,
            IsHidden = p.IsHidden,
            RemoteId = p.RemoteId,
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

    /// <summary>Checks the text parts of a draft against the site limits. Returns an error message or null.</summary>
    private string? Validate(string content, string? headline, IReadOnlyList<string> tags, IReadOnlyList<string> warnings,
        string? audioTitle = null, string? audioArtist = null)
    {
        var cfg = settings.Current;
        if (content.Length > cfg.MaxPostLength)
            return $"Posts can be at most {cfg.MaxPostLength:N0} characters.";
        if ((headline ?? "").Length > 140)
            return "Headlines can be at most 140 characters.";
        if ((audioTitle ?? "").Length > 140 || (audioArtist ?? "").Length > 140)
            return "Track titles and artist names can be at most 140 characters.";
        var text = string.Join('\n', [content, headline ?? "", .. tags, .. warnings, audioTitle ?? "", audioArtist ?? ""]);
        return cfg.GetBannedWords().Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? "Your post contains a word that isn't allowed on this site."
            : null;
    }

    public async Task<(Post? Post, string? Error)> CreateAsync(int authorId, PostDraft draft)
    {
        var cfg = settings.Current;
        var content = (draft.Content ?? "").Trim().Replace("\r\n", "\n");
        var headline = string.IsNullOrWhiteSpace(draft.Headline) ? null : draft.Headline.Trim();
        var isComment = draft.ParentId is not null;
        var tags = isComment ? [] : PostRenderer.ParseTags(draft.Tags);
        var warnings = isComment ? [] : PostRenderer.ParseWarnings(draft.ContentWarnings);
        var hasImage = draft.Image is { Length: > 0 };
        // Audio goes on posts; comments stay text (and images).
        var hasAudio = !isComment && draft.Audio is { Length: > 0 };
        var audioTitle = hasAudio ? Clean(draft.AudioTitle) : null;
        var audioArtist = hasAudio ? Clean(draft.AudioArtist) : null;

        if (content.Length == 0 && headline is null && !hasImage && !hasAudio && draft.ShareOfId is null)
            return (null, "Write something first.");
        if (Validate(content, headline, tags, warnings, audioTitle, audioArtist) is { } invalid)
            return (null, invalid);

        Post? parent = null;
        if (draft.ParentId is int pid)
        {
            parent = await Visible(authorId, false).FirstOrDefaultAsync(p => p.Id == pid);
            if (parent is null) return (null, "The post you're commenting on doesn't exist anymore.");
        }

        Post? shared = null;
        if (draft.ShareOfId is int sid && !isComment)
        {
            shared = await Visible(authorId, false).FirstOrDefaultAsync(p => p.Id == sid);
            if (shared is null) return (null, "That post can't be shared.");
            // Sharing a share that added nothing shares the post it points at.
            if (shared is { IsShare: true, SharedPostId: int inner } && string.IsNullOrWhiteSpace(shared.Content) &&
                shared.Headline is null && shared.ImagePath is null && shared.AudioPath is null)
            {
                shared = await Visible(authorId, false).FirstOrDefaultAsync(p => p.Id == inner) ?? shared;
            }
        }

        string? audioUrl = null;
        if (hasAudio)
        {
            if (!cfg.AllowAudioUploads) return (null, "Audio attachments are currently disabled.");
            var (url, error) = await uploads.SaveAudioAsync(draft.Audio, "audio");
            if (error is not null) return (null, error);
            audioUrl = url;
        }

        string? imageUrl = null;
        if (hasImage)
        {
            string? error = null;
            if (!cfg.AllowImageUploads) error = "Image uploads are currently disabled.";
            else (imageUrl, error) = await uploads.SaveImageAsync(draft.Image, "posts");
            if (error is not null)
            {
                uploads.Delete(audioUrl);
                return (null, error);
            }
        }

        var rendered = PostRenderer.Render(content);
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
            ImagePath = imageUrl,
            AudioPath = audioUrl,
            AudioTitle = audioTitle,
            AudioArtist = audioArtist,
            ParentId = draft.ParentId,
            RootId = parent is null ? null : parent.RootId ?? parent.Id,
            IsShare = shared is not null,
            SharedPostId = shared?.Id,
        };
        for (var i = 0; i < tags.Count; i++) post.Tags.Add(new PostTag { Tag = tags[i], Position = i });
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        if (parent is not null)
            await notifications.NotifyAsync(parent.AuthorId, authorId, NotificationType.Reply, post.Id);
        if (shared is not null)
            await notifications.NotifyAsync(shared.AuthorId, authorId, NotificationType.Share, post.Id);

        var mentioned = PostRenderer.ExtractMentions(content);
        if (mentioned.Count > 0)
        {
            var ids = await db.Users.Where(u => mentioned.Contains(u.NormalizedUserName)).Select(u => u.Id).ToListAsync();
            foreach (var id in ids.Where(id => id != parent?.AuthorId))
                await notifications.NotifyAsync(id, authorId, NotificationType.Mention, post.Id);
        }

        await db.SaveChangesAsync();
        await federation.PostChangedAsync(post);
        return (post, null);
    }

    /// <summary>Updates the text of an existing post (attached files stay as they are; the track title and artist can change).</summary>
    public async Task<string?> UpdateAsync(Post post, PostDraft draft)
    {
        var content = (draft.Content ?? "").Trim().Replace("\r\n", "\n");
        var headline = string.IsNullOrWhiteSpace(draft.Headline) ? null : draft.Headline.Trim();
        var isComment = post.ParentId is not null;
        var tags = isComment ? [] : PostRenderer.ParseTags(draft.Tags);
        var warnings = isComment ? [] : PostRenderer.ParseWarnings(draft.ContentWarnings);
        var audioTitle = post.AudioPath is null ? null : Clean(draft.AudioTitle);
        var audioArtist = post.AudioPath is null ? null : Clean(draft.AudioArtist);

        if (content.Length == 0 && headline is null && post.ImagePath is null && post.AudioPath is null && !post.IsShare)
            return "A post can't be empty.";
        if (Validate(content, headline, tags, warnings, audioTitle, audioArtist) is { } invalid)
            return invalid;

        var rendered = PostRenderer.Render(content);
        post.Content = content;
        post.Headline = isComment ? null : headline;
        post.RenderedHtml = rendered.Html;
        post.RenderedPreviewHtml = rendered.PreviewHtml;
        post.RenderVersion = PostRenderer.Version;
        post.ContentWarnings = warnings.Count > 0 ? string.Join(", ", warnings) : null;
        post.IsAdult = !isComment && draft.IsAdult;
        post.AudioTitle = audioTitle;
        post.AudioArtist = audioArtist;
        post.EditedAt = DateTime.UtcNow;

        db.PostTags.RemoveRange(db.PostTags.Where(t => t.PostId == post.Id));
        for (var i = 0; i < tags.Count; i++) db.PostTags.Add(new PostTag { PostId = post.Id, Tag = tags[i], Position = i });
        await db.SaveChangesAsync();
        await federation.PostChangedAsync(post);
        return null;
    }

    /// <summary>Deletes a post with all its comments and their uploaded files.</summary>
    public async Task DeleteAsync(Post post)
    {
        await federation.PostDeletingAsync(post);
        var ids = await SubtreeIdsAsync(post.Id);
        var files = await AttachmentsAsync(db.Posts.Where(p => ids.Contains(p.Id) && p.RemoteId == null));
        db.Posts.Remove(post);
        await db.SaveChangesAsync();
        foreach (var file in files) uploads.Delete(file);
    }

    /// <summary>Every uploaded image and audio file attached to the given posts.</summary>
    public static async Task<List<string?>> AttachmentsAsync(IQueryable<Post> query)
    {
        var rows = await query.Where(p => p.ImagePath != null || p.AudioPath != null)
            .Select(p => new { p.ImagePath, p.AudioPath }).ToListAsync();
        return rows.SelectMany(r => new[] { r.ImagePath, r.AudioPath }).Where(f => f is not null).ToList();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
                if (post.RemoteId is null)
                {
                    var r = PostRenderer.Render(post.Content);
                    post.RenderedHtml = r.Html;
                    post.RenderedPreviewHtml = r.PreviewHtml;
                }
                else
                {
                    (post.RenderedHtml, post.RenderedPreviewHtml) = FederationService.RenderRemote(post.Content, post.RemoteId);
                }
                post.RenderVersion = PostRenderer.Version;
            }
            await db.SaveChangesAsync();
            count += batch.Count;
        }
    }

    public async Task<List<TrendingTag>> TrendingAsync(int count = 8, bool forGuest = false)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var rows = await db.PostTags.AsNoTracking()
            .Where(t => t.Post.CreatedAt >= since && !t.Post.IsHidden && !t.Post.IsAdult && !t.Post.Author.IsAdultPage)
            .Where(t => !forGuest || !t.Post.Author.HiddenFromGuests)
            .Where(t => t.Post.Author.Domain == null)
            .GroupBy(t => t.Tag)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag)
            .Take(count)
            .ToListAsync();
        return rows.Select(r => new TrendingTag(r.Tag, r.Count)).ToList();
    }
}
