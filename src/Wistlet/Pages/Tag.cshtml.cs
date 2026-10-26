using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

public class TagModel(PostService posts, Data.AppDbContext db, TagService tagService) : PageModel
{
    public string Tag { get; private set; } = "";

    /// <summary>Other tags counted as this one (synonyms).</summary>
    public IReadOnlyList<string> Synonyms { get; private set; } = [];

    /// <summary>The viewer's preferences for this tag (follow / silence / CW).</summary>
    public HashSet<TagPreferenceKind> Prefs { get; private set; } = [];
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(string tag, int? before)
    {
        var typed = tag.Trim().TrimStart('#').ToLowerInvariant();
        // An alias's page is its canonical tag's page.
        Tag = tagService.Canonical(typed);
        if (Tag != typed) return RedirectPermanent($"/tag/{Uri.EscapeDataString(Tag)}{Request.QueryString}");

        var group = tagService.Group(Tag);
        Synonyms = group.Where(t => t != Tag).ToList();
        var me = User.GetUserId();
        if (me is int id)
            Prefs = (await db.TagPreferences.Where(t => t.UserId == id && group.Contains(t.Tag)).Select(t => t.Kind).ToListAsync()).ToHashSet();
        var query = (await posts.FeedAsync(me, User.IsStaff())).Where(p => p.Tags.Any(t => group.Contains(t.Tag)));
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/tag/{Uri.EscapeDataString(Tag)}?before={n}" : null;
        return Page();
    }
}
