using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Wistlet.Services;

/// <summary>
/// Turns plain post text into safe HTML: everything is encoded, then links,
/// #hashtags and @mentions are turned into anchors and newlines into line breaks.
/// </summary>
public static partial class ContentFormatter
{
    [GeneratedRegex(@"(?<url>https?://[^\s<>""]+[^\s<>"".,;:!?)\]'])|(?<![\w@#&])#(?<tag>[\p{L}\p{N}_]{1,64})|(?<![\w@])@(?<user>[A-Za-z0-9_]{1,32})")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?<![\w@#&])#(?<tag>[\p{L}\p{N}_]{1,64})")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?<![\w@])@(?<user>[A-Za-z0-9_]{1,32})")]
    private static partial Regex MentionRegex();

    public static IHtmlContent Format(string? text)
    {
        if (string.IsNullOrEmpty(text)) return HtmlString.Empty;

        var sb = new StringBuilder(text.Length + 64);
        var last = 0;
        foreach (Match m in TokenRegex().Matches(text))
        {
            sb.Append(Encode(text[last..m.Index]));
            if (m.Groups["url"].Success)
            {
                var url = m.Groups["url"].Value;
                var display = url.Length > 48 ? url[..45] + "…" : url;
                sb.Append($"<a href=\"{WebUtility.HtmlEncode(url)}\" rel=\"nofollow noopener noreferrer ugc\" target=\"_blank\">{WebUtility.HtmlEncode(display)}</a>");
            }
            else if (m.Groups["tag"].Success)
            {
                var tag = m.Groups["tag"].Value;
                sb.Append($"<a class=\"hashtag\" href=\"/tag/{Uri.EscapeDataString(tag.ToLowerInvariant())}\">#{WebUtility.HtmlEncode(tag)}</a>");
            }
            else
            {
                var user = m.Groups["user"].Value;
                sb.Append($"<a class=\"mention\" href=\"/u/{Uri.EscapeDataString(user)}\">@{WebUtility.HtmlEncode(user)}</a>");
            }
            last = m.Index + m.Length;
        }
        sb.Append(Encode(text[last..]));
        return new HtmlString(sb.ToString());
    }

    private static string Encode(string s) =>
        WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br>");

    public static IReadOnlyCollection<string> ExtractTags(string text) =>
        TagRegex().Matches(text).Select(m => m.Groups["tag"].Value.ToLowerInvariant()).Distinct().Take(20).ToList();

    public static IReadOnlyCollection<string> ExtractMentions(string text) =>
        MentionRegex().Matches(text).Select(m => m.Groups["user"].Value.ToLowerInvariant()).Distinct().Take(20).ToList();
}
