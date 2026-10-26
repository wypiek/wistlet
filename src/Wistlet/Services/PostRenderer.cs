using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace Wistlet.Services;

/// <summary>
/// Renders post bodies the way cohost did: Markdown (CommonMark + GFM, with single
/// newlines kept as line breaks) with raw HTML allowed, then sanitized.
///
/// The sanitizing rules follow cohost's behaviour as documented by prechoster
/// (https://github.com/cpsdqs/prechoster): inline <c>style</c> attributes are kept, so
/// "CSS crimes" work, but <c>&lt;style&gt;</c>, scripts and embeds are removed, ids are
/// prefixed with <c>user-content-</c>, <c>class</c> attributes are dropped, inputs become
/// disabled checkboxes, CSS custom-property declarations, <c>!important</c> and
/// <c>position: fixed</c> are removed, and images may only load over http(s).
/// The post body is additionally isolated with CSS containment when displayed.
/// </summary>
public static partial class PostRenderer
{
    /// <summary>Bump when the output changes so stored posts are re-rendered on start-up.</summary>
    public const int Version = 3;

    /// <summary>A line containing only this splits a post into a preview and the rest ("read more").</summary>
    public const string ReadMoreMarker = "<!--more-->";

    public const string IdPrefix = "user-content-";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UsePipeTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>Elements removed together with everything inside them.</summary>
    private static readonly HashSet<string> RemoveWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet",
        "svg", "math", "canvas", "textarea", "select", "option", "optgroup", "datalist", "title", "meta",
        "link", "base", "slot", "portal", "head", "param",
    };

    /// <summary>Elements that are allowed to stay. Anything else is unwrapped (its children are kept).</summary>
    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "acronym", "aside", "audio", "b", "bdo", "big", "blockquote", "br", "caption", "center",
        "cite", "code", "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
        "h1", "h2", "h3", "h4", "h5", "h6", "hgroup", "hr", "i", "img", "input", "ins", "kbd", "li", "mark",
        "marquee", "ol", "p", "picture", "pre", "q", "rp", "rt", "ruby", "s", "samp", "small", "source", "span",
        "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr",
        "track", "tt", "u", "ul", "var", "video", "wbr",
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "title", "lang", "dir", "hidden", "role", "id", "translate",
    };

    private static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ["href", "name"],
        ["img"] = ["src", "alt", "width", "height", "loading", "decoding"],
        ["audio"] = ["src", "controls", "loop", "muted", "preload"],
        ["video"] = ["src", "controls", "loop", "muted", "preload", "poster", "width", "height", "playsinline"],
        ["source"] = ["src", "type", "media"],
        ["track"] = ["src", "kind", "srclang", "label", "default"],
        ["details"] = ["open", "name"],
        ["input"] = ["type", "checked"],
        ["ol"] = ["start", "reversed", "type"],
        ["ul"] = ["type"],
        ["li"] = ["value"],
        ["td"] = ["colspan", "rowspan", "align", "valign", "width", "height"],
        ["th"] = ["colspan", "rowspan", "align", "valign", "width", "height", "scope"],
        ["table"] = ["width", "align", "border", "cellpadding", "cellspacing"],
        ["tr"] = ["align", "valign"],
        ["blockquote"] = ["cite"],
        ["q"] = ["cite"],
        ["del"] = ["cite", "datetime"],
        ["ins"] = ["cite", "datetime"],
        ["time"] = ["datetime"],
        ["font"] = ["color", "face", "size"],
        ["marquee"] = ["behavior", "direction", "scrollamount", "scrolldelay", "loop", "width", "height"],
        ["p"] = ["align"],
        ["div"] = ["align"],
        ["h1"] = ["align"], ["h2"] = ["align"], ["h3"] = ["align"], ["h4"] = ["align"], ["h5"] = ["align"], ["h6"] = ["align"],
        ["hr"] = ["width", "align"],
    };

    private static readonly string[] IdReferencingAttributes =
        ["aria-labelledby", "aria-describedby", "aria-controls", "aria-owns", "aria-flowto", "aria-activedescendant", "aria-details"];

    [GeneratedRegex(@"(?<![\w@/])@(?<user>[A-Za-z0-9_]{3,20})\b")]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@":(?<code>[a-z0-9_+-]{1,32}):")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"url\(\s*(['""]?)(?<url>[^'"")]*)\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"^[a-z][a-z-]*$")]
    private static partial Regex CssPropertyRegex();

    /// <param name="EmbedUrls">Links that sit alone on their own line; they get an embed (cohost turned these into iframely cards).</param>
    public record Result(string Html, string? PreviewHtml, IReadOnlyList<string> EmbedUrls);

    /// <summary>Most embeds one post can show.</summary>
    public const int MaxEmbeds = 5;

    /// <summary>Renders a post body. When it contains a read-more marker, <see cref="Result.PreviewHtml"/> holds the part before it.</summary>
    /// <param name="emoji">The site's custom emoji (shortcode → image URL); <c>:shortcode:</c> in text becomes the image.</param>
    public static Result Render(string? markdown, IReadOnlyDictionary<string, string>? emoji = null)
    {
        markdown = (markdown ?? "").Replace("\r\n", "\n");
        var full = RenderFragment(markdown.Replace(ReadMoreMarker, ""), emoji);

        var cut = FindReadMore(markdown);
        string? preview = cut is int i ? RenderFragment(markdown[..i], emoji) : null;
        return new Result(full, preview, FindEmbedLinks(full));
    }

    private static int? FindReadMore(string markdown)
    {
        var lines = markdown.Split('\n');
        var offset = 0;
        foreach (var line in lines)
        {
            if (line.Trim() == ReadMoreMarker) return offset;
            offset += line.Length + 1;
        }
        return null;
    }

    /// <summary>Markdown → sanitized HTML for one body.</summary>
    public static string RenderFragment(string markdown, IReadOnlyDictionary<string, string>? emoji = null)
    {
        var html = Markdown.ToHtml(markdown, Pipeline);
        return Sanitize(html, emoji);
    }

    public static string Sanitize(string html, IReadOnlyDictionary<string, string>? emoji = null)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument("<!doctype html><html><body></body></html>");
        var root = doc.Body!;
        root.InnerHtml = html;

        // Collect ids first so in-post anchors (#foo, footnotes) can be pointed at the prefixed ids.
        var ids = new HashSet<string>(root.QuerySelectorAll("[id]").Select(e => e.Id!).Where(i => !string.IsNullOrEmpty(i)));

        CleanChildren(root, ids);
        LinkMentions(root);
        if (emoji is { Count: > 0 }) ReplaceEmoji(root, emoji);
        return root.InnerHtml;
    }

    private static void CleanChildren(INode parent, HashSet<string> ids)
    {
        foreach (var node in parent.ChildNodes.ToArray())
        {
            switch (node)
            {
                case IComment:
                case IProcessingInstruction:
                case IDocumentType:
                    node.RemoveFromParent();
                    break;
                case IElement el:
                    CleanElement(el, ids);
                    break;
            }
        }
    }

    private static void CleanElement(IElement el, HashSet<string> ids)
    {
        var tag = el.LocalName;
        if (RemoveWithContent.Contains(tag))
        {
            el.Remove();
            return;
        }

        // Clean the subtree first; then this element is either kept or unwrapped.
        CleanChildren(el, ids);

        if (!AllowedElements.Contains(tag))
        {
            var parent = el.Parent!;
            foreach (var child in el.ChildNodes.ToArray()) parent.InsertBefore(child, el);
            el.Remove();
            return;
        }

        ElementAttributes.TryGetValue(tag, out var allowedHere);
        foreach (var attr in el.Attributes.ToArray())
        {
            var name = attr.Name.ToLowerInvariant();
            var keep = GlobalAttributes.Contains(name)
                       || name.StartsWith("aria-", StringComparison.Ordinal)
                       || (allowedHere?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false);
            if (!keep) el.RemoveAttribute(attr.Name);
        }

        // ids (and details/anchor names) are namespaced so they can't clash with the page.
        if (el.Id is { Length: > 0 } id) el.Id = IdPrefix + id;
        if (el.GetAttribute("name") is { Length: > 0 } nameAttr) el.SetAttribute("name", IdPrefix + nameAttr);
        foreach (var refAttr in IdReferencingAttributes)
        {
            if (el.GetAttribute(refAttr) is { Length: > 0 } value)
                el.SetAttribute(refAttr, string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => IdPrefix + v)));
        }

        if (el.GetAttribute("style") is { } style)
        {
            var cleaned = SanitizeStyle(style);
            if (cleaned.Length == 0) el.RemoveAttribute("style");
            else el.SetAttribute("style", cleaned);
        }

        switch (tag)
        {
            case "a":
                var href = el.GetAttribute("href");
                if (href is null) break;
                href = href.Trim();
                if (href.StartsWith('#'))
                {
                    var target = Uri.UnescapeDataString(href[1..]);
                    el.SetAttribute("href", ids.Contains(target) ? "#" + IdPrefix + target : href);
                }
                else if (!IsSafeLink(href))
                {
                    el.RemoveAttribute("href");
                }
                else
                {
                    el.SetAttribute("rel", "nofollow noopener noreferrer ugc");
                    if (IsExternal(href)) el.SetAttribute("target", "_blank");
                }
                break;

            case "img" or "audio" or "video" or "source" or "track":
                foreach (var srcAttr in new[] { "src", "poster" })
                {
                    if (el.GetAttribute(srcAttr) is { } src && !IsSafeMediaUrl(src)) el.RemoveAttribute(srcAttr);
                }
                if (tag == "img" && !el.HasAttribute("loading")) el.SetAttribute("loading", "lazy");
                break;

            case "input":
                // cohost turned every input into a disabled checkbox.
                el.SetAttribute("type", "checkbox");
                el.SetAttribute("disabled", "");
                break;
        }
    }

    private static bool IsSafeLink(string url)
    {
        if (url.StartsWith('/') && !url.StartsWith("//")) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return !url.Contains(':');
        return u.Scheme is "http" or "https" or "mailto";
    }

    private static bool IsExternal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    private static bool IsSafeMediaUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith('/') && !url.StartsWith("//")) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
    }

    /// <summary>
    /// Cleans an inline style attribute: custom-property declarations, <c>!important</c>
    /// declarations, legacy script hooks and unsafe <c>url()</c>s are dropped and
    /// <c>position: fixed</c> becomes <c>static</c>. Everything else is kept as written.
    /// </summary>
    public static string SanitizeStyle(string style)
    {
        var output = new StringBuilder();
        foreach (var decl in SplitDeclarations(style))
        {
            var colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            var prop = decl[..colon].Trim().ToLowerInvariant();
            var value = decl[(colon + 1)..].Trim();
            if (value.Length == 0) continue;
            if (!CssPropertyRegex().IsMatch(prop)) continue; // also rejects "--custom-properties"
            if (value.Contains("!important", StringComparison.OrdinalIgnoreCase)) continue;

            var lower = value.ToLowerInvariant();
            if (lower.Contains("expression(") || lower.Contains("javascript:") || lower.Contains("vbscript:")) continue;
            if (prop is "behavior" or "-moz-binding") continue;
            if (prop == "position" && lower == "fixed") value = "static";

            var badUrl = false;
            foreach (Match m in CssUrlRegex().Matches(value))
            {
                var u = m.Groups["url"].Value.Trim();
                var ok = u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                         || u.StartsWith("data:font/", StringComparison.OrdinalIgnoreCase)
                         || IsSafeMediaUrl(u)
                         || (!u.Contains(':') && !u.StartsWith("//"));
                if (!ok) { badUrl = true; break; }
            }
            if (badUrl) continue;

            if (output.Length > 0) output.Append("; ");
            output.Append(prop).Append(": ").Append(value);
        }
        return output.ToString();
    }

    /// <summary>Splits on semicolons that aren't inside parentheses or quotes.</summary>
    private static IEnumerable<string> SplitDeclarations(string style)
    {
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < style.Length; i++)
        {
            var c = style[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ';' && depth == 0)
            {
                yield return style[start..i];
                start = i + 1;
            }
        }
        if (start < style.Length) yield return style[start..];
    }

    /// <summary>Turns @handles in text into profile links (outside links and code).</summary>
    private static void LinkMentions(IElement root)
    {
        var walker = root.Owner!.CreateTreeWalker(root, FilterSettings.Text);
        var texts = new List<IText>();
        while (walker.ToNext() is IText t) texts.Add(t);

        foreach (var text in texts)
        {
            if (text.Closest("a, code, pre") is not null) continue;
            var value = text.Data;
            var matches = MentionRegex().Matches(value);
            if (matches.Count == 0) continue;

            var parent = text.Parent!;
            var last = 0;
            foreach (Match m in matches)
            {
                if (m.Index > last) parent.InsertBefore(root.Owner!.CreateTextNode(value[last..m.Index]), text);
                var a = root.Owner!.CreateElement("a");
                a.SetAttribute("href", "/u/" + m.Groups["user"].Value);
                a.SetAttribute("class", "co-mention");
                a.TextContent = m.Value;
                parent.InsertBefore(a, text);
                last = m.Index + m.Length;
            }
            if (last < value.Length) parent.InsertBefore(root.Owner!.CreateTextNode(value[last..]), text);
            text.RemoveFromParent();
        }
    }

    /// <summary>
    /// Finds links that are alone on their line (the whole paragraph, or between line breaks) and show
    /// their own address, like a URL pasted on its own line. These become embeds below the post.
    /// </summary>
    public static IReadOnlyList<string> FindEmbedLinks(string html)
    {
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body></body></html>");
        doc.Body!.InnerHtml = html;
        var found = new List<string>();
        foreach (var a in doc.Body.QuerySelectorAll("p > a[href]"))
        {
            var href = a.GetAttribute("href")!;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) continue;
            var text = a.TextContent.Trim();
            if (text != href && text + "/" != href && text != href.TrimEnd('/')) continue;
            if (!AloneOnLine(a)) continue;
            if (!found.Contains(href)) found.Add(href);
            if (found.Count == MaxEmbeds) break;
        }
        return found;

        static bool AloneOnLine(IElement a)
        {
            static bool Blank(INode? n) => n is IText t && string.IsNullOrWhiteSpace(t.Data);
            var prev = a.PreviousSibling;
            while (Blank(prev)) prev = prev!.PreviousSibling;
            var next = a.NextSibling;
            while (Blank(next)) next = next!.NextSibling;
            return (prev is null || prev is IElement { LocalName: "br" }) && (next is null || next is IElement { LocalName: "br" });
        }
    }

    /// <summary>Turns <c>:shortcode:</c> in text (outside code) into the site's emoji images.</summary>
    private static void ReplaceEmoji(IElement root, IReadOnlyDictionary<string, string> emoji)
    {
        var walker = root.Owner!.CreateTreeWalker(root, FilterSettings.Text);
        var texts = new List<IText>();
        while (walker.ToNext() is IText t) texts.Add(t);

        foreach (var text in texts)
        {
            if (text.Closest("code, pre") is not null) continue;
            var value = text.Data;
            var matches = EmojiRegex().Matches(value).Where(m => emoji.ContainsKey(m.Groups["code"].Value)).ToList();
            if (matches.Count == 0) continue;

            var parent = text.Parent!;
            var last = 0;
            foreach (var m in matches)
            {
                if (m.Index < last) continue;
                if (m.Index > last) parent.InsertBefore(root.Owner!.CreateTextNode(value[last..m.Index]), text);
                var img = root.Owner!.CreateElement("img");
                img.SetAttribute("class", "co-emoji");
                img.SetAttribute("src", emoji[m.Groups["code"].Value]);
                img.SetAttribute("alt", m.Value);
                img.SetAttribute("title", m.Value);
                parent.InsertBefore(img, text);
                last = m.Index + m.Length;
            }
            if (last < value.Length) parent.InsertBefore(root.Owner!.CreateTextNode(value[last..]), text);
            text.RemoveFromParent();
        }
    }

    /// <summary>Shortcodes used in a post, to find posts to re-render when an emoji changes.</summary>
    public static bool IsValidEmojiShortcode(string code) => EmojiRegex().IsMatch($":{code}:") && EmojiRegex().Match($":{code}:").Length == code.Length + 2;

    private static IElement? Closest(this INode node, string selector)
    {
        for (var p = node.ParentElement; p is not null; p = p.ParentElement)
            if (p.Matches(selector)) return p;
        return null;
    }

    /// <summary>Usernames mentioned in a post (for notifications).</summary>
    public static IReadOnlyCollection<string> ExtractMentions(string? markdown) =>
        MentionRegex().Matches(markdown ?? "").Select(m => m.Groups["user"].Value.ToLowerInvariant()).Distinct().Take(20).ToList();

    /// <summary>Parses cohost-style tags: comma separated, may contain spaces, an optional leading # is dropped.</summary>
    public static IReadOnlyList<string> ParseTags(string? tags) =>
        (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Trim())
            .Where(t => t.Length is > 0 and <= 64)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .Take(30)
            .ToList();

    /// <summary>Parses a comma-separated list of content warnings.</summary>
    public static IReadOnlyList<string> ParseWarnings(string? cws) =>
        (cws ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
}
