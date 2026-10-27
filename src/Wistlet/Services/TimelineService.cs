using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

/// <summary>Why a post is in someone's "for you" timeline.</summary>
public enum TimelineReason
{
    Followed,
    LikedByFollowee,
    SharedByFollowee,
    Tag,
    Popular,
}

/// <summary>Everything the ranking looks at for one post.</summary>
public record TimelineCandidate(
    int PostId,
    int AuthorId,
    DateTime CreatedAt,
    int Likes,
    int Comments,
    int Shares,
    int FolloweeLikes,
    int FolloweeShares,
    IReadOnlyList<string> Tags,
    bool LikedByMe);

/// <summary>What the ranking knows about the viewer.</summary>
public record TimelineViewer(
    IReadOnlySet<int> Following,
    IReadOnlyDictionary<int, int> AuthorInteractions,
    IReadOnlyDictionary<string, int> TagInteractions);

/// <summary>
/// The optional "for you" ranking. A post's score is its weight divided by a power of its age
/// (as on Hacker News), so new posts rise and old ones sink no matter how popular they were.
/// The weight adds up, on logarithmic scales so no single signal dominates:
/// following the author; the viewer's own recent likes and comments on the author's posts;
/// likes and shares by people the viewer follows; tags the viewer has liked before;
/// and the post's overall likes, comments and shares.
/// </summary>
public static class TimelineRanker
{
    private const double Gravity = 1.4;
    private const double FollowBonus = 3;
    private const double RepeatAuthorPenalty = 0.7;

    public static double Weight(TimelineCandidate c, TimelineViewer v)
    {
        var weight = 1.0;
        if (v.Following.Contains(c.AuthorId)) weight += FollowBonus;
        weight += Math.Log(1 + c.Likes + 2.0 * c.Comments + 3.0 * c.Shares);
        weight += Math.Log(1 + v.AuthorInteractions.GetValueOrDefault(c.AuthorId));
        weight += 1.5 * Math.Log(1 + c.FolloweeLikes) + 2 * Math.Log(1 + c.FolloweeShares);
        weight += Math.Min(2, 0.5 * c.Tags.Sum(t => Math.Log(1 + v.TagInteractions.GetValueOrDefault(t))));
        // Posts the viewer already liked have been seen; let others go first.
        if (c.LikedByMe) weight *= 0.5;
        return weight;
    }

    public static double Score(TimelineCandidate c, TimelineViewer v, DateTime nowUtc)
    {
        var ageHours = Math.Max(0, (nowUtc - c.CreatedAt).TotalHours);
        return Weight(c, v) / Math.Pow(ageHours + 2, Gravity);
    }

    /// <summary>
    /// Orders candidates by score, softening runs of the same author: each further post by an
    /// author already placed counts for 70% of the one before.
    /// </summary>
    public static List<TimelineCandidate> Rank(IEnumerable<TimelineCandidate> candidates, TimelineViewer v, DateTime nowUtc)
    {
        var remaining = candidates.Select(c => (Candidate: c, Score: Score(c, v, nowUtc))).ToList();
        var placed = new Dictionary<int, int>();
        var result = new List<TimelineCandidate>(remaining.Count);
        while (remaining.Count > 0)
        {
            var best = 0;
            var bestScore = double.MinValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var s = remaining[i].Score * Math.Pow(RepeatAuthorPenalty, placed.GetValueOrDefault(remaining[i].Candidate.AuthorId));
                if (s > bestScore || (s == bestScore && remaining[i].Candidate.PostId > remaining[best].Candidate.PostId))
                {
                    best = i;
                    bestScore = s;
                }
            }
            var pick = remaining[best].Candidate;
            remaining.RemoveAt(best);
            placed[pick.AuthorId] = placed.GetValueOrDefault(pick.AuthorId) + 1;
            result.Add(pick);
        }
        return result;
    }

    /// <summary>The main reason a post was picked, for the "why am I seeing this" line.</summary>
    public static TimelineReason ReasonFor(TimelineCandidate c, TimelineViewer v)
    {
        if (v.Following.Contains(c.AuthorId)) return TimelineReason.Followed;
        if (c.FolloweeShares > 0) return TimelineReason.SharedByFollowee;
        if (c.FolloweeLikes > 0) return TimelineReason.LikedByFollowee;
        if (c.Tags.Any(v.TagInteractions.ContainsKey)) return TimelineReason.Tag;
        return TimelineReason.Popular;
    }
}

/// <summary>Builds the "for you" timeline from the database.</summary>
public class TimelineService(AppDbContext db, PostService posts, SiteSettingsService settings)
{
    /// <summary>Only posts this recent are ranked.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);

    /// <summary>How far back the viewer's likes and comments count as interest.</summary>
    private static readonly TimeSpan InterestWindow = TimeSpan.FromDays(30);

    private const int MaxCandidates = 1000;

    public async Task<(List<PostView> Posts, bool HasMore)> ForYouAsync(int viewerId, bool isStaff, int page)
    {
        var now = DateTime.UtcNow;
        var since = now - Window;
        var interestSince = now - InterestWindow;

        // A list (not a set) so EF can pass it to SQL.
        var following = await db.Follows.Where(f => f.FollowerId == viewerId).Select(f => f.FolloweeId).ToListAsync();

        var likedAuthors = await db.Likes.Where(l => l.UserId == viewerId && l.CreatedAt >= interestSince)
            .GroupBy(l => l.Post.AuthorId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var commentedAuthors = await db.Posts.Where(p => p.AuthorId == viewerId && p.RootId != null && p.CreatedAt >= interestSince)
            .Join(db.Posts, c => c.RootId, r => r.Id, (c, r) => r.AuthorId)
            .GroupBy(a => a).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var authorInteractions = new Dictionary<int, int>();
        foreach (var x in likedAuthors.Concat(commentedAuthors).Where(x => x.Key != viewerId))
            authorInteractions[x.Key] = authorInteractions.GetValueOrDefault(x.Key) + x.Count;

        var tagInteractions = await db.Likes.Where(l => l.UserId == viewerId && l.CreatedAt >= interestSince)
            .SelectMany(l => l.Post.Tags).GroupBy(t => t.Tag)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(50)
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        var likedTags = tagInteractions.Keys.ToList();

        var viewer = new TimelineViewer(following.ToHashSet(), authorInteractions, tagInteractions);

        // Recent posts from people the viewer follows, posts they liked or shared, posts in tags
        // the viewer likes, and posts other people are responding to. Shares that add nothing are
        // left out: the post they point at stands in for them.
        var feed = await posts.FeedAsync(viewerId, isStaff);
        var candidates = await feed
            .Where(p => p.CreatedAt >= since && p.AuthorId != viewerId)
            .Where(p => !(p.IsShare && p.Content == "" && p.Headline == null && p.ImagePath == null && p.AudioPath == null))
            .Where(p => following.Contains(p.AuthorId)
                        || p.Likes.Any(l => following.Contains(l.UserId))
                        || db.Posts.Any(s => s.SharedPostId == p.Id && following.Contains(s.AuthorId))
                        || p.Tags.Any(t => likedTags.Contains(t.Tag))
                        || p.Likes.Count + db.Posts.Count(c => c.RootId == p.Id) >= 2)
            .OrderByDescending(p => p.Id)
            .Take(MaxCandidates)
            .Select(p => new TimelineCandidate(
                p.Id,
                p.AuthorId,
                p.CreatedAt,
                p.Likes.Count,
                db.Posts.Count(c => c.RootId == p.Id && !c.IsHidden && !c.HiddenByPostAuthor),
                db.Posts.Count(s => s.SharedPostId == p.Id),
                p.Likes.Count(l => following.Contains(l.UserId)),
                db.Posts.Count(s => s.SharedPostId == p.Id && following.Contains(s.AuthorId)),
                p.Tags.Select(t => t.Tag).ToList(),
                p.Likes.Any(l => l.UserId == viewerId)))
            .ToListAsync();

        var ranked = TimelineRanker.Rank(candidates, viewer, now);
        var size = settings.Current.PostsPerPage;
        var pageItems = ranked.Skip(page * size).Take(size).ToList();
        var ids = pageItems.Select(c => c.PostId).ToList();

        var views = (await posts.ProjectAsync(feed.Where(p => ids.Contains(p.Id)), viewerId)).ToDictionary(p => p.Id);
        var reasons = await DescribeAsync(pageItems, viewer);
        var result = new List<PostView>();
        foreach (var c in pageItems)
        {
            if (!views.TryGetValue(c.PostId, out var view)) continue;
            view.Reason = reasons.GetValueOrDefault(c.PostId);
            result.Add(view);
        }
        return (result, ranked.Count > (page + 1) * size);
    }

    /// <summary>"liked by @pete", "because you like #books" and so on, for posts from people the viewer doesn't follow.</summary>
    private async Task<Dictionary<int, string>> DescribeAsync(List<TimelineCandidate> items, TimelineViewer viewer)
    {
        var reasons = items.ToDictionary(c => c.PostId, c => TimelineRanker.ReasonFor(c, viewer));
        var following = viewer.Following.ToList();

        var likedIds = reasons.Where(r => r.Value == TimelineReason.LikedByFollowee).Select(r => r.Key).ToList();
        var likers = (await db.Likes.Where(l => likedIds.Contains(l.PostId) && following.Contains(l.UserId))
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new { l.PostId, l.User.UserName }).ToListAsync())
            .GroupBy(x => x.PostId).ToDictionary(g => g.Key, g => g.First().UserName);

        var sharedIds = reasons.Where(r => r.Value == TimelineReason.SharedByFollowee).Select(r => r.Key).ToList();
        var sharers = (await db.Posts.Where(s => s.SharedPostId != null && sharedIds.Contains(s.SharedPostId.Value) && following.Contains(s.AuthorId))
                .OrderByDescending(s => s.Id)
                .Select(s => new { PostId = s.SharedPostId!.Value, s.Author.UserName }).ToListAsync())
            .GroupBy(x => x.PostId).ToDictionary(g => g.Key, g => g.First().UserName);

        var text = new Dictionary<int, string>();
        foreach (var c in items)
        {
            var line = reasons[c.PostId] switch
            {
                TimelineReason.LikedByFollowee when likers.TryGetValue(c.PostId, out var u) => $"liked by @{u}",
                TimelineReason.SharedByFollowee when sharers.TryGetValue(c.PostId, out var u) => $"shared by @{u}",
                TimelineReason.Tag => $"because you like #{c.Tags.Where(viewer.TagInteractions.ContainsKey).MaxBy(t => viewer.TagInteractions[t])}",
                TimelineReason.Popular => $"popular on {settings.Current.SiteName.ToLowerInvariant()}",
                _ => null,
            };
            if (line is not null) text[c.PostId] = line;
        }
        return text;
    }
}
