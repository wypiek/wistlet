using Microsoft.EntityFrameworkCore;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Data;

public class DbInitializer(AppDbContext db, AccountService accounts, PostService posts, IConfiguration config, ILogger<DbInitializer> logger)
{
    public async Task InitializeAsync()
    {
        await db.Database.MigrateAsync();
        var rerendered = await posts.RerenderOutdatedAsync();
        if (rerendered > 0) logger.LogInformation("Re-rendered {Count} posts with post renderer v{Version}", rerendered, PostRenderer.Version);
        await EnsureAdminAsync();
        if (config.GetValue("Wistlet:SeedDemoContent", false) && !await db.Posts.AnyAsync())
            await SeedDemoAsync();
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
            (2, "the haunted vending machine", VendingMachineDemo, "css crimes"),
            (0, null, "Hey @pete, did you know you can save posts for later?", "tips"),
        };
        var t = DateTime.UtcNow.AddHours(-demoPosts.Length);
        foreach (var (author, headline, text, tags) in demoPosts)
        {
            var rendered = PostRenderer.Render(text);
            var post = new Post
            {
                AuthorId = users[author].Id, Headline = headline, Content = text, CreatedAt = t = t.AddMinutes(37),
                RenderedHtml = rendered.Html, RenderedPreviewHtml = rendered.PreviewHtml, RenderVersion = PostRenderer.Version,
            };
            var parsed = PostRenderer.ParseTags(tags);
            for (var i = 0; i < parsed.Count; i++) post.Tags.Add(new PostTag { Tag = parsed[i], Position = i });
            db.Posts.Add(post);
        }
        for (var i = 1; i < users.Count; i++)
            db.Follows.Add(new Follow { FollowerId = users[i].Id, FolloweeId = users[0].Id });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A CSS crime written the way people write HTML: nested, indented, with blank lines between
    /// elements. (Plain Markdown would show everything after the first blank line as code.)
    /// </summary>
    private const string VendingMachineDemo = """
        <div style="display: grid; gap: 12px; padding: 16px; border-radius: 16px; background: linear-gradient(135deg, rgb(255 216 168), rgb(229 107 111));">

            <div style="font-size: 24px; font-weight: bold; color: rgb(131 37 79);">
                the haunted vending machine
            </div>

            <details style="background: rgb(255 249 242); color: rgb(25 25 25); border-radius: 12px; padding: 8px 12px;">
                <summary style="cursor: pointer; font-weight: bold;">insert coin</summary>

                <div style="display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px;">
                    <span style="padding: 4px 10px; border-radius: 999px; background: rgb(131 37 79); color: rgb(255 249 242);">soda</span>
                    <span style="padding: 4px 10px; border-radius: 999px; background: rgb(255 171 92);">chips</span>
                    <span style="padding: 4px 10px; border-radius: 999px; background: rgb(25 25 25); color: rgb(255 249 242); animation: bounce 1s infinite;">???</span>
                </div>

                <p style="margin: 8px 0 0;">the machine hums. something moves inside.</p>
            </details>

            <div style="text-align: right; font-size: 12px; color: rgb(81 17 46);">out of order since 2022</div>
        </div>
        """;

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
