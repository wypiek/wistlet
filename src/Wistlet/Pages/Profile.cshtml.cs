using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class ProfileModel(AppDbContext db, PostService posts, SiteSettingsService settings) : PageModel
{
    public User Profile { get; private set; } = null!;
    public string Tab { get; private set; } = "posts";
    public int PostCount { get; private set; }
    public int FollowerCount { get; private set; }
    public int FollowingCount { get; private set; }
    public bool IsMe { get; private set; }
    public bool IFollow { get; private set; }
    public bool FollowsMe { get; private set; }
    public bool IBlocked { get; private set; }
    public bool BlockedMe { get; private set; }
    public bool CanMessage { get; private set; }
    public List<PostView> Posts { get; private set; } = [];
    public List<UserCardModel> People { get; private set; } = [];
    public string? MoreUrl { get; private set; }

    /// <summary>The page is hidden from logged-out visitors and this is one.</summary>
    public bool NeedsLogin { get; private set; }

    /// <summary>An 18+ page the viewer hasn't confirmed their age for yet.</summary>
    public bool NeedsAdultConfirmation { get; private set; }

    /// <summary>Remembers (for the browser session) that the visitor confirmed they're 18 or older.</summary>
    public const string AdultConfirmedCookie = "wistlet.18plus";

    private static readonly string[] Tabs = ["posts", "replies", "media", "likes", "followers", "following"];

    public async Task<IActionResult> OnGetAsync(string userName, string? tab, int? before, string? adult)
    {
        var normalized = AccountService.Normalize(userName);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user is null) return NotFound();
        if (user.UserName != userName) return RedirectPermanent($"/u/{user.UserName}{(tab is null ? "" : "/" + tab)}");

        Profile = user;
        Tab = Tabs.Contains(tab) ? tab! : "posts";
        var me = User.GetUserId();
        var staff = User.IsStaff();
        IsMe = me == user.Id;

        if (user.IsCurrentlyBanned && !staff) return NotFound();

        if (user.HiddenFromGuests && me is null)
        {
            NeedsLogin = true;
            return Page();
        }

        if (user.IsAdultPage && !IsMe && !await posts.ShowsAdultAsync(me))
        {
            if (adult == "yes")
            {
                Response.Cookies.Append(AdultConfirmedCookie, "1", new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps });
                return Redirect($"/u/{user.UserName}{(tab is null ? "" : "/" + tab)}");
            }
            if (Request.Cookies[AdultConfirmedCookie] != "1")
            {
                NeedsAdultConfirmation = true;
                return Page();
            }
        }

        PostCount = await db.Posts.CountAsync(p => p.AuthorId == user.Id && !p.IsHidden);
        FollowerCount = await db.Follows.CountAsync(f => f.FolloweeId == user.Id);
        FollowingCount = await db.Follows.CountAsync(f => f.FollowerId == user.Id);

        if (me is int id && !IsMe)
        {
            IFollow = await db.Follows.AnyAsync(f => f.FollowerId == id && f.FolloweeId == user.Id);
            FollowsMe = await db.Follows.AnyAsync(f => f.FollowerId == user.Id && f.FolloweeId == id);
            IBlocked = await db.Blocks.AnyAsync(b => b.BlockerId == id && b.BlockedId == user.Id);
            BlockedMe = await db.Blocks.AnyAsync(b => b.BlockerId == user.Id && b.BlockedId == id);
            CanMessage = settings.Current.AllowDirectMessages && !IBlocked && !BlockedMe && user.MessagePrivacy switch
            {
                MessagePrivacy.Everyone => true,
                MessagePrivacy.PeopleIFollow => FollowsMe,
                _ => false,
            };
        }

        if (BlockedMe) return Page();

        var visible = posts.Visible(me, staff);
        var baseUrl = $"/u/{user.UserName}{(Tab == "posts" ? "" : "/" + Tab)}";
        switch (Tab)
        {
            case "followers":
            case "following":
                People = await LoadPeopleAsync(user.Id, Tab == "followers", me);
                return Page();
            case "likes":
                Posts = await posts.ProjectAsync(
                    visible.Where(p => p.Likes.Any(l => l.UserId == user.Id))
                        .OrderByDescending(p => db.Likes.Where(l => l.UserId == user.Id && l.PostId == p.Id).Select(l => l.CreatedAt).First()),
                    me, 100);
                return Page();
        }

        // The visitor chose to see this 18+ page, so its posts aren't filtered out as 18+ here.
        var query = Tab == "replies" || user.IsAdultPage
            ? visible.Where(p => p.AuthorId == user.Id && (Tab == "replies" ? p.ParentId != null : p.ParentId == null))
            : (await posts.FeedAsync(me, staff)).Where(p => p.AuthorId == user.Id);
        if (Tab == "media") query = query.Where(p => p.ImagePath != null || p.AudioPath != null);
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"{baseUrl}?before={n}" : null;
        return Page();
    }

    private async Task<List<UserCardModel>> LoadPeopleAsync(int userId, bool followers, int? me)
    {
        var now = DateTime.UtcNow;
        var users = followers
            ? db.Follows.Where(f => f.FolloweeId == userId).OrderByDescending(f => f.CreatedAt).Select(f => f.Follower)
            : db.Follows.Where(f => f.FollowerId == userId).OrderByDescending(f => f.CreatedAt).Select(f => f.Followee);
        var viewer = me ?? -1;
        var list = await users
            .Where(u => !(u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now)))
            .Where(u => me != null || !u.HiddenFromGuests)
            .Take(200)
            .Select(u => new
            {
                Summary = new UserSummary(u.Id, u.UserName, u.DisplayName, u.AvatarPath, u.IsVerified, u.Role, u.Bio, u.AvatarShape),
                Following = db.Follows.Any(f => f.FollowerId == viewer && f.FolloweeId == u.Id),
            })
            .ToListAsync();
        return list.Select(x => new UserCardModel(x.Summary, me is not null, x.Following)).ToList();
    }
}
