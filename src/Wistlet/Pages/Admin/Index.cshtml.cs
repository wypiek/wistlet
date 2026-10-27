using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;
using Wistlet.Services.Federation;

namespace Wistlet.Pages.Admin;

public class IndexModel(AppDbContext db, UploadService uploads, IConfiguration config, IWebHostEnvironment env,
    SiteSettingsService settings, FederationContext federation, EmailService email, ServerConfig server) : PageModel
{
    /// <summary>A number on the dashboard; <see cref="Note"/> is the smaller line under it.</summary>
    public record Stat(string Label, int Value, string? Note = null, string? Link = null, bool Alert = false);
    public record Day(DateOnly Date, int Count);

    /// <summary>Something an admin should look at (<see cref="Warning"/>) or know (otherwise).</summary>
    public record Attention(string Text, string? Link, string LinkText, bool Warning);

    public List<Stat> Stats { get; private set; } = [];
    public List<Attention> Attentions { get; private set; } = [];
    public List<Day> Signups { get; private set; } = [];
    public List<Day> PostsPerDay { get; private set; } = [];
    public List<User> RecentUsers { get; private set; } = [];
    public List<AuditLog> RecentActions { get; private set; } = [];
    public List<(string Label, string Value)> SystemInfo { get; private set; } = [];
    public IReadOnlyList<ServerConfig.Check> Configuration => server.Checks;

    public async Task OnGetAsync()
    {
        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var dayAgo = now.AddDays(-1);
        var local = db.Users.Where(u => u.Domain == null);
        var openReports = await db.Reports.CountAsync(r => r.Status == ReportStatus.Open);
        var failedDeliveries = await db.FederationDeliveries.CountAsync(d => d.FailedAt != null);
        var queuedDeliveries = await db.FederationDeliveries.CountAsync(d => d.FailedAt == null);

        Stats =
        [
            new("members", await local.CountAsync(), $"+{await local.CountAsync(u => u.CreatedAt >= weekAgo)} this week", "/admin/users"),
            new("active today", await local.CountAsync(u => u.LastSeenAt >= dayAgo), "seen in the last 24 hours", "/admin/users?sort=active"),
            new("posts", await db.Posts.CountAsync(p => p.ParentId == null), $"+{await db.Posts.CountAsync(p => p.ParentId == null && p.CreatedAt >= dayAgo)} today", "/admin/posts"),
            new("comments", await db.Posts.CountAsync(p => p.ParentId != null), $"+{await db.Posts.CountAsync(p => p.ParentId != null && p.CreatedAt >= dayAgo)} today"),
            new("open reports", openReports, openReports == 0 ? "nothing waiting" : "waiting for a moderator", "/admin/reports", openReports > 0),
            new("banned", await local.CountAsync(u => u.IsBanned), $"{await db.Posts.CountAsync(p => p.IsHidden)} hidden posts", "/admin/users?status=banned"),
        ];

        var cfg = settings.Current;
        if (openReports > 0)
            Attentions.Add(new($"{Fmt.Plural(openReports, "report")} waiting for a moderator.", "/admin/reports", "Review", true));
        if (failedDeliveries > 0)
            Attentions.Add(new($"{Fmt.Plural(failedDeliveries, "federation delivery", "federation deliveries")} failed.", "/admin/federation", "Look", true));
        if (cfg.MaintenanceMode)
            Attentions.Add(new("Maintenance mode is on: only staff can use the site.", "/admin/settings#access", "Change", true));
        foreach (var problem in server.Problems)
            Attentions.Add(new($"{problem.Variable}: {problem.Message}", "#server-configuration", "Details", true));
        if (cfg.FederationEnabled && !federation.IsConfigured)
            Attentions.Add(new("Federation is turned on in Site settings, but it can't work until WISTLET_PUBLIC_URL is set correctly.", "#server-configuration", "Details", true));
        if (!federation.IsConfigured && server.Problems.All(p => p.Variable != "WISTLET_PUBLIC_URL"))
            Attentions.Add(new("WISTLET_PUBLIC_URL isn't set: e-mail links use whatever address a request came in on, and federation is unavailable.", null, "", false));
        if (!email.HasServer && server.Problems.All(p => p.Variable != "WISTLET_SMTP_HOST"))
            Attentions.Add(new("No SMTP server is configured, so password resets by e-mail are off.", null, "", false));
        if (!cfg.RegistrationOpen)
            Attentions.Add(new("Registration is closed.", "/admin/settings#access", "Change", false));
        if (cfg.AnnouncementEnabled)
            Attentions.Add(new("The announcement banner is showing.", "/admin/settings#announcement", "Edit", false));

        var since = DateOnly.FromDateTime(now).AddDays(-13);
        var sinceDt = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var signupDates = await local.Where(u => u.CreatedAt >= sinceDt).Select(u => u.CreatedAt).ToListAsync();
        var postDates = await db.Posts.Where(p => p.CreatedAt >= sinceDt && p.RemoteId == null).Select(p => p.CreatedAt).ToListAsync();
        Signups = Bucket(signupDates, since);
        PostsPerDay = Bucket(postDates, since);

        RecentUsers = await local.AsNoTracking().OrderByDescending(u => u.Id).Take(6).ToListAsync();
        RecentActions = await db.AuditLogs.AsNoTracking().Include(a => a.Actor).OrderByDescending(a => a.Id).Take(6).ToListAsync();

        var dataPath = config["Wistlet:DataPath"]!;
        var dbFile = Path.Combine(dataPath, "wistlet.db");
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var uptime = DateTime.UtcNow - started;
        SystemInfo =
        [
            ("Runtime", RuntimeInformation.FrameworkDescription),
            ("Operating system", RuntimeInformation.OSDescription),
            ("Environment", env.EnvironmentName),
            ("Uptime", $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"),
            ("Database size", FileSize(dbFile)),
            ("Uploads", DirectorySize(uploads.Root)),
            ("Data directory", dataPath),
            ("Public address", federation.Origin ?? "not set"),
            ("E-mail", email.HasServer ? "SMTP configured" : env.IsDevelopment() ? "written to the log (development)" : "not configured"),
            ("Federation", !federation.IsConfigured ? "unavailable" : federation.IsEnabled
                ? $"on, {Fmt.Plural(await db.RemoteInstances.CountAsync(), "known instance")}, {queuedDeliveries} queued"
                : "off"),
        ];
    }

    private static List<Day> Bucket(List<DateTime> dates, DateOnly since)
    {
        var counts = dates.GroupBy(d => DateOnly.FromDateTime(d)).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, 14).Select(i => since.AddDays(i)).Select(d => new Day(d, counts.GetValueOrDefault(d))).ToList();
    }

    private static string FileSize(string file) =>
        System.IO.File.Exists(file) ? Fmt.Bytes(new FileInfo(file).Length) : "—";

    private static string DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return "—";
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        return $"{Fmt.Bytes(files.Sum(f => f.Length))} in {files.Count} files";
    }
}
