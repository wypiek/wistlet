using System.Net;
using Wistlet.Services;

namespace Wistlet.Tests;

public class EmbedTests
{
    private static IReadOnlyList<string> Links(string markdown) => PostRenderer.Render(markdown).EmbedUrls;

    [Fact]
    public void LinkOnItsOwnParagraphBecomesAnEmbed() =>
        Assert.Equal(["https://example.com/a"], Links("look at this\n\nhttps://example.com/a\n\nnice"));

    [Fact]
    public void LinkOnItsOwnLineInsideAParagraphBecomesAnEmbed() =>
        Assert.Equal(["https://example.com/a"], Links("look at this\nhttps://example.com/a\nnice"));

    [Theory]
    [InlineData("see https://example.com/a for more")]
    [InlineData("[a nice article](https://example.com/a)")]
    [InlineData("`https://example.com/a`")]
    [InlineData("    https://example.com/a")]
    public void InlineOrLabelledLinksStayLinks(string markdown) => Assert.Empty(Links(markdown));

    [Fact]
    public void EmbedsAreDistinctAndCapped()
    {
        var md = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"https://example.com/{i % 7}"));
        var links = Links(md);
        Assert.Equal(PostRenderer.MaxEmbeds, links.Count);
        Assert.Equal(links.Distinct().Count(), links.Count);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://youtu.be/jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://m.youtube.com/watch?v=jNQXAC9IVRw&t=5", "jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw", "jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/watch?v=bad\"id", null)]
    [InlineData("https://example.com/watch?v=jNQXAC9IVRw", null)]
    [InlineData("javascript:alert(1)", null)]
    public void RecognisesYouTubeLinks(string url, string? id) => Assert.Equal(id, Embeds.YouTubeId(url));

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2002:a00:1::1", false)]
    public void OnlyPublicAddressesAreFetched(string ip, bool isPublic) =>
        Assert.Equal(isPublic, LinkPreviewService.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void ReadsOpenGraphMetadata()
    {
        var html = """
            <html><head><title>fallback</title>
            <meta property="og:title" content="  An   article ">
            <meta property="og:description" content="What it's about.">
            <meta property="og:image" content="/img/card.png">
            <meta property="og:site_name" content="Example News">
            </head></html>
            """;
        var meta = LinkPreviewService.Parse(html, new Uri("https://example.com/news/1"))!;
        Assert.Equal("An article", meta.Title);
        Assert.Equal("What it's about.", meta.Description);
        Assert.Equal("https://example.com/img/card.png", meta.ImageUrl);
        Assert.Equal("Example News", meta.SiteName);
    }

    [Fact]
    public void FallsBackToTitleAndDropsInsecureImages()
    {
        var meta = LinkPreviewService.Parse("<title>Plain page</title><meta name=description content=hi><meta property=og:image content=http://x.example/a.png>",
            new Uri("http://plain.example/"))!;
        Assert.Equal("Plain page", meta.Title);
        Assert.Equal("hi", meta.Description);
        Assert.Null(meta.ImageUrl);
        Assert.Equal("plain.example", meta.SiteName);
    }

    [Fact]
    public void PagesWithoutMetadataHaveNoPreview() =>
        Assert.Null(LinkPreviewService.Parse("<html><body>hello</body></html>", new Uri("https://example.com/")));
}
