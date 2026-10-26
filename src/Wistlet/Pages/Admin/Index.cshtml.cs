using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

public class IndexModel(AppDbContext db, UploadService uploads, IConfiguration config, IWebHostEnvironment env) : PageModel
{
    public record Stat(string Label, string Value, string? Link = null);
    public record Day(DateOnly Date, int Count);

    public List<Stat> Stats { get; private set; } = [];
    public List<Day> Signups { get; private set; } = [];
    public List<Day> PostsPerDay { get; private set; } = [];
    public List<User> RecentUsers { get; private set; } = [];
    public List<(string Label, string Value)> SystemInfo { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var dayAgo = now.AddDays(-1);

        Stats =
        [
            new("Members", (await db.Users.CountAsync()).ToString("N0"), "/admin/users"),
            new("New this week", (await db.Users.CountAsync(u => u.CreatedAt >= weekAgo)).ToString("N0"), "/admin/users?sort=new"),
            new("Active today", (await db.Users.CountAsync(u => u.LastSeenAt >= dayAgo)).ToString("N0")),
            new("Posts", (await db.Posts.CountAsync()).ToString("N0"), "/admin/posts"),
            new("Posts today", (await db.Posts.CountAsync(p => p.CreatedAt >= dayAgo)).ToString("N0")),
            new("Likes", (await db.Likes.CountAsync()).ToString("N0")),
            new("Messages", (await db.Messages.CountAsync()).ToString("N0")),
            new("Open reports", (await db.Reports.CountAsync(r => r.Status == ReportStatus.Open)).ToString("N0"), "/admin/reports"),
            new("Banned", (await db.Users.CountAsync(u => u.IsBanned)).ToString("N0"), "/admin/users?status=banned"),
            new("Hidden posts", (await db.Posts.CountAsync(p => p.IsHidden)).ToString("N0"), "/admin/posts?filter=hidden"),
        ];

        var since = DateOnly.FromDateTime(now).AddDays(-13);
        var sinceDt = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var signupDates = await db.Users.Where(u => u.CreatedAt >= sinceDt).Select(u => u.CreatedAt).ToListAsync();
        var postDates = await db.Posts.Where(p => p.CreatedAt >= sinceDt).Select(p => p.CreatedAt).ToListAsync();
        Signups = Bucket(signupDates, since);
        PostsPerDay = Bucket(postDates, since);

        RecentUsers = await db.Users.AsNoTracking().OrderByDescending(u => u.Id).Take(6).ToListAsync();

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
