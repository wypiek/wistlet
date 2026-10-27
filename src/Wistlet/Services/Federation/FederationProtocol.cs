using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wistlet.Services.Federation;

/// <summary>
/// The Wistlet federation protocol, version 1: how Wistlet instances find each other, sign
/// their requests and describe accounts, posts and activities. It only speaks to other Wistlet
/// instances: an instance is recognised by the document at <see cref="DiscoveryPath"/>, whose
/// "software" field must be "wistlet". See docs/federation.md.
/// </summary>
public static partial class FederationProtocol
{
    public const int Version = 1;
    public const string Software = "wistlet";
    public const string DiscoveryPath = "/.well-known/wistlet";
    public const string InboxPath = "/federation/inbox";

    public const string InstanceHeader = "Wistlet-Instance";
    public const string DateHeader = "Wistlet-Date";
    public const string DigestHeader = "Wistlet-Digest";
    public const string SignatureHeader = "Wistlet-Signature";

    /// <summary>How far a request's date may be from the receiver's clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Largest request or response body either side accepts.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex LocalNameRegex();

    // Lower-case host names (labels of letters, digits and inner hyphens), optionally with a port.
    [GeneratedRegex(@"^(?=.{1,253}(:|$))[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*(?::\d{1,5})?$")]
    private static partial Regex DomainRegex();

    public static bool IsValidName(string? name) => name is not null && LocalNameRegex().IsMatch(name);

    public static bool IsValidDomain(string? domain) => domain is not null && DomainRegex().IsMatch(domain);

    /// <summary>Splits "@annie@other.example" (or without the leading @) into name and domain.</summary>
    public static bool TryParseHandle(string? handle, out string name, out string domain)
    {
        name = domain = "";
        var h = (handle ?? "").Trim().TrimStart('@');
        var at = h.IndexOf('@');
        if (at <= 0 || at == h.Length - 1) return false;
        var n = h[..at];
        var d = h[(at + 1)..].ToLowerInvariant();
        if (!IsValidName(n) || !IsValidDomain(d)) return false;
        name = n;
        domain = d;
        return true;
    }

    /// <summary>The text a request signature covers. Naming both instances stops a signed request being replayed elsewhere.</summary>
    public static string SigningString(string method, string path, string targetDomain, string senderDomain, string date, string digest) =>
        $"wistlet-v{Version}\n{method.ToUpperInvariant()}\n{path}\n{targetDomain}\n{senderDomain}\n{date}\n{digest}";

    public static string Digest(ReadOnlySpan<byte> body) => Convert.ToBase64String(SHA256.HashData(body));

    /// <summary>Headers for a signed request from <paramref name="senderDomain"/> to <paramref name="targetDomain"/>.</summary>
    public static Dictionary<string, string> Sign(ECDsa key, string method, string path, string targetDomain, string senderDomain,
        byte[] body, DateTimeOffset now)
    {
        var date = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var digest = Digest(body);
        var text = SigningString(method, path, targetDomain, senderDomain, date, digest);
        var signature = key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256);
        return new Dictionary<string, string>
        {
            [InstanceHeader] = senderDomain,
            [DateHeader] = date,
            [DigestHeader] = digest,
            [SignatureHeader] = Convert.ToBase64String(signature),
        };
    }

    /// <summary>
    /// Checks a request's date and digest. Returns an error message, or null when both are fine;
    /// the signature itself is checked with <see cref="VerifySignature"/> once the key is known.
    /// </summary>
    public static string? CheckDateAndDigest(string? date, string? digest, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!long.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            return "missing or malformed date";
        var when = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, 0, 253402300799));
        if ((now - when).Duration() > MaxClockSkew) return "date too far from the current time";
        if (digest is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(Digest(body))))
            return "digest doesn't match the body";
        return null;
    }

    public static bool VerifySignature(string publicKeySpki, string signingString, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(signingString), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// True if <paramref name="url"/> is an absolute http(s) URL on <paramref name="domain"/>
    /// (host and port), optionally under <paramref name="pathPrefix"/>.
    /// </summary>
    public static bool IsUrlOn(string? url, string domain, string? pathPrefix = null) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) &&
        string.Equals(u.Authority, domain, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(u.UserInfo) &&
        (pathPrefix is null || u.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal)) &&
        url!.Length <= 300;

    /// <summary>The local post id in a post URL ("https://a.example/post/12" → 12).</summary>
    public static int? PostIdFromUrl(string? url, string domain)
    {
        if (!IsUrlOn(url, domain, "/post/")) return null;
        var tail = new Uri(url!).AbsolutePath["/post/".Length..];
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// Makes root-relative links and image sources in rendered post HTML point at the post's
    /// own instance, so "@annie" or "/post/3" in a remote post don't lead to local pages.
    /// </summary>
    public static string Absolutize(string html, string origin) =>
        RelativeUrlRegex().Replace(html, m => $"{m.Groups[1].Value}=\"{origin}/");

    [GeneratedRegex("\\b(href|src)=\"/(?![/\\\\])")]
    private static partial Regex RelativeUrlRegex();
}

/// <summary>The discovery document at /.well-known/wistlet.</summary>
public record InstanceDocument(
    [property: JsonPropertyName("software")] string Software,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("inbox")] string Inbox);

/// <summary>A public profile, as served at /federation/users/{name}.</summary>
public record ActorDocument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("dek")] string? Dek,
    [property: JsonPropertyName("pronouns")] string? Pronouns,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("avatar")] string? Avatar,
    [property: JsonPropertyName("avatarShape")] string? AvatarShape,
    [property: JsonPropertyName("banner")] string? Banner,
    [property: JsonPropertyName("adultPage")] bool AdultPage,
    [property: JsonPropertyName("url")] string Url);

public record AudioDocument(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist);

/// <summary>
/// A post or comment. Posts are sent as Markdown and rendered by the receiving instance with
/// its own sanitizer, never as HTML.
/// </summary>
public record PostDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("editedAt")] DateTime? EditedAt,
    [property: JsonPropertyName("headline")] string? Headline,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("contentWarnings")] string? ContentWarnings,
    [property: JsonPropertyName("adult")] bool Adult,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("audio")] AudioDocument? Audio,
    // For comments: the post (on the receiving instance) they're on, and the post or comment they answer.
    [property: JsonPropertyName("post")] string? Post = null,
    [property: JsonPropertyName("parent")] string? Parent = null);

/// <summary>Activity types sent to an inbox.</summary>
public static class ActivityTypes
{
    public const string Follow = "follow";
    public const string Unfollow = "unfollow";
    public const string Post = "post";
    public const string Delete = "delete";
    public const string Like = "like";
    public const string Unlike = "unlike";
    public const string Comment = "comment";
    public const string Profile = "profile";
    public const string DeleteAccount = "delete-account";
}

/// <summary>
/// One thing that happened, sent to another instance's inbox. <see cref="Actor"/> is a user name
/// on the sending instance; an instance can only speak for its own accounts.
/// </summary>
public record Activity(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("actor")] string Actor,
    // follow/unfollow: the user name being (un)followed on the receiving instance.
    [property: JsonPropertyName("target")] string? Target = null,
    // like/unlike/delete: a post URL.
    [property: JsonPropertyName("object")] string? Object = null,
    [property: JsonPropertyName("post")] PostDocument? Post = null,
    [property: JsonPropertyName("profile")] ActorDocument? Profile = null);
