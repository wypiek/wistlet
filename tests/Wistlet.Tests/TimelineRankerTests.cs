using Wistlet.Services;

namespace Wistlet.Tests;

public class TimelineRankerTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static TimelineCandidate C(int id, int author, double hoursAgo, int likes = 0, int comments = 0, int shares = 0,
        int followeeLikes = 0, int followeeShares = 0, string[]? tags = null, bool likedByMe = false) =>
        new(id, author, Now.AddHours(-hoursAgo), likes, comments, shares, followeeLikes, followeeShares, tags ?? [], likedByMe);

    private static TimelineViewer V(int[]? following = null, Dictionary<int, int>? authors = null, Dictionary<string, int>? tags = null) =>
        new((following ?? []).ToHashSet(), authors ?? [], tags ?? []);

    [Fact]
    public void NewerPostsWinWhenEverythingElseIsEqual()
    {
        var v = V([1]);
        Assert.True(TimelineRanker.Score(C(1, 1, 1), v, Now) > TimelineRanker.Score(C(2, 1, 10), v, Now));
    }

    [Fact]
    public void FollowingAndInterestRaiseAPost()
    {
        var v = V([1], new() { [3] = 5 }, new() { ["books"] = 4 });
        var plain = TimelineRanker.Score(C(1, 2, 2), v, Now);
        Assert.True(TimelineRanker.Score(C(2, 1, 2), v, Now) > plain);
        Assert.True(TimelineRanker.Score(C(3, 3, 2), v, Now) > plain);
        Assert.True(TimelineRanker.Score(C(4, 2, 2, tags: ["books"]), v, Now) > plain);
        Assert.True(TimelineRanker.Score(C(5, 2, 2, followeeLikes: 2), v, Now) > plain);
        Assert.True(TimelineRanker.Score(C(6, 2, 2, likes: 10, comments: 3), v, Now) > plain);
        Assert.True(TimelineRanker.Score(C(7, 2, 2, likedByMe: true), v, Now) < plain);
    }

    [Fact]
    public void OldPopularPostsSinkBelowFreshOnes()
    {
        var v = V([1]);
        var oldHit = C(1, 2, 60, likes: 40, comments: 10, shares: 5);
        var fresh = C(2, 1, 1);
        Assert.Equal([2, 1], TimelineRanker.Rank([oldHit, fresh], v, Now).Select(c => c.PostId));
    }

    [Fact]
    public void SpreadsOutPostsByTheSameAuthor()
    {
        var v = V([1, 2]);
        // Author 1 posted three times just before author 2's single post.
        var ranked = TimelineRanker.Rank([C(1, 1, 1.0), C(2, 1, 1.1), C(3, 1, 1.2), C(4, 2, 1.5)], v, Now);
        Assert.Equal([1, 4, 2, 3], ranked.Select(c => c.PostId)); // author 2 comes second, not fourth
    }

    [Fact]
    public void ExplainsWhyAPostIsThere()
    {
        var v = V([1], tags: new() { ["books"] = 1 });
        Assert.Equal(TimelineReason.Followed, TimelineRanker.ReasonFor(C(1, 1, 1, followeeLikes: 3), v));
        Assert.Equal(TimelineReason.SharedByFollowee, TimelineRanker.ReasonFor(C(2, 2, 1, followeeLikes: 1, followeeShares: 1), v));
        Assert.Equal(TimelineReason.LikedByFollowee, TimelineRanker.ReasonFor(C(3, 2, 1, followeeLikes: 1), v));
        Assert.Equal(TimelineReason.Tag, TimelineRanker.ReasonFor(C(4, 2, 1, tags: ["books"]), v));
        Assert.Equal(TimelineReason.Popular, TimelineRanker.ReasonFor(C(5, 2, 1, likes: 5), v));
    }
}
