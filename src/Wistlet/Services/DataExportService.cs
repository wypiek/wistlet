using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;

namespace Wistlet.Services;

/// <summary>
/// Builds a zip of everything a user has put on the site: profile, posts and comments (Markdown
/// source and rendered HTML), likes, saved posts, follows, blocks, direct messages and the files
/// they uploaded.
/// </summary>
public class DataExportService(AppDbContext db, UploadService uploads)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcDateTimeConverter() },
    };

    /// <summary>Stored times are UTC but come back from SQLite without a kind; write them with the "Z".</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTime().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    public async Task WriteZipAsync(int userId, Stream output, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var files = new List<string?> { user.AvatarPath, user.BannerPath };

        var posts = await db.Posts.AsNoTracking()
            .Where(p => p.AuthorId == userId)
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                Kind = p.ParentId != null ? "comment" : p.IsShare ? "share" : "post",
                Url = $"/post/{p.Id}",
                p.CreatedAt,
                p.EditedAt,
                p.Headline,
                Markdown = p.Content,
                Html = p.RenderedHtml,
                Tags = p.Tags.OrderBy(t => t.Position).Select(t => t.Tag).ToList(),
                p.ContentWarnings,
                p.IsAdult,
                Image = p.ImagePath,
                Audio = p.AudioPath == null ? null : new { File = p.AudioPath, Title = p.AudioTitle, Artist = p.AudioArtist },
                CommentOn = p.ParentId,
                SharedPost = p.SharedPostId,
                HiddenByModerator = p.IsHidden,
                Likes = p.Likes.Count,
            })
            .ToListAsync(ct);
        files.AddRange(posts.Select(p => p.Image));
        files.AddRange(posts.Select(p => p.Audio?.File));

        var profile = new
        {
            user.UserName,
            user.DisplayName,
            user.Email,
            user.Pronouns,
            Tagline = user.Dek,
            Description = user.Bio,
            user.Location,
            user.Website,
            Avatar = user.AvatarPath,
            user.AvatarShape,
            Banner = user.BannerPath,
            Role = user.Role.ToString(),
            user.IsVerified,
            user.CreatedAt,
            Settings = new
            {
                MessagePrivacy = user.MessagePrivacy.ToString(),
                user.ShowAdultContent,
                user.IsAdultPage,
                user.HiddenFromGuests,
                user.TwoFactorEnabled,
            },
        };

        var likes = await db.Likes.AsNoTracking().Where(l => l.UserId == userId).OrderBy(l => l.CreatedAt)
            .Select(l => new { Post = l.PostId, Author = l.Post.Author.UserName, l.CreatedAt }).ToListAsync(ct);
        var saved = await db.Bookmarks.AsNoTracking().Where(b => b.UserId == userId).OrderBy(b => b.CreatedAt)
            .Select(b => new { Post = b.PostId, Author = b.Post.Author.UserName, b.CreatedAt }).ToListAsync(ct);
        var following = await db.Follows.AsNoTracking().Where(f => f.FollowerId == userId).OrderBy(f => f.CreatedAt)
            .Select(f => new { f.Followee.UserName, f.CreatedAt }).ToListAsync(ct);
        var followers = await db.Follows.AsNoTracking().Where(f => f.FolloweeId == userId).OrderBy(f => f.CreatedAt)
            .Select(f => new { f.Follower.UserName, f.CreatedAt }).ToListAsync(ct);
        var blocked = await db.Blocks.AsNoTracking().Where(b => b.BlockerId == userId).OrderBy(b => b.CreatedAt)
            .Select(b => new { b.Blocked.UserName, b.CreatedAt }).ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.SenderId == userId || m.RecipientId == userId)
            .OrderBy(m => m.Id)
            .Select(m => new { From = m.Sender.UserName, To = m.Recipient.UserName, m.Body, m.CreatedAt, m.ReadAt })
            .ToListAsync(ct);

        // Zip writes are partly synchronous, which the response stream doesn't allow, and uploads
        // can add up to a lot, so the archive is built in a temporary file first.
        await using var buffer = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddJsonAsync(zip, "profile.json", profile, ct);
            await AddJsonAsync(zip, "posts.json", posts, ct);
            await AddJsonAsync(zip, "likes.json", likes, ct);
            await AddJsonAsync(zip, "saved-posts.json", saved, ct);
            await AddJsonAsync(zip, "following.json", following, ct);
            await AddJsonAsync(zip, "followers.json", followers, ct);
            await AddJsonAsync(zip, "blocked.json", blocked, ct);
            await AddJsonAsync(zip, "messages.json", messages, ct);

            foreach (var url in files.Where(f => f is not null).Distinct())
            {
                if (uploads.PathFor(url) is not { } path || !File.Exists(path)) continue;
                // Keep the site's path, so "/uploads/posts/x.png" in posts.json is "uploads/posts/x.png" in the zip.
                var entry = zip.CreateEntry(url!.TrimStart('/'), CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync(ct);
                await using var source = File.OpenRead(path);
                await source.CopyToAsync(target, ct);
            }

            var readme = zip.CreateEntry("README.txt");
            await using (var w = new StreamWriter(await readme.OpenAsync(ct)))
            {
                await w.WriteAsync(
                    $"Data export for @{user.UserName}, made {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n\n" +
                    "profile.json      your profile and settings\n" +
                    "posts.json        your posts, shares and comments (Markdown source and rendered HTML)\n" +
                    "likes.json        posts you liked\n" +
                    "saved-posts.json  posts you saved\n" +
                    "following.json    accounts you follow\n" +
                    "followers.json    accounts that follow you\n" +
                    "blocked.json      accounts you blocked\n" +
                    "messages.json     direct messages you sent and received\n" +
                    "uploads/          your pictures, banner, images and audio\n");
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(output, ct);
    }

    private static async Task AddJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = await entry.OpenAsync(ct);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
    }
}
