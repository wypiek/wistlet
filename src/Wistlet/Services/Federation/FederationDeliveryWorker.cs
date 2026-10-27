using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;

namespace Wistlet.Services.Federation;

/// <summary>
/// Sends queued activities to other instances, oldest first. Failed deliveries are retried with
/// growing gaps for about a day; a refusal (a 4xx answer) isn't retried.
/// </summary>
public class FederationDeliveryWorker(IServiceScopeFactory scopes, FederationContext fed, ILogger<FederationDeliveryWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                if (fed.IsEnabled) sent = await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Federation delivery failed");
            }
            try { await Task.Delay(sent > 0 ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Retry gaps: 30 s, 1, 2, 4 … minutes, capped at 6 hours.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, attempts - 1), 6 * 3600));

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.FederationDeliveries.Where(d => d.FailedAt == null && d.NextAttemptAt <= now)
            .OrderBy(d => d.Id).Take(20).ToListAsync(ct);
        if (due.Count == 0) return 0;

        var domains = due.Select(d => d.Domain).Distinct().ToList();
        var blocked = await db.RemoteInstances.Where(i => i.IsBlocked && domains.Contains(i.Domain)).Select(i => i.Domain).ToListAsync(ct);

        foreach (var delivery in due)
        {
            if (blocked.Contains(delivery.Domain))
            {
                db.FederationDeliveries.Remove(delivery);
                continue;
            }
            delivery.Attempts++;
            try
            {
                var (status, body) = await fed.PostToInboxAsync(delivery.Domain, Encoding.UTF8.GetBytes(delivery.Body), ct);
                var code = (int)status;
                if (code is >= 200 and < 300)
                {
                    db.FederationDeliveries.Remove(delivery);
                    continue;
                }
                delivery.LastError = $"{code} {body}".Trim();
                if (code is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
                    delivery.FailedAt = DateTime.UtcNow;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                delivery.LastError = e.Message;
            }
            if (delivery.LastError?.Length > 500) delivery.LastError = delivery.LastError[..500];
            if (delivery.FailedAt is null && delivery.Attempts >= MaxAttempts) delivery.FailedAt = DateTime.UtcNow;
            delivery.NextAttemptAt = DateTime.UtcNow + Backoff(delivery.Attempts);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
