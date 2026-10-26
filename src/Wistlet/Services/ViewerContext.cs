using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Services;

/// <summary>Per-request information about the signed-in user, used by the layout.</summary>
public class ViewerContext(AppDbContext db, IHttpContextAccessor http)
{
    private bool _loaded;
    private User? _user;

    public int? UserId => http.HttpContext?.User.GetUserId();

    public async Task<User?> GetUserAsync()
    {
        if (_loaded) return _user;
        _loaded = true;
        if (UserId is int id) _user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        return _user;
    }

    public async Task<(int Notifications, int Messages)> GetUnreadCountsAsync()
    {
        if (UserId is not int id) return (0, 0);
        var notes = await db.Notifications.CountAsync(n => n.UserId == id && !n.IsRead);
        var messages = await db.Messages.CountAsync(m => m.RecipientId == id && m.ReadAt == null);
        return (notes, messages);
    }

    /// <summary>Unanswered asks waiting in the viewer's inbox.</summary>
    public Task<int> GetPendingAskCountAsync() =>
        UserId is int id ? db.Asks.CountAsync(a => a.RecipientId == id && !a.IsAnswered) : Task.FromResult(0);

    /// <summary>The pages the signed-in account can switch between (its own page first).</summary>
    public async Task<List<User>> GetPagesAsync()
    {
        if (http.HttpContext?.User.GetAccountId() is not int account) return [];
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == account || u.OwnerId == account)
            .OrderBy(u => u.OwnerId != null).ThenBy(u => u.Id)
            .ToListAsync();
    }

    public Task<int> GetOpenReportCountAsync() => db.Reports.CountAsync(r => r.Status == ReportStatus.Open);
}
