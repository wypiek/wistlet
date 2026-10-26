using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Pages.Admin;

public class AuditModel(AppDbContext db) : PageModel
{
    public const int PageSize = 100;

    public List<AuditLog> Entries { get; private set; } = [];
    public string? Q { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int Total { get; private set; }
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public string PageUrl(int page) => $"/admin/audit?q={Uri.EscapeDataString(Q ?? "")}&p={page}";

    public async Task OnGetAsync(string? q, int p = 1)
    {
        Q = q?.Trim();
        PageNumber = Math.Max(1, p);
        var query = db.AuditLogs.AsNoTracking().Include(a => a.Actor).AsQueryable();
        if (!string.IsNullOrEmpty(Q))
        {
            var term = Q.ToLower();
            query = query.Where(a => a.Action.ToLower().Contains(term) || a.Target.ToLower().Contains(term) ||
                                     (a.Details != null && a.Details.ToLower().Contains(term)) ||
                                     (a.Actor != null && a.Actor.NormalizedUserName.Contains(term.TrimStart('@'))));
        }
        Total = await query.CountAsync();
        Entries = await query.OrderByDescending(a => a.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize).ToListAsync();
    }
}
