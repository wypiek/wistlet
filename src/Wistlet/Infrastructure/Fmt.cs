using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Html;

namespace Wistlet.Infrastructure;

public static class Fmt
{
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        return utc.Year == DateTime.UtcNow.Year
            ? utc.ToString("MMM d", CultureInfo.InvariantCulture)
            : utc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>Relative time in words: "just now", "5 minutes ago", "3 days ago", then a date.</summary>
    public static string AgoLong(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return Unit((int)span.TotalMinutes, "minute");
        if (span.TotalHours < 24) return Unit((int)span.TotalHours, "hour");
        if (span.TotalDays < 7) return Unit((int)span.TotalDays, "day");
        return utc.Year == DateTime.UtcNow.Year
            ? utc.ToString("MMM d", CultureInfo.InvariantCulture)
            : utc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>A &lt;time&gt; element showing relative time, with the absolute time as a tooltip.</summary>
    public static IHtmlContent Time(DateTime utc)
    {
        var iso = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        var full = utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return new HtmlString($"<time datetime=\"{iso}\" title=\"{full}\">{WebUtility.HtmlEncode(AgoLong(utc))}</time>");
    }

    public static string Date(DateTime utc) => utc.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Plural(int n, string singular, string? plural = null) =>
        $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? singular : plural ?? singular + "s")}";

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static string StableHash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];

    /// <summary>Only allow local return URLs, falling back to the given default.</summary>
    public static string SafeReturnUrl(string? url, string fallback = "/") =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : fallback;
}
