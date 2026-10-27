using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Wistlet.Services;

/// <summary>
/// Pushes events to people's open browser tabs as Server-Sent Events (see <c>/events</c>): new
/// direct messages, typing indicators, read receipts and unread counts. Kept in memory, which
/// suits a single Wistlet process; events for people with no open tab are simply dropped.
/// </summary>
public sealed class LiveEvents
{
    /// <summary>Open streams one person may have at once (tabs, devices).</summary>
    public const int MaxStreamsPerUser = 8;

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Channel<string>>> _streams = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Opens a stream for one tab, or returns null when the person already has too many.</summary>
    public (Guid Id, ChannelReader<string> Reader)? Subscribe(int userId)
    {
        var streams = _streams.GetOrAdd(userId, _ => new());
        if (streams.Count >= MaxStreamsPerUser) return null;
        // A slow tab loses its oldest events rather than holding up everyone else.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        streams[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(int userId, Guid id)
    {
        // The (possibly empty) per-person map stays: removing it could race with a tab opening.
        if (_streams.TryGetValue(userId, out var streams) && streams.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    public bool IsOnline(int userId) => _streams.TryGetValue(userId, out var s) && !s.IsEmpty;

    /// <summary>Sends an event to every open tab of one person.</summary>
    public void Publish(int userId, string type, object data)
    {
        if (!_streams.TryGetValue(userId, out var streams) || streams.IsEmpty) return;
        var frame = Format(type, JsonSerializer.Serialize(data, Json));
        foreach (var channel in streams.Values) channel.Writer.TryWrite(frame);
    }

    /// <summary>One Server-Sent Events frame. The JSON has no line breaks, so one data line is enough.</summary>
    public static string Format(string type, string json) => $"event: {type}\ndata: {json}\n\n";
}
