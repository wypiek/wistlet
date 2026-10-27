using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Services;
using Wistlet.Services.Federation;

namespace Wistlet.Infrastructure;

/// <summary>The HTTP side of the federation protocol (see docs/federation.md).</summary>
public static class FederationEndpoints
{
    public static void MapFederation(this WebApplication app)
    {
        var group = app.MapGroup("").RequireRateLimiting("federation");

        group.MapGet(FederationProtocol.DiscoveryPath, (FederationContext fed, SiteSettingsService settings) =>
            !fed.IsEnabled
                ? Results.NotFound()
                : Results.Json(new InstanceDocument(FederationProtocol.Software, FederationProtocol.Version, fed.Domain!,
                    settings.Current.SiteName, fed.PublicKey, FederationProtocol.InboxPath), FederationContext.Json));

        group.MapGet("/federation/users/{name}", async (string name, FederationContext fed, FederationService federation, AppDbContext db) =>
        {
            if (!fed.IsEnabled) return Results.NotFound();
            var user = await FindAsync(db, name);
            return user is null ? Results.NotFound() : Results.Json(federation.ActorFor(user), FederationContext.Json);
        });

        group.MapGet("/federation/users/{name}/posts", async (string name, FederationContext fed, FederationService federation, AppDbContext db) =>
        {
            if (!fed.IsEnabled) return Results.NotFound();
            var user = await FindAsync(db, name);
            if (user is null) return Results.NotFound();
            var recent = await db.Posts.AsNoTracking().Include(p => p.Tags)
                .Where(p => p.AuthorId == user.Id && p.ParentId == null && !p.IsShare && !p.IsHidden)
                .OrderByDescending(p => p.Id).Take(20).ToListAsync();
            return Results.Json(recent.Select(p => federation.PostFor(p, user.UserName)), FederationContext.Json);
        });

        group.MapPost(FederationProtocol.InboxPath, async (HttpContext http, FederationInbox inbox) =>
        {
            if (http.Request.ContentLength > FederationProtocol.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            var read = new byte[16 * 1024];
            int n;
            while ((n = await http.Request.Body.ReadAsync(read, http.RequestAborted)) > 0)
            {
                buffer.Write(read, 0, n);
                if (buffer.Length > FederationProtocol.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var result = await inbox.ReceiveAsync(http.Request.Headers, buffer.ToArray());
            return Results.Json(new { message = result.Message }, statusCode: result.Status);
        });
    }

    /// <summary>A local account that may be shown to other instances.</summary>
    private static async Task<Models.User?> FindAsync(AppDbContext db, string name)
    {
        if (!FederationProtocol.IsValidName(name)) return null;
        var normalized = AccountService.Normalize(name);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Domain == null);
        return user is not null && FederationService.Federates(user) ? user : null;
    }
}
