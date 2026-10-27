using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Tests;

public class AudioDetectionTests
{
    private static byte[] Header(params byte[] start) => [.. start, .. new byte[16 - start.Length]];

    [Fact]
    public void RecognisesAudioBySignature()
    {
        Assert.Equal(".mp3", UploadService.DetectAudioExtension(Header("ID3"u8.ToArray())));
        Assert.Equal(".mp3", UploadService.DetectAudioExtension(Header(0xFF, 0xFB, 0x90)));
        Assert.Equal(".ogg", UploadService.DetectAudioExtension(Header("OggS"u8.ToArray())));
        Assert.Equal(".flac", UploadService.DetectAudioExtension(Header("fLaC"u8.ToArray())));
        Assert.Equal(".wav", UploadService.DetectAudioExtension("RIFF\0\0\0\0WAVEfmt "u8.ToArray()));
        Assert.Equal(".m4a", UploadService.DetectAudioExtension("\0\0\0 ftypM4A \0\0\0\0"u8.ToArray()));
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Null(UploadService.DetectAudioExtension(Header(0x89, 0x50, 0x4E, 0x47)));
        Assert.Null(UploadService.DetectAudioExtension("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(UploadService.DetectAudioExtension("<html><body>hi</b"u8.ToArray()));
        // 0xFF 0xE0 has the sync bits but a reserved layer.
        Assert.Null(UploadService.DetectAudioExtension(Header(0xFF, 0xE0)));
        Assert.Null(UploadService.DetectAudioExtension([]));
    }
}

public class NotificationGroupingTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc);
    private static int _id = 1000;

    private static UserSummary U(int id) => new(id, $"user{id}", $"User {id}", null, false, UserRole.User);

    private static NotificationRow Row(NotificationType type, int? actor, DateTime at, int? post = null, int? target = null,
        bool read = false, string? excerpt = null, string? targetExcerpt = null) =>
        new(_id--, type, read, at, actor is int a ? U(a) : null, post, target, excerpt, targetExcerpt, null);

    [Fact]
    public void MergesLikesAndSharesOfTheSamePostPerDay()
    {
        var rows = new[]
        {
            Row(NotificationType.Like, 1, Now.AddMinutes(-1), post: 10, excerpt: "my post"),
            Row(NotificationType.Like, 2, Now.AddMinutes(-2), post: 10, read: true),
            Row(NotificationType.Share, 3, Now.AddMinutes(-3), post: 50, target: 10, targetExcerpt: "my post"),
            Row(NotificationType.Like, 3, Now.AddMinutes(-4), post: 11),
            Row(NotificationType.Share, 4, Now.AddMinutes(-5), post: 51, target: 10),
            Row(NotificationType.Like, 1, Now.AddMinutes(-6), post: 10),
            Row(NotificationType.Like, 5, Now.AddDays(-1), post: 10, read: true),
        };

        var days = NotificationService.Group(rows, Now);

        Assert.Equal(["today", "yesterday"], days.Select(d => d.Label));
        var today = days[0].Groups;
        Assert.Equal(3, today.Count);

        Assert.Equal(NotificationType.Like, today[0].Type);
        Assert.Equal(10, today[0].PostId);
        Assert.Equal([1, 2], today[0].Actors.Select(a => a.Id)); // user 1 liked twice: listed once
        Assert.True(today[0].IsUnread);
        Assert.Equal("my post", today[0].PostExcerpt);

        Assert.Equal(NotificationType.Share, today[1].Type);
        Assert.Equal(10, today[1].PostId); // grouped by the shared post, not the share
        Assert.Equal([3, 4], today[1].Actors.Select(a => a.Id));

        Assert.Equal(11, today[2].PostId);
        Assert.False(days[1].Groups[0].IsUnread);
    }

    [Fact]
    public void MergesFollowersButKeepsCommentsSeparate()
    {
        var rows = new[]
        {
            Row(NotificationType.Follow, 1, Now.AddMinutes(-1)),
            Row(NotificationType.Reply, 2, Now.AddMinutes(-2), post: 20, target: 10, excerpt: "<b>nice</b>\n\npost", targetExcerpt: "mine"),
            Row(NotificationType.Follow, 3, Now.AddMinutes(-3)),
            Row(NotificationType.Reply, 2, Now.AddMinutes(-4), post: 21, target: 10),
            Row(NotificationType.System, null, Now.AddMinutes(-5)),
        };

        var groups = NotificationService.Group(rows, Now).Single().Groups;

        Assert.Equal([NotificationType.Follow, NotificationType.Reply, NotificationType.Reply, NotificationType.System], groups.Select(g => g.Type));
        Assert.Equal([1, 3], groups[0].Actors.Select(a => a.Id));
        Assert.Equal(20, groups[1].ActivityPostId);
        Assert.Equal(10, groups[1].PostId);
        Assert.Equal("nice post", groups[1].ActivityExcerpt);
        Assert.Equal("mine", groups[1].PostExcerpt);
        Assert.Empty(groups[3].Actors);
    }

    [Fact]
    public void LabelsOlderDaysWithTheDate()
    {
        var days = NotificationService.Group([Row(NotificationType.Follow, 1, new DateTime(2026, 10, 20, 9, 0, 0)), Row(NotificationType.Follow, 1, new DateTime(2025, 3, 1))], Now);
        Assert.Equal(["tuesday, october 20", "march 1, 2025"], days.Select(d => d.Label));
    }

    [Fact]
    public void ExcerptsArePlainAndShort()
    {
        Assert.Null(NotificationService.Excerpt("<div style=\"x\"></div>"));
        Assert.Equal("hello world", NotificationService.Excerpt("<p>hello</p>\n<p>world</p>"));
        Assert.Equal("fish & chips", NotificationService.Excerpt("<p><strong>fish</strong> &amp; chips</p><p><a href=\"/u/x\" cla"));
        Assert.Equal(new string('a', 79) + "…", NotificationService.Excerpt(new string('a', 200)));
    }
}
