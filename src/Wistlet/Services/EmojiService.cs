using Microsoft.EntityFrameworkCore;
using Wistlet.Data;

namespace Wistlet.Services;

/// <summary>Keeps the site's custom emoji (shortcode → image URL) in memory for the post renderer.</summary>
public class EmojiService(IServiceScopeFactory scopes)
{
    private IReadOnlyDictionary<string, string> current = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Current => current;

    public async Task ReloadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        current = await db.CustomEmoji.AsNoTracking().ToDictionaryAsync(e => e.Shortcode, e => e.Path);
    }
}
