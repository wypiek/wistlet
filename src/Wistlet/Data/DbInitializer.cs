using Microsoft.EntityFrameworkCore;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Data;

public class DbInitializer(AppDbContext db, AccountService accounts, PostService posts, EmojiService emoji, IConfiguration config, ILogger<DbInitializer> logger)
{
    public async Task InitializeAsync()
    {
        await db.Database.MigrateAsync();
        await SeedEmojiAsync();
        await emoji.ReloadAsync();
        var rerendered = await posts.RerenderOutdatedAsync();
        if (rerendered > 0) logger.LogInformation("Re-rendered {Count} posts with post renderer v{Version}", rerendered, PostRenderer.Version);
        await EnsureAdminAsync();
        if (config.GetValue("Wistlet:SeedDemoContent", false) && !await db.Posts.AnyAsync())
            await SeedDemoAsync();
    }

    /// <summary>A few starter emoji on a new site (once; admins can remove them).</summary>
    private async Task SeedEmojiAsync()
    {
        const string key = "EmojiSeeded";
        if (await db.SiteSettings.AnyAsync(s => s.Key == key)) return;
        foreach (var code in new[] { "egg", "egg-crack", "cherry-heart", "sparkle" })
            if (!await db.CustomEmoji.AnyAsync(e => e.Shortcode == code))
                db.CustomEmoji.Add(new CustomEmoji { Shortcode = code, Path = $"/img/emoji/{code}.svg" });
        db.SiteSettings.Add(new SiteSetting { Key = key, Value = "true" });
        await db.SaveChangesAsync();
    }

    /// <summary>Creates (or promotes) the admin account named in configuration, if any.</summary>
    private async Task EnsureAdminAsync()
    {
        var userName = config["Wistlet:AdminUser"];
        var password = config["Wistlet:AdminPassword"];
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) return;

        var existing = await accounts.FindByUserNameAsync(userName);
        if (existing is null)
        {
            if (AccountService.ValidatePassword(password) is { } pwError)
            {
                logger.LogWarning("Not creating admin {User}: {Error}", userName, pwError);
                return;
            }
            await accounts.CreateUserAsync(userName, config["Wistlet:AdminEmail"] ?? "", password, role: UserRole.Admin);
            logger.LogInformation("Created admin account {User}", userName);
        }
        else if (existing.Role != UserRole.Admin)
        {
            existing.Role = UserRole.Admin;
            AccountService.RotateSecurityStamp(existing);
            await db.SaveChangesAsync();
            logger.LogInformation("Promoted {User} to admin", userName);
        }
    }

    private async Task SeedDemoAsync()
    {
        logger.LogInformation("Seeding demo content");
        var demo = new (string User, string Name, string Bio)[]
        {
            ("annie", "Annie A.", "Coffee, code and long walks."),
            ("pete", "Pete P.", "Writes about books and bikes."),
            ("mo", "Mo M.", "Music nerd."),
            ("sol", "Sol S.", "Card games and puzzles."),
        };
        var users = new List<User>();
        foreach (var (u, name, bio) in demo)
        {
            var user = await accounts.FindByUserNameAsync(u)
                       ?? await accounts.CreateUserAsync(u, $"{u}@example.com", AccountService.GenerateTemporaryPassword(), name, UserRole.User);
            user.Bio = bio;
            users.Add(user);
        }
        users[0].IsVerified = true;

        var demoPosts = new (int Author, string? Headline, string Text, string Tags)[]
        {
            (0, "welcome to wistlet!", "Follow a few people to fill up your timeline.\n\nPosts are **Markdown**, and HTML with inline styles works too.", "wistlet, welcome"),
            (1, null, "Just finished a great book. Recommendations welcome!", "books"),
            (2, null, "Tip: tags go in the tags box under your post, and they can have spaces in them.", "tips, how to use wistlet"),
            (3, "a tiny css crime", CssCrimeDemo, "css crimes, the cohost global feed"),
            (0, null, "Hey @pete, did you know you can save posts for later?", "tips"),
            (2, null, "the first video ever uploaded to youtube. a link on its own line becomes an embed:\n\nhttps://www.youtube.com/watch?v=jNQXAC9IVRw", "music, history"),
        };
        var t = DateTime.UtcNow.AddHours(-demoPosts.Length);
        foreach (var (author, headline, text, tags) in demoPosts)
        {
            var rendered = posts.Render(text);
            var post = new Post
            {
                AuthorId = users[author].Id, Headline = headline, Content = text, CreatedAt = t = t.AddMinutes(37),
                RenderedHtml = rendered.Html, RenderedPreviewHtml = rendered.PreviewHtml, RenderVersion = PostRenderer.Version,
                EmbedUrls = string.Join('\n', rendered.EmbedUrls),
            };
            var parsed = PostRenderer.ParseTags(tags);
            for (var i = 0; i < parsed.Count; i++) post.Tags.Add(new PostTag { Tag = parsed[i], Position = i });
            db.Posts.Add(post);
        }
        for (var i = 1; i < users.Count; i++)
            db.Follows.Add(new Follow { FollowerId = users[i].Id, FolloweeId = users[0].Id });
        await db.SaveChangesAsync();
    }

    /// <summary>A small post in cohost's "CSS crimes" tradition: inline styles, details/summary and the built-in spin keyframes.</summary>
    private const string CssCrimeDemo = """
        <div style="display: grid; place-items: center; padding: 1.5rem; border-radius: 1rem; background: linear-gradient(135deg, rgb(255 216 168), rgb(229 107 111));">
        <details style="cursor: pointer;">
        <summary style="list-style: none; font-weight: bold; color: rgb(131 37 79);">press the egg</summary>
        <div style="margin-top: 1rem; width: 4rem; height: 5rem; border-radius: 50% 50% 50% 50% / 60% 60% 40% 40%; background: rgb(255 249 242); box-shadow: 0 4px 10px rgb(0 0 0 / 0.3); animation: spin 3s linear infinite;"></div>
        </details>
        </div>
        """;
}
