using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;
using Microsoft.EntityFrameworkCore;

namespace Wistlet.Pages;

public class TagModel(PostService posts, Data.AppDbContext db) : PageModel
{
    public string Tag { get; private set; } = "";

    /// <summary>The viewer's preferences for this tag (follow / silence / CW).</summary>
    public HashSet<TagPreferenceKind> Prefs { get; private set; } = [];
    public List<PostView> Posts { get; private set; } = [];
    public string? MoreUrl { get; private set; }

    public async Task OnGetAsync(string tag, int? before)
    {
        Tag = tag.Trim().TrimStart('#').ToLowerInvariant();
        var me = User.GetUserId();
        if (me is int id)
            Prefs = (await db.TagPreferences.Where(t => t.UserId == id && t.Tag == Tag).Select(t => t.Kind).ToListAsync()).ToHashSet();
        var query = (await posts.FeedAsync(me, User.IsStaff())).Where(p => p.Tags.Any(t => t.Tag == Tag));
        var (page, next) = await posts.PageAsync(query, me, before);
        Posts = page;
        MoreUrl = next is int n ? $"/tag/{Uri.EscapeDataString(Tag)}?before={n}" : null;
    }
}
