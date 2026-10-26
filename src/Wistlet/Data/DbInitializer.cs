using Microsoft.EntityFrameworkCore;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Data;

public class DbInitializer(AppDbContext db, AccountService accounts, IConfiguration config, ILogger<DbInitializer> logger)
{
    public async Task InitializeAsync()
    {
        await db.Database.MigrateAsync();
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

        var posts = new (int Author, string Text)[]
        {
            (0, "Welcome to #Wistlet! Follow a few people to fill up your timeline."),
            (1, "Just finished a great book. Recommendations welcome! #books"),
            (2, "Tip: use #hashtags so people can find your posts. #tips"),
            (3, "Anyone up for a puzzle swap this weekend? #books"),
            (0, "Hey @pete, did you know you can save posts for later? #tips"),
        };
        var t = DateTime.UtcNow.AddHours(-posts.Length);
        foreach (var (author, text) in posts)
        {
            var post = new Post { AuthorId = users[author].Id, Content = text, CreatedAt = t = t.AddMinutes(37) };
            foreach (var tag in ContentFormatter.ExtractTags(text)) post.Tags.Add(new PostTag { Tag = tag });
            db.Posts.Add(post);
        }
        for (var i = 1; i < users.Count; i++)
            db.Follows.Add(new Follow { FollowerId = users[i].Id, FolloweeId = users[0].Id });
        await db.SaveChangesAsync();
    }
}
