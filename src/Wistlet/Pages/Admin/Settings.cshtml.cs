using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Data;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

public class SettingsModel(SiteSettingsService settings, AuditService audit, AppDbContext db) : PageModel
{
    [BindProperty]
    public SiteConfig Config { get; set; } = new();

    public void OnGet() => Config = settings.Current.Clone();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        // Model binding turns empty inputs into null; normalise every text field.
        foreach (var p in typeof(SiteConfig).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(string)))
            p.SetValue(Config, ((string?)p.GetValue(Config) ?? "").Trim());
        Config.BannedWords = string.Join(", ", Config.GetBannedWords());

        var before = settings.Current;
        var changes = SiteConfigDiff(before, Config);
        await settings.SaveAsync(Config);
        if (changes.Count > 0)
        {
            audit.Log("settings.update", "site settings", string.Join("; ", changes));
            await db.SaveChangesAsync();
        }
        TempData["Flash"] = changes.Count == 0 ? "Nothing changed." : $"Settings saved ({changes.Count} change{(changes.Count == 1 ? "" : "s")}).";
        return RedirectToPage();
    }

    private static List<string> SiteConfigDiff(SiteConfig a, SiteConfig b)
    {
        var list = new List<string>();
        foreach (var p in typeof(SiteConfig).GetProperties().Where(p => p.CanWrite))
        {
            var va = Convert.ToString(p.GetValue(a), CultureInfo.InvariantCulture);
            var vb = Convert.ToString(p.GetValue(b), CultureInfo.InvariantCulture);
            if (va == vb) continue;
            list.Add(vb is { Length: > 40 } ? $"{p.Name} changed" : $"{p.Name}: {va} → {vb}");
        }
        return list;
    }
}
