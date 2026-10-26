using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>An Atom feed of a page's latest public posts (cohost had one per page too).</summary>
public class FeedModel(AppDbContext db, PostService posts, SiteSettingsService settings) : PageModel
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public async Task<IActionResult> OnGetAsync(string userName)
    {
        var normalized = AccountService.Normalize(userName);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        // Private pages have no public feed.
        if (user is null || user.IsCurrentlyBanned || user.IsPrivate) return NotFound();

        var items = await posts.ProjectAsync(
            posts.Visible(null, false).Where(p => p.AuthorId == user.Id && p.ParentId == null).OrderByDescending(p => p.Id), null, 20);

        var origin = $"{Request.Scheme}://{Request.Host}";
        var profile = $"{origin}/u/{user.UserName}";
        var feed = new XElement(Atom + "feed",
            new XElement(Atom + "id", profile),
            new XElement(Atom + "title", $"{user.DisplayName} (@{user.UserName}) on {settings.Current.SiteName}"),
            new XElement(Atom + "subtitle", user.Dek),
            new XElement(Atom + "updated", (items.FirstOrDefault()?.CreatedAt ?? user.CreatedAt).ToString("o")),
            new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("type", "text/html"), new XAttribute("href", profile)),
            new XElement(Atom + "link", new XAttribute("rel", "self"), new XAttribute("type", "application/atom+xml"), new XAttribute("href", $"{profile}/feed.atom")),
            new XElement(Atom + "author", new XElement(Atom + "name", user.DisplayName), new XElement(Atom + "uri", profile)),
            items.Select(p =>
            {
                var url = $"{origin}/post/{p.Id}";
                var root = p.Root;
                var title = p.Headline ?? root.Headline ?? (p.IsShare ? $"shared a post by @{root.Author.UserName}" : $"post by @{user.UserName}");
                // Collapsed posts (CWs, 18+) stay collapsed in readers: only a note and the link.
                var html = p.Warnings.Count > 0 || p.IsAdult || root.IsAdult || root.Warnings.Count > 0
                    ? $"<p>This post has content warnings or 18+ content. <a href=\"{url}\">Read it on {settings.Current.SiteName}</a>.</p>"
                    : string.Join("", p.ShareTree.Append(p).Where(x => !x.IsTransparentShare).Select(x =>
                        string.Join("", x.Attachments.Select(a => $"<p><img src=\"{Absolute(origin, a.Url)}\" alt=\"{System.Net.WebUtility.HtmlEncode(a.AltText)}\" /></p>")) + x.Html));
                return new XElement(Atom + "entry",
                    new XElement(Atom + "id", url),
                    new XElement(Atom + "title", title),
                    new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("href", url)),
                    new XElement(Atom + "published", p.CreatedAt.ToString("o")),
                    new XElement(Atom + "updated", (p.EditedAt ?? p.CreatedAt).ToString("o")),
                    p.Tags.Select(t => new XElement(Atom + "category", new XAttribute("term", t))),
                    new XElement(Atom + "content", new XAttribute("type", "html"), html));
            }));

        return Content(new XDocument(new XDeclaration("1.0", "utf-8", null), feed).ToString(), "application/atom+xml; charset=utf-8");
    }

    private static string Absolute(string origin, string url) => url.StartsWith('/') ? origin + url : url;
}
