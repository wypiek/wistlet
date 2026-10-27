using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;
using Wistlet.Services;
using Wistlet.Services.Federation;

namespace Wistlet.Pages.Admin;

/// <summary>Other Wistlet instances this one knows, and the queue of activities going out to them.</summary>
public class FederationModel(AppDbContext db, FederationContext fed, AuditService audit) : PageModel
{
    public record InstanceRow(RemoteInstance Instance, int Accounts, int Pending);

    public FederationContext Context => fed;
    public List<InstanceRow> Instances { get; private set; } = [];
    public int Pending { get; private set; }
    public List<FederationDelivery> Failed { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var instances = await db.RemoteInstances.AsNoTracking().OrderByDescending(i => i.LastSeenAt).ToListAsync();
        var accounts = await db.Users.Where(u => u.Domain != null).GroupBy(u => u.Domain!).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        var pending = await db.FederationDeliveries.Where(d => d.FailedAt == null).GroupBy(d => d.Domain).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        Instances = instances.Select(i => new InstanceRow(i, accounts.GetValueOrDefault(i.Domain), pending.GetValueOrDefault(i.Domain))).ToList();
        Pending = pending.Values.Sum();
        Failed = await db.FederationDeliveries.AsNoTracking().Where(d => d.FailedAt != null).OrderByDescending(d => d.Id).Take(50).ToListAsync();
    }

    private async Task<RemoteInstance?> FindAsync(string domain) => await db.RemoteInstances.FirstOrDefaultAsync(i => i.Domain == domain);

    public async Task<IActionResult> OnPostBlockAsync(string domain)
    {
        if (await FindAsync(domain) is not { } instance) return NotFound();
        instance.IsBlocked = !instance.IsBlocked;
        audit.Log(instance.IsBlocked ? "federation.block" : "federation.unblock", domain);
        await db.SaveChangesAsync();
        TempData["Flash"] = instance.IsBlocked
            ? $"{domain} is blocked: nothing is accepted from or sent to it, and its posts are hidden."
            : $"{domain} is unblocked.";
        return RedirectToPage();
    }

    /// <summary>Forgets the pinned key, so the instance's current one is fetched on its next request (after a key change there).</summary>
    public async Task<IActionResult> OnPostForgetKeyAsync(string domain)
    {
        if (await FindAsync(domain) is not { } instance) return NotFound();
        instance.PublicKey = null;
        audit.Log("federation.forget-key", domain);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"The key for {domain} will be fetched again on its next request.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync()
    {
        var count = await db.FederationDeliveries.Where(d => d.FailedAt != null).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.FailedAt, (DateTime?)null)
            .SetProperty(d => d.Attempts, 0)
            .SetProperty(d => d.NextAttemptAt, DateTime.UtcNow));
        TempData["Flash"] = $"Retrying {count} deliveries.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        var count = await db.FederationDeliveries.Where(d => d.FailedAt != null).ExecuteDeleteAsync();
        TempData["Flash"] = $"Removed {count} failed deliveries.";
        return RedirectToPage();
    }
}
