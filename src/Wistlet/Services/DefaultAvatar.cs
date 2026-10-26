using System.Net;

namespace Wistlet.Services;

/// <summary>Generates a simple initial-letter SVG avatar from a user name.</summary>
public static class DefaultAvatar
{
    private static readonly string[] Palette = ["#1f6fb8", "#3a8a22", "#c27a0e", "#b83a2a", "#6a3fb8", "#1d8a80", "#b8367a", "#4f5f73"];

    public static string Render(string name)
    {
        var hash = 17;
        foreach (var c in name.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        var color = Palette[(hash & 0x7fffffff) % Palette.Length];
        var letter = WebUtility.HtmlEncode(name.Length > 0 ? name[..1].ToUpperInvariant() : "?");
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="96" height="96" viewBox="0 0 96 96">
              <rect width="96" height="96" fill="{color}"/>
              <text x="48" y="64" font-family="Segoe UI,Noto Sans,sans-serif" font-size="46" text-anchor="middle" fill="#fff">{letter}</text>
            </svg>
            """;
    }
}
