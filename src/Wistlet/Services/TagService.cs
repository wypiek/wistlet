using Microsoft.EntityFrameworkCore;
using Wistlet.Data;

namespace Wistlet.Services;

/// <summary>
/// Tag synonyms, kept in memory: an alias (#tf) and its canonical tag (#transformers) are treated as the
/// same tag on tag pages, in followed / silenced / CW'd tags and in trending tags. Posts keep the tags
/// exactly as their authors typed them.
/// </summary>
public class TagService(IServiceScopeFactory scopes)
{
    private IReadOnlyDictionary<string, string> canonical = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string[]> groups = new Dictionary<string, string[]>();

    public async Task ReloadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.TagSynonyms.AsNoTracking().ToListAsync();
        canonical = rows.ToDictionary(r => r.Alias, r => r.Canonical);
        groups = rows.GroupBy(r => r.Canonical).ToDictionary(g => g.Key, g => g.Select(r => r.Alias).Prepend(g.Key).ToArray());
    }

    /// <summary>The tag an alias stands for (or the tag itself).</summary>
    public string Canonical(string tag) => canonical.TryGetValue(tag, out var c) ? c : tag;

    /// <summary>The canonical tag and all of its aliases.</summary>
    public IReadOnlyList<string> Group(string tag)
    {
        var c = Canonical(tag);
        return groups.TryGetValue(c, out var g) ? g : [c];
    }

    /// <summary>Every tag that counts as one of <paramref name="tags"/>.</summary>
    public List<string> Expand(IEnumerable<string> tags) => tags.SelectMany(Group).Distinct().ToList();

    public IReadOnlyDictionary<string, string[]> Groups => groups;
}
