using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Services;

public class AuditService(AppDbContext db, IHttpContextAccessor http)
{
    /// <summary>Queues an audit entry; it is written with the next SaveChanges.</summary>
    public void Log(string action, string target, string? details = null)
    {
        var ctx = http.HttpContext;
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = ctx?.User.GetAccountId(),
            Action = action,
            Target = Truncate(target, 200),
            Details = details is null ? null : Truncate(details, 1000),
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
        });
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
