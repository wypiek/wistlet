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

    /// <summary>cohost's profile filters: transparent shares, and shares with something added ("replies").</summary>
    public bool HideShares { get; private set; }
    public bool HideReplies { get; private set; }
    /// <summary>On the "tagged" tab: the tag the posts are filtered by.</summary>
    public string? Tagged { get; private set; }
    /// <summary>The tags this account uses most, shown like cohost's pinned tags.</summary>
    public List<string> PinnedTags { get; private set; } = [];

    private static readonly string[] Tabs = ["posts", "replies", "media", "likes", "followers", "following", "tagged"];

    public string FilterUrl(bool hideShares, bool hideReplies)
    {
        var q = new List<string>();
        if (hideShares) q.Add("hideShares=true");
        if (hideReplies) q.Add("hideReplies=true");
        return $"/u/{Profile.UserName}" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    public async Task<IActionResult> OnGetAsync(string userName, string? tab, string? tag, int? before, bool hideShares, bool hideReplies)
    {
        var normalized = AccountService.Normalize(userName);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user is null) return NotFound();
        if (user.UserName != userName)
            return RedirectPermanent($"/u/{user.UserName}{(tab is null ? "" : "/" + tab)}{(tag is null ? "" : "/" + Uri.EscapeDataString(tag))}{Request.QueryString}");

        Profile = user;
        Tab = Tabs.Contains(tab) ? tab! : "posts";
        if (Tab == "tagged")
        {
            Tagged = tag?.Trim().TrimStart('#').ToLowerInvariant();
            if (string.IsNullOrEmpty(Tagged)) return Redirect($"/u/{user.UserName}");
        }
        HideShares = hideShares;
        HideReplies = hideReplies;
        var me = User.GetUserId();
        var staff = User.IsStaff();
        IsMe = me == user.Id;

        if (user.IsCurrentlyBanned && !staff) return NotFound();

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
        PinnedTags = await db.PostTags
            .Where(t => t.Post.AuthorId == user.Id && t.Post.ParentId == null && !t.Post.IsHidden)
            .GroupBy(t => t.Tag)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .Take(10)
            .ToListAsync();
        var baseUrl = Tab switch
        {
            "posts" => FilterUrl(HideShares, HideReplies),
            "tagged" => $"/u/{user.UserName}/tagged/{Uri.EscapeDataString(Tagged!)}",
            _ => $"/u/{user.UserName}/{Tab}",
        };
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

        var query = Tab == "replies"
            ? visible.Where(p => p.AuthorId == user.Id && p.ParentId != null)
            : (await posts.FeedAsync(me, staff)).Where(p => p.AuthorId == user.Id);
        if (Tab == "media") query = query.Where(p => p.ImagePath != null);
        if (Tab == "tagged") query = query.Where(p => p.Tags.Any(t => t.Tag == Tagged));
        if (Tab == "posts")
        {
            // A share with nothing added is a "share"; one with its own text, headline or image is a "reply".
            if (HideShares)
                query = query.Where(p => !(p.IsShare && p.Content == "" && (p.Headline == null || p.Headline == "") && p.ImagePath == null));
            if (HideReplies)
                query = query.Where(p => !(p.IsShare && !(p.Content == "" && (p.Headline == null || p.Headline == "") && p.ImagePath == null)));
        }
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"{baseUrl}{(baseUrl.Contains('?') ? '&' : '?')}before={n}" : null;
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
