using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class ProfileModel(AppDbContext db, PostService posts, SiteSettingsService settings, TagService tagService) : PageModel
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
    public bool HideAsks { get; private set; }

    /// <summary>Whether the viewer may send this page an ask, and whether it has to be anonymous.</summary>
    public bool CanAsk { get; private set; }
    public bool AskMustBeAnonymous { get; private set; }

    /// <summary>Follow request sent and waiting for approval.</summary>
    public bool Requested { get; private set; }

    /// <summary>Private page the viewer can't see into.</summary>
    public bool IsLockedOut { get; private set; }

    public List<ContactCardItem> ContactCard { get; private set; } = [];
    /// <summary>On the "tagged" tab: the tag the posts are filtered by.</summary>
    public string? Tagged { get; private set; }
    /// <summary>The tags this account uses most, shown like cohost's pinned tags.</summary>
    public List<string> PinnedTags { get; private set; } = [];

    private static readonly string[] Tabs = ["posts", "replies", "media", "likes", "followers", "following", "tagged"];

    public string FilterUrl(bool hideShares, bool hideReplies, bool hideAsks)
    {
        var q = new List<string>();
        if (hideShares) q.Add("hideShares=true");
        if (hideReplies) q.Add("hideReplies=true");
        if (hideAsks) q.Add("hideAsks=true");
        return $"/u/{Profile.UserName}" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    public async Task<IActionResult> OnGetAsync(string userName, string? tab, string? tag, int? before, bool hideShares, bool hideReplies, bool hideAsks)
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
        HideAsks = hideAsks;
        var me = User.GetUserId();
        var staff = User.IsStaff();
        IsMe = me == user.Id;

        if (user.IsCurrentlyBanned && !staff) return NotFound();
        // An extra page disappears along with its account when the account is banned.
        if (user.OwnerId is int owner && !staff && await db.Users.AnyAsync(u => u.Id == owner && u.IsBanned && (u.BannedUntil == null || u.BannedUntil > DateTime.UtcNow)))
            return NotFound();

        PostCount = await db.Posts.CountAsync(p => p.AuthorId == user.Id && !p.IsHidden && !p.IsDraft && p.ParentId == null);
        FollowerCount = await db.Follows.CountAsync(f => f.FolloweeId == user.Id);
        FollowingCount = await db.Follows.CountAsync(f => f.FollowerId == user.Id);

        if (me is int id && !IsMe)
        {
            IFollow = await db.Follows.AnyAsync(f => f.FollowerId == id && f.FolloweeId == user.Id);
            FollowsMe = await db.Follows.AnyAsync(f => f.FollowerId == user.Id && f.FolloweeId == id);
            IBlocked = await db.Blocks.AnyAsync(b => b.BlockerId == id && b.BlockedId == user.Id);
            Requested = !IFollow && await db.FollowRequests.AnyAsync(f => f.FollowerId == id && f.FolloweeId == user.Id);
            BlockedMe = await db.Blocks.AnyAsync(b => b.BlockerId == user.Id && b.BlockedId == id);
            CanMessage = settings.Current.AllowDirectMessages && !IBlocked && !BlockedMe && user.MessagePrivacy switch
            {
                MessagePrivacy.Everyone => true,
                MessagePrivacy.PeopleIFollow => FollowsMe,
                _ => false,
            };
        }

        // Contact card lines marked followers-only show to followers (and the owner).
        ContactCard = ContactCardItem.Parse(user.ContactCard).Where(c => !c.FollowersOnly || IsMe || IFollow || staff).ToList();
        if (user.AsksEnabled && !IsMe && !IBlocked && !BlockedMe)
        {
            CanAsk = me is not null || user.LoggedOutAsksAllowed;
            AskMustBeAnonymous = me is null;
        }
        IsLockedOut = user.IsPrivate && !IsMe && !IFollow && !staff;

        if (BlockedMe || IsLockedOut) return Page();

        var visible = posts.Visible(me, staff);
        // Pinned tags: the ones the page chose, or else the ones it uses most.
        PinnedTags = PostRenderer.ParseTags(user.PinnedTags).ToList();
        if (PinnedTags.Count == 0)
        {
            var used = await db.PostTags
                .Where(t => t.Post.AuthorId == user.Id && t.Post.ParentId == null && !t.Post.IsHidden && !t.Post.IsDraft)
                .GroupBy(t => t.Tag)
                .Select(g => new { Tag = g.Key, Count = g.Count() })
                .ToListAsync();
            PinnedTags = used.GroupBy(u => tagService.Canonical(u.Tag))
                .Select(g => new { Tag = g.Key, Count = g.Sum(u => u.Count) })
                .OrderByDescending(t => t.Count).ThenBy(t => t.Tag)
                .Select(t => t.Tag).Take(10).ToList();
        }
        var baseUrl = Tab switch
        {
            "posts" => FilterUrl(HideShares, HideReplies, HideAsks),
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
        if (Tab == "media") query = query.Where(p => p.Attachments.Any());
        if (Tab == "tagged")
        {
            var group = tagService.Group(Tagged!);
            query = query.Where(p => p.Tags.Any(t => group.Contains(t.Tag)));
        }
        if (Tab == "posts")
        {
            // A share with nothing added is a "share"; one with its own text, headline or image is a "reply".
            if (HideShares)
                query = query.Where(p => !(p.IsShare && p.Content == "" && p.Headline == null && p.AskId == null && !p.Attachments.Any()));
            if (HideReplies)
                query = query.Where(p => !(p.IsShare && !(p.Content == "" && p.Headline == null && p.AskId == null && !p.Attachments.Any())));
            if (HideAsks)
                query = query.Where(p => p.AskId == null);
        }

        // Pinned posts come first, on the first page of the plain post list.
        var pinned = new List<PostView>();
        if (Tab == "posts")
        {
            if (before is null)
                pinned = await posts.ProjectAsync(query.Where(p => p.PinnedToProfile).OrderByDescending(p => p.Id), me, 10);
            query = query.Where(p => !p.PinnedToProfile);
        }
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = [.. pinned, .. page];
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
