using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

public class PostService(AppDbContext db, SiteSettingsService settings, UploadService uploads, NotificationService notifications)
{
    /// <summary>
    /// Posts a viewer may see: hides moderator-hidden posts (unless staff), posts by
    /// banned users, and posts across a block in either direction.
    /// </summary>
    public IQueryable<Post> Visible(int? viewerId, bool isStaff)
    {
        var now = DateTime.UtcNow;
        var q = db.Posts.AsNoTracking()
            .Where(p => !(p.Author.IsBanned && (p.Author.BannedUntil == null || p.Author.BannedUntil > now)));
        if (!isStaff) q = q.Where(p => !p.IsHidden);
        if (viewerId is int me)
        {
            q = q.Where(p =>
                !db.Blocks.Any(b => (b.BlockerId == me && b.BlockedId == p.AuthorId) ||
                                    (b.BlockerId == p.AuthorId && b.BlockedId == me)));
        }
        return q;
    }

    public Task<List<PostView>> ProjectAsync(IQueryable<Post> query, int? viewerId, int? take = null)
    {
        if (take is int t) query = query.Take(t);
        var me = viewerId ?? -1;
        return query.Select(p => new PostView
        {
            Id = p.Id,
            Content = p.Content,
            ImagePath = p.ImagePath,
            CreatedAt = p.CreatedAt,
            EditedAt = p.EditedAt,
            IsHidden = p.IsHidden,
            IsPinned = p.IsPinned,
            ParentId = p.ParentId,
            ParentAuthorUserName = p.Parent != null ? p.Parent.Author.UserName : null,
            Author = new UserSummary(p.Author.Id, p.Author.UserName, p.Author.DisplayName, p.Author.AvatarPath, p.Author.IsVerified, p.Author.Role, ""),
            LikeCount = p.Likes.Count,
            ReplyCount = p.Replies.Count(r => !r.IsHidden),
            LikedByMe = p.Likes.Any(l => l.UserId == me),
            BookmarkedByMe = p.Bookmarks.Any(b => b.UserId == me),
        }).ToListAsync();
    }

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

    public async Task<(Post? Post, string? Error)> CreateAsync(int authorId, string? content, IFormFile? image, int? parentId)
    {
        var cfg = settings.Current;
        content = (content ?? "").Trim().Replace("\r\n", "\n");

        if (content.Length == 0 && (image is null || image.Length == 0))
            return (null, "Write something first.");
        if (content.Length > cfg.MaxPostLength)
            return (null, $"Posts can be at most {cfg.MaxPostLength} characters.");

        var banned = cfg.GetBannedWords().FirstOrDefault(w => content.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (banned is not null)
            return (null, "Your post contains a word that isn't allowed on this site.");

        Post? parent = null;
        if (parentId is int pid)
        {
            parent = await Visible(authorId, false).FirstOrDefaultAsync(p => p.Id == pid);
            if (parent is null) return (null, "The post you're replying to doesn't exist anymore.");
        }

        string? imageUrl = null;
        if (image is { Length: > 0 })
        {
            if (!cfg.AllowImageUploads) return (null, "Image uploads are currently disabled.");
            var (url, error) = await uploads.SaveImageAsync(image, "posts");
            if (error is not null) return (null, error);
            imageUrl = url;
        }

        var post = new Post { AuthorId = authorId, Content = content, ImagePath = imageUrl, ParentId = parentId };
        foreach (var tag in ContentFormatter.ExtractTags(content))
            post.Tags.Add(new PostTag { Tag = tag });
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        if (parent is not null)
            await notifications.NotifyAsync(parent.AuthorId, authorId, NotificationType.Reply, post.Id);

        var mentioned = ContentFormatter.ExtractMentions(content);
        if (mentioned.Count > 0)
        {
            var ids = await db.Users.Where(u => mentioned.Contains(u.NormalizedUserName)).Select(u => u.Id).ToListAsync();
            foreach (var id in ids.Where(id => id != parent?.AuthorId))
                await notifications.NotifyAsync(id, authorId, NotificationType.Mention, post.Id);
        }

        await db.SaveChangesAsync();
        return (post, null);
    }

    /// <summary>Deletes a post along with its uploaded image and those of its replies.</summary>
    public async Task DeleteAsync(Post post)
    {
        var images = await db.Posts.Where(p => (p.Id == post.Id || p.ParentId == post.Id) && p.ImagePath != null)
            .Select(p => p.ImagePath).ToListAsync();
        db.Posts.Remove(post);
        await db.SaveChangesAsync();
        foreach (var img in images) uploads.Delete(img);
    }

    public async Task<List<TrendingTag>> TrendingAsync(int count = 8)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var rows = await db.PostTags.AsNoTracking()
            .Where(t => t.Post.CreatedAt >= since && !t.Post.IsHidden)
            .GroupBy(t => t.Tag)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag)
            .Take(count)
            .ToListAsync();
        return rows.Select(r => new TrendingTag(r.Tag, r.Count)).ToList();
    }
}
