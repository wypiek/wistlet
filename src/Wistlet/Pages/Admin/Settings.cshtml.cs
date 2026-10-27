using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Data;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

public class SettingsModel(SiteSettingsService settings, AuditService audit, AppDbContext db, UploadService uploads) : PageModel
{
    [BindProperty]
    public SiteConfig Config { get; set; } = new();

    public void OnGet() => Config = settings.Current.Clone();

    public async Task<IActionResult> OnPostAsync(IFormFile? siteIcon, bool resetSiteIcon, IFormFile? favicon, bool resetFavicon)
    {
        // The icons aren't text fields: they start as they are and change through uploads below.
        var before = settings.Current;
        Config.SiteIconPath = before.SiteIconPath;
        Config.FaviconPath = before.FaviconPath;
        if (!ModelState.IsValid) return Page();

        // Model binding turns empty inputs into null; normalise every text field.
        foreach (var p in typeof(SiteConfig).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(string)))
            p.SetValue(Config, ((string?)p.GetValue(Config) ?? "").Trim());
        Config.BannedWords = string.Join(", ", Config.GetBannedWords());

        // Store both new images before touching the old ones, so a failed upload changes nothing.
        var (iconUrl, iconError) = await uploads.SaveImageAsync(siteIcon, "site", maxMbOverride: 2);
        if (iconError is not null)
        {
            ModelState.AddModelError("", $"Site icon: {iconError}");
            return Page();
        }
        var (faviconUrl, faviconError) = await uploads.SaveImageAsync(favicon, "site", maxMbOverride: 1);
        if (faviconError is not null)
        {
            uploads.Delete(iconUrl);
            ModelState.AddModelError("", $"Favicon: {faviconError}");
            return Page();
        }
        var replaced = new List<string>();
        if (iconUrl is not null || resetSiteIcon)
        {
            replaced.Add(Config.SiteIconPath);
            Config.SiteIconPath = iconUrl ?? "";
        }
        if (faviconUrl is not null || resetFavicon)
        {
            replaced.Add(Config.FaviconPath);
            Config.FaviconPath = faviconUrl ?? "";
        }

        var changes = SiteConfigDiff(before, Config);
        await settings.SaveAsync(Config);
        foreach (var old in replaced) uploads.Delete(old);
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
