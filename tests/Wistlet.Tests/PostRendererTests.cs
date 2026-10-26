using Wistlet.Services;

namespace Wistlet.Tests;

public class PostRendererTests
{
    private static string R(string md) => PostRenderer.Render(md).Html;

    // ---- Markdown ----

    [Fact]
    public void RendersMarkdownWithGfmAndLineBreaks()
    {
        var html = R("**bold** and ~~gone~~\nnext line\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<del>gone</del>", html);
        Assert.Contains("<br>", html);
        Assert.Contains("<table>", html);
    }

    [Fact]
    public void SingleTildeIsNotStrikethrough() => Assert.DoesNotContain("<del>", R("~one~"));

    [Fact]
    public void MentionsBecomeProfileLinks()
    {
        var html = R("hi @annie! but not `@code` or me@example.com");
        Assert.Contains("<a href=\"/u/annie\" class=\"co-mention\">@annie</a>", html);
        Assert.DoesNotContain("/u/code", html);
        Assert.DoesNotContain("/u/example", html);
    }

    [Fact]
    public void ReadMoreMarkerSplitsPreview()
    {
        var r = PostRenderer.Render("first part\n<!--more-->\nsecret part");
        Assert.NotNull(r.PreviewHtml);
        Assert.Contains("first part", r.PreviewHtml);
        Assert.DoesNotContain("secret", r.PreviewHtml);
        Assert.Contains("secret part", r.Html);
        Assert.Null(PostRenderer.Render("no marker").PreviewHtml);
    }

    // ---- CSS crimes stay possible ----

    [Fact]
    public void KeepsInlineStylesAndInteractiveElements()
    {
        var html = R("<details open><summary style=\"color: red\">click</summary><div style=\"display: grid; grid-template-columns: 1fr 1fr; animation: spin 2s linear infinite; transform: rotate(3deg)\">x</div></details>");
        Assert.Contains("<details open=\"\">", html);
        Assert.Contains("<summary style=\"color: red\">", html);
        Assert.Contains("grid-template-columns: 1fr 1fr", html);
        Assert.Contains("animation: spin 2s linear infinite", html);
    }

    [Fact]
    public void KeepsDataImagesInCssButNotInImgSrc()
    {
        var html = R("<div style=\"background: url(data:image/png;base64,AAAA)\"></div><img src=\"data:image/png;base64,AAAA\" alt=\"x\">");
        Assert.Contains("url(data:image/png;base64,AAAA)", html);
        Assert.DoesNotContain("src=\"data:", html);
    }

    [Fact]
    public void AllowsRemoteImages() => Assert.Contains("src=\"https://example.com/a.png\"", R("<img src=\"https://example.com/a.png\">"));

    // ---- cohost's restrictions ----

    [Theory]
    [InlineData("<script>alert(1)</script>", "alert")]
    [InlineData("<style>body{display:none}</style>", "display:none")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "iframe")]
    [InlineData("<svg><script>alert(1)</script></svg>", "svg")]
    [InlineData("<object data=\"x\"></object>", "object")]
    [InlineData("<textarea>hi</textarea>", "textarea")]
    public void RemovesDangerousElementsWithContent(string input, string forbidden) =>
        Assert.DoesNotContain(forbidden, R(input), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void UnwrapsUnsupportedElementsButKeepsText()
    {
        var html = R("<form><button>press</button></form><section>inside</section>");
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<section", html);
        Assert.Contains("press", html);
        Assert.Contains("inside", html);
    }

    [Fact]
    public void RemovesEventHandlersAndClasses()
    {
        var html = R("<div onclick=\"alert(1)\" onmouseover=\"x()\" class=\"evil\" style=\"color: blue\">x</div>");
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("onmouseover", html);
        Assert.DoesNotContain("class=", html);
        Assert.Contains("color: blue", html);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>")]
    [InlineData("[x](javascript:alert(1))")]
    public void RemovesScriptLinks(string input) => Assert.DoesNotContain("href=", R(input));

    [Fact]
    public void ExternalLinksAreMarked()
    {
        var html = R("[site](https://example.com)");
        Assert.Contains("rel=\"nofollow noopener noreferrer ugc\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void PrefixesIdsAndRewritesInPostAnchors()
    {
        var html = R("<div id=\"top\">x</div><a href=\"#top\">up</a><details name=\"acc\"></details>");
        Assert.Contains("id=\"user-content-top\"", html);
        Assert.Contains("href=\"#user-content-top\"", html);
        Assert.Contains("name=\"user-content-acc\"", html);
    }

    [Fact]
    public void InputsBecomeDisabledCheckboxes()
    {
        var html = R("<input type=\"text\" value=\"x\" onfocus=\"alert(1)\">");
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("disabled", html);
        Assert.DoesNotContain("onfocus", html);
    }

    [Theory]
    [InlineData("--x: red", "")]
    [InlineData("color: red !important", "")]
    [InlineData("position: fixed; top: 0", "position: static; top: 0")]
    [InlineData("background: url(javascript:alert(1))", "")]
    [InlineData("width: expression(alert(1))", "")]
    [InlineData("behavior: url(x.htc)", "")]
    [InlineData("background: url('https://example.com/a.png'); color: rgb(var(--color-cherry))", "background: url('https://example.com/a.png'); color: rgb(var(--color-cherry))")]
    [InlineData("font-family: 'a;b', serif; color: red", "font-family: 'a;b', serif; color: red")]
    public void SanitizesInlineCss(string input, string expected) =>
        Assert.Equal(expected, PostRenderer.SanitizeStyle(input));

    [Theory]
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
    [InlineData("<a href=\" java\tscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"//evil.example/\">x</a>")]
    [InlineData("<div style=\"background:url(&quot;javascript:alert(1)&quot;)\">x</div>")]
    [InlineData("<math><mtext><script>alert(1)</script></mtext></math>")]
    public void BlocksObfuscatedVectors(string input)
    {
        var html = R(input);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public void AllowsRelativeUrlsInCss() =>
        Assert.Equal("background: url(/uploads/posts/a.png)", PostRenderer.SanitizeStyle("background: url(/uploads/posts/a.png)"));

    [Fact]
    public void AllowsRelativeLinksAndImages()
    {
        var html = R("[me](/u/boss) <img src=\"/uploads/posts/a.png\">");
        Assert.Contains("href=\"/u/boss\"", html);
        Assert.Contains("src=\"/uploads/posts/a.png\"", html);
    }

    [Fact]
    public void ParsesTagsLikeCohost()
    {
        Assert.Equal(["css crimes", "art", "the cohost global feed"], PostRenderer.ParseTags("#CSS Crimes, art , , the cohost global feed, art"));
    }

    private static readonly Dictionary<string, string> Emoji = new() { ["egg"] = "/img/emoji/egg.svg" };

    [Fact]
    public void ReplacesKnownEmojiShortcodes()
    {
        var html = PostRenderer.Render("hello :egg: and :nope:", Emoji).Html;
        Assert.Contains("<img class=\"co-emoji\" src=\"/img/emoji/egg.svg\" alt=\":egg:\" title=\":egg:\">", html);
        Assert.Contains(":nope:", html);
    }

    [Fact]
    public void LeavesEmojiInCodeAlone()
    {
        var html = PostRenderer.Render("`:egg:`\n\n    :egg:", Emoji).Html;
        Assert.DoesNotContain("co-emoji", html);
    }

    [Fact]
    public void EmojiClassCannotBeForged()
    {
        var html = PostRenderer.Render("<img class=\"co-emoji\" src=\"https://x.example/a.png\">", Emoji).Html;
        Assert.DoesNotContain("class", html);
    }

    [Theory]
    [InlineData("egg", true)]
    [InlineData("party-egg_2+", true)]
    [InlineData("Egg", false)]
    [InlineData("", false)]
    [InlineData("a:b", false)]
    [InlineData("waytoolongshortcodewaytoolongshortcode", false)]
    public void ValidatesEmojiShortcodes(string code, bool valid) =>
        Assert.Equal(valid, PostRenderer.IsValidEmojiShortcode(code));
}
