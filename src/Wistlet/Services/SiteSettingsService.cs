using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services;

/// <summary>
/// Strongly typed, admin-editable site configuration. Each property is persisted
/// as a row in the SiteSettings table, keyed by property name.
/// </summary>
public class SiteConfig
{
    [Display(Name = "Site name"), Required, StringLength(40)]
    public string SiteName { get; set; } = "Wistlet";

    [Display(Name = "Tagline"), StringLength(120)]
    public string Tagline { get; set; } = "A small social network.";

    [Display(Name = "About text"), StringLength(4000)]
    public string AboutText { get; set; } =
        "Wistlet is a small social network. Post, follow, like and chat.";

    [Display(Name = "Show announcement banner")]
    public bool AnnouncementEnabled { get; set; }

    [Display(Name = "Announcement text"), StringLength(500)]
    public string AnnouncementText { get; set; } = "";

    [Display(Name = "Allow new registrations")]
    public bool RegistrationOpen { get; set; } = true;

    [Display(Name = "Maintenance mode (only staff can use the site)")]
    public bool MaintenanceMode { get; set; }

    [Display(Name = "Maintenance message"), StringLength(500)]
    public string MaintenanceMessage { get; set; } = "We're doing some maintenance. Please check back soon.";

    [Display(Name = "Maximum post length"), Range(20, 5000)]
    public int MaxPostLength { get; set; } = 500;

    [Display(Name = "Allow image uploads in posts")]
    public bool AllowImageUploads { get; set; } = true;

    [Display(Name = "Maximum upload size (MB)"), Range(1, 50)]
    public int MaxUploadMb { get; set; } = 5;

    [Display(Name = "Allow direct messages")]
    public bool AllowDirectMessages { get; set; } = true;

    [Display(Name = "Allow guests to browse Explore and profiles")]
    public bool PublicBrowsing { get; set; } = true;

    [Display(Name = "Posts per page"), Range(5, 100)]
    public int PostsPerPage { get; set; } = 20;

    [Display(Name = "Banned words (comma separated, posts containing these are rejected)"), StringLength(4000)]
    public string BannedWords { get; set; } = "";

    [Display(Name = "Footer text"), StringLength(200)]
    public string FooterText { get; set; } = "Powered by Wistlet · Styled with 7.css";

    // A method rather than a property so model binding doesn't touch it.
    public IEnumerable<string> GetBannedWords() =>
        (BannedWords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public SiteConfig Clone() => (SiteConfig)MemberwiseClone();

    internal static readonly PropertyInfo[] Properties = typeof(SiteConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite)
        .ToArray();
}

/// <summary>Singleton cache of <see cref="SiteConfig"/> backed by the database.</summary>
public class SiteSettingsService(IServiceScopeFactory scopes)
{
    private volatile SiteConfig? _current;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SiteConfig Current => _current ?? throw new InvalidOperationException("Site settings not loaded yet.");

    public async Task LoadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        var config = new SiteConfig();
        foreach (var prop in SiteConfig.Properties)
        {
            if (!rows.TryGetValue(prop.Name, out var raw)) continue;
            try
            {
                prop.SetValue(config, Convert.ChangeType(raw, prop.PropertyType, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                // Ignore malformed values and keep the default.
            }
        }
        _current = config;
    }

    public async Task SaveAsync(SiteConfig config)
    {
        await _lock.WaitAsync();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.SiteSettings.ToDictionaryAsync(s => s.Key);
            foreach (var prop in SiteConfig.Properties)
            {
                var value = Convert.ToString(prop.GetValue(config), CultureInfo.InvariantCulture) ?? "";
                if (existing.TryGetValue(prop.Name, out var row)) row.Value = value;
                else db.SiteSettings.Add(new SiteSetting { Key = prop.Name, Value = value });
            }
            await db.SaveChangesAsync();
            _current = config.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }
}
