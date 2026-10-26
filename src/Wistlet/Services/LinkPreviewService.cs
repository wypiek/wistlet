using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

/// <summary>
/// Fetches previews (title, description, image) for links posted on their own line, in the background.
/// The server only ever talks to public addresses on ports 80/443: every connection's IP is checked
/// when it's made (so redirects and DNS tricks can't reach the local network), responses are capped
/// in size and time, and only HTML is read.
/// </summary>
public partial class LinkPreviewService : BackgroundService
{
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);
    private const int MaxBytes = 1024 * 1024;

    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly IServiceScopeFactory scopes;
    private readonly SiteSettingsService settings;
    private readonly ILogger<LinkPreviewService> logger;
    private readonly HttpClient http;

    public LinkPreviewService(IServiceScopeFactory scopes, SiteSettingsService settings, IConfiguration config, ILogger<LinkPreviewService> logger)
    {
        this.scopes = scopes;
        this.settings = settings;
        this.logger = logger;
        // Only for local testing: lets previews reach private addresses and other ports.
        var allowPrivate = config.GetValue("Wistlet:LinkPreviewsAllowPrivateAddresses", false);

        var handler = new SocketsHttpHandler
        {
            // A proxy would make the address check meaningless, so connections are always direct.
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (ctx, ct) =>
            {
                if (!allowPrivate && ctx.DnsEndPoint.Port is not (80 or 443))
                    throw new HttpRequestException($"Port {ctx.DnsEndPoint.Port} isn't allowed for link previews.");
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => allowPrivate || IsPublicAddress(a)).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException($"{ctx.DnsEndPoint.Host} doesn't resolve to a public address.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WistletLinkPreview/1.0 (+https://github.com/; like cohost's link embeds)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
    }

    /// <summary>Queues links for fetching (YouTube links and fresh previews are skipped).</summary>
    /// <remarks>Also called while posts are re-rendered at start-up, before site settings are loaded; the setting is checked when fetching.</remarks>
    public void Enqueue(IEnumerable<string> urls)
    {
        foreach (var url in urls)
            if (Embeds.YouTubeId(url) is null) queue.Writer.TryWrite(url);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var url in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await FetchAndStoreAsync(url, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Link preview for {Url} failed", url);
            }
        }
    }

    private async Task FetchAndStoreAsync(string url, CancellationToken ct)
    {
        if (!settings.Current.LinkPreviews) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.LinkPreviews.FirstOrDefaultAsync(p => p.Url == url, ct);
        var now = DateTime.UtcNow;
        if (row is { Status: LinkPreviewStatus.Ok } && now - row.FetchedAt < RefreshAfter) return;
        if (row is { Status: LinkPreviewStatus.Failed } && now - row.FetchedAt < RetryFailedAfter) return;

        if (row is null)
        {
            row = new LinkPreview { Url = url };
            db.LinkPreviews.Add(row);
        }

        var meta = await FetchAsync(url, ct);
        row.FetchedAt = now;
        row.Status = meta is null ? LinkPreviewStatus.Failed : LinkPreviewStatus.Ok;
        row.Title = meta?.Title;
        row.Description = meta?.Description;
        row.ImageUrl = meta?.ImageUrl;
        row.SiteName = meta?.SiteName;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another fetch of the same link saved first; that one wins.
        }
    }

    public record Metadata(string? Title, string? Description, string? ImageUrl, string? SiteName);

    /// <summary>Fetches a page and reads its preview metadata; null when it can't be fetched or isn't HTML.</summary>
    public async Task<Metadata?> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBytes];
            var total = 0;
            int read;
            while (total < MaxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0) total += read;
            var charset = response.Content.Headers.ContentType?.CharSet;
            Encoding encoding;
            try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            var html = encoding.GetString(buffer, 0, total);
            return Parse(html, response.RequestMessage?.RequestUri ?? uri);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogInformation("Couldn't fetch a preview for {Url}: {Error}", url, ex.Message);
            return null;
        }
    }

    /// <summary>Reads OpenGraph / Twitter card / plain HTML metadata. Image URLs must be https (the CSP only allows those).</summary>
    public static Metadata? Parse(string html, Uri baseUri)
    {
        var doc = new HtmlParser().ParseDocument(html);
        string? Meta(params string[] keys)
        {
            foreach (var key in keys)
            {
                var el = doc.QuerySelector($"meta[property='{key}'], meta[name='{key}']");
                var value = el?.GetAttribute("content")?.Trim();
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        var title = Meta("og:title", "twitter:title") ?? doc.Title?.Trim();
        var description = Meta("og:description", "twitter:description", "description");
        var site = Meta("og:site_name") ?? baseUri.Host;
        string? image = null;
        if (Meta("og:image", "og:image:url", "twitter:image") is { } img && Uri.TryCreate(baseUri, img, out var imgUri) && imgUri.Scheme == "https")
            image = imgUri.AbsoluteUri;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description)) return null;
        return new Metadata(Clip(title, 300), Clip(description, 1000), image is { Length: <= 2000 } ? image : null, Clip(site, 200));
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = WhitespaceRegex().Replace(s.Trim(), " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>False for loopback, private, link-local, carrier-grade NAT, multicast and other non-public ranges.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) ||
                     (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                     (b[0] == 198 && b[1] is 18 or 19) ||
                     (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
                     (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            if ((b[0] & 0xfe) == 0xfc) return false;                        // unique local fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // documentation
            if (b[0] == 0x20 && b[1] == 0x02) return false;                  // 6to4 can wrap private IPv4
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false; // NAT64
            return true;
        }
        return false;
    }
}

/// <summary>Helpers for showing embeds.</summary>
public static partial class Embeds
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YouTubeIdRegex();

    /// <summary>The video id of a YouTube link (watch, youtu.be, shorts, embed), or null.</summary>
    public static string? YouTubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
        var host = u.Host.ToLowerInvariant();
        string? id = null;
        if (host == "youtu.be")
            id = u.AbsolutePath.Trim('/');
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts[0] == "watch")
                id = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
            else if (parts.Length >= 2 && parts[0] is "shorts" or "embed" or "live")
                id = parts[1];
        }
        return id is not null && YouTubeIdRegex().IsMatch(id) ? id : null;
    }
}
