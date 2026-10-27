using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Wistlet.Services;

namespace Wistlet.Infrastructure;

/// <summary>The Server-Sent Events stream behind live direct messages (see <see cref="LiveEvents"/>).</summary>
public static class LiveEndpoints
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(25);

    /// <summary>Streams end after this long and the browser reconnects, so the login is checked again.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static void MapLiveEvents(this WebApplication app)
    {
        app.MapGet("/events", async (HttpContext http, LiveEvents live, SiteSettingsService settings) =>
        {
            if (http.User.GetUserId() is not int me) return Results.Unauthorized();
            if (!settings.Current.AllowDirectMessages) return Results.NotFound();
            if (live.Subscribe(me) is not var (id, reader)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            try
            {
                var response = http.Response;
                response.ContentType = "text/event-stream";
                response.Headers.CacheControl = "no-cache";
                response.Headers["X-Accel-Buffering"] = "no";
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                lifetime.CancelAfter(Lifetime);
                var ct = lifetime.Token;

                // Reconnect after 5 s if the connection drops.
                await response.WriteAsync("retry: 5000\n\n", ct);
                await response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(Heartbeat);
                    string frame;
                    try { frame = await reader.ReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = ": ping\n\n"; }
                    await response.WriteAsync(frame, ct);
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            finally
            {
                live.Unsubscribe(me, id);
            }
            return Results.Empty;
        }).RequireAuthorization();
    }
}
