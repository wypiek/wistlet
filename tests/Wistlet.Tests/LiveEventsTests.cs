using Wistlet.Services;

namespace Wistlet.Tests;

public class LiveEventsTests
{
    [Fact]
    public async Task DeliversToEveryOpenTabOfThatPersonOnly()
    {
        var live = new LiveEvents();
        var (tab1, reader1) = live.Subscribe(1)!.Value;
        var (_, reader2) = live.Subscribe(1)!.Value;
        var (_, other) = live.Subscribe(2)!.Value;

        live.Publish(1, "dm", new { id = 5, html = "hi <b>there</b>\n" });

        var frame = await reader1.ReadAsync();
        Assert.Equal("event: dm\ndata: {\"id\":5,\"html\":\"hi \\u003Cb\\u003Ethere\\u003C/b\\u003E\\n\"}\n\n", frame);
        Assert.Equal(frame, await reader2.ReadAsync());
        Assert.False(other.TryRead(out _));

        live.Unsubscribe(1, tab1);
        Assert.True(reader1.Completion.IsCompleted);
        Assert.True(live.IsOnline(1));
    }

    [Fact]
    public void LimitsTabsPerPerson()
    {
        var live = new LiveEvents();
        var tabs = Enumerable.Range(0, LiveEvents.MaxStreamsPerUser).Select(_ => live.Subscribe(7)).ToList();
        Assert.All(tabs, t => Assert.True(t.HasValue));
        Assert.Null(live.Subscribe(7));
        live.Unsubscribe(7, tabs[0]!.Value.Id);
        Assert.NotNull(live.Subscribe(7));
    }

    [Fact]
    public void SlowTabsLoseTheirOldestEvents()
    {
        var live = new LiveEvents();
        var (_, reader) = live.Subscribe(3)!.Value;
        for (var i = 0; i < 100; i++) live.Publish(3, "unread", new { count = i });
        Assert.True(reader.TryRead(out var first));
        Assert.Contains("\"count\":36", first);
        Assert.False(live.IsOnline(99));
        live.Publish(99, "dm", new { }); // nobody listening: nothing happens
    }
}
