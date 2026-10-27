using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wistlet.Services.Federation;

/// <summary>
/// This instance's federation identity (its domain and signing key) and the HTTP client used to
/// talk to other instances.
/// </summary>
public sealed class FederationContext : IDisposable
{
    private readonly SiteSettingsService _settings;
    private readonly ECDsa _key;
    private readonly HttpClient _http;

    public FederationContext(IConfiguration config, ServerConfig server, SiteSettingsService settings, ILogger<FederationContext> logger)
    {
        _settings = settings;
        AllowHttp = server.AllowHttp;
        AllowPrivateAddresses = config.GetValue("Wistlet:Federation:AllowPrivateAddresses", false);

        // Checked and explained by ServerConfig (WISTLET_PUBLIC_URL).
        Origin = server.PublicOrigin;
        Domain = server.PublicDomain;

        var keyPath = Path.Combine(config["Wistlet:DataPath"] ?? "data", "federation-key.pem");
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath))
        {
            _key.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            File.WriteAllText(keyPath, _key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogInformation("Created the federation signing key at {Path}", keyPath);
        }
        PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        _http = new HttpClient(CreateHandler(AllowPrivateAddresses))
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = FederationProtocol.MaxBodyBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Wistlet/{FederationProtocol.Version}");
    }

    /// <summary>Plain-HTTP instances are allowed (for testing on one machine; never in production).</summary>
    public bool AllowHttp { get; }

    /// <summary>Instances on loopback and private network addresses are allowed (for testing).</summary>
    public bool AllowPrivateAddresses { get; }

    /// <summary>"https://wistlet.example", from Wistlet:PublicUrl; null when that isn't set.</summary>
    public string? Origin { get; }

    /// <summary>"wistlet.example" (with the port, if it isn't the default one).</summary>
    public string? Domain { get; }

    public string PublicKey { get; }

    /// <summary>Short fingerprint of the public key, for admins comparing notes.</summary>
    public string KeyFingerprint => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(PublicKey)))[..16];

    public bool IsConfigured => Domain is not null;

    public bool IsEnabled => IsConfigured && _settings.Current.FederationEnabled;

    /// <summary>The base address of another instance.</summary>
    public string OriginOf(string domain) => $"{(AllowHttp ? "http" : "https")}://{domain}";

    public Dictionary<string, string> Sign(string method, string path, string targetDomain, byte[] body) =>
        FederationProtocol.Sign(_key, method, path, targetDomain, Domain!, body, DateTimeOffset.UtcNow);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>GETs a JSON document from another instance; null if it isn't there or isn't valid.</summary>
    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default) where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        try { return JsonSerializer.Deserialize<T>(bytes, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Delivers an activity to another instance's inbox, signed with this instance's key.</summary>
    public async Task<(HttpStatusCode Status, string Body)> PostToInboxAsync(string domain, byte[] body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OriginOf(domain) + FederationProtocol.InboxPath)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in Sign("POST", FederationProtocol.InboxPath, domain, body))
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, text.Length > 300 ? text[..300] : text);
    }

    /// <summary>
    /// Connections only go to public addresses (unless testing allows otherwise), checked on the
    /// address actually connected to, so a host name can't be pointed at the internal network.
    /// Redirects aren't followed.
    /// </summary>
    private static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            Exception? last = null;
            foreach (var address in addresses)
            {
                if (!allowPrivate && IsNonPublic(address)) continue;
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e)
                {
                    socket.Dispose();
                    last = e;
                }
            }
            throw new HttpRequestException($"{ctx.DnsEndPoint.Host} has no reachable public address.", last);
        },
    };

    public static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 0 or 10 or 127 or >= 224 ||
                   (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                   (b[0] == 198 && b[1] is 18 or 19);
        }
        var v6 = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
               (v6[0] & 0xFE) == 0xFC; // fc00::/7, unique local addresses
    }

    public void Dispose()
    {
        _http.Dispose();
        _key.Dispose();
    }
}
