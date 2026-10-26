using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

/// <summary>Tag synonyms: make an alias count as another tag everywhere (tag pages, follows, filters, trending).</summary>
public class TagsModel(AppDbContext db, TagService tags, AuditService audit) : PageModel
{
    public record Group(string Canonical, List<string> Aliases, int Posts);

    public List<Group> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        foreach (var (canonical, members) in tags.Groups.OrderBy(g => g.Key))
        {
            var count = await db.PostTags.Where(t => members.Contains(t.Tag)).Select(t => t.PostId).Distinct().CountAsync();
            Groups.Add(new Group(canonical, members.Where(m => m != canonical).Order().ToList(), count));
        }
    }

    public async Task<IActionResult> OnPostAddAsync(string? alias, string? canonical)
    {
        var a = PostRenderer.ParseTags(alias).FirstOrDefault();
        var c = PostRenderer.ParseTags(canonical).FirstOrDefault();
        if (a is null || c is null) return Fail("Enter both tags.");
        // Always point at the end of a chain, so lookups take one step.
        c = tags.Canonical(c);
        if (a == c) return Fail("A tag can't be a synonym of itself.");
        if (await db.TagSynonyms.AnyAsync(s => s.Alias == a)) return Fail($"#{a} is already a synonym. Remove it first.");

        db.TagSynonyms.Add(new TagSynonym { Alias = a, Canonical = c });
        // Tags that pointed at the new alias now point at its canonical tag.
        await db.TagSynonyms.Where(s => s.Canonical == a).ExecuteUpdateAsync(x => x.SetProperty(s => s.Canonical, c));
        audit.Log("tag.synonym-add", $"#{a} → #{c}");
        await db.SaveChangesAsync();
        await tags.ReloadAsync();
        TempData["Flash"] = $"#{a} now counts as #{c}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string alias)
    {
        var row = await db.TagSynonyms.FindAsync(alias);
        if (row is null) return NotFound();
        db.TagSynonyms.Remove(row);
        audit.Log("tag.synonym-remove", $"#{row.Alias} → #{row.Canonical}");
        await db.SaveChangesAsync();
        await tags.ReloadAsync();
        TempData["Flash"] = $"#{row.Alias} is its own tag again.";
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage();
    }
}
