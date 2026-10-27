using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services.Federation;

/// <summary>Checks and applies activities other instances send to this one's inbox.</summary>
public class FederationInbox(
    AppDbContext db,
    FederationContext fed,
    FederationService federation,
    PostService posts,
    NotificationService notifications,
    IMemoryCache cache,
    ILogger<FederationInbox> logger)
{
    public record Result(int Status, string Message);

    private static Result Ok(string message = "ok") => new(200, message);

    /// <summary>Verifies a signed inbox request, then applies the activity in it.</summary>
    public async Task<Result> ReceiveAsync(IHeaderDictionary headers, byte[] body)
    {
        if (!fed.IsEnabled) return new(404, "federation is off");

        var sender = headers[FederationProtocol.InstanceHeader].ToString().ToLowerInvariant();
        if (!FederationProtocol.IsValidDomain(sender) || sender == fed.Domain) return new(400, "bad instance header");

        var date = headers[FederationProtocol.DateHeader].ToString();
        var digest = headers[FederationProtocol.DigestHeader].ToString();
        var signature = headers[FederationProtocol.SignatureHeader].ToString();
        if (FederationProtocol.CheckDateAndDigest(date, digest, body, DateTimeOffset.UtcNow) is { } problem) return new(401, problem);

        var instance = await federation.EnsureInstanceAsync(sender, includeBlocked: true);
        if (instance is null) return new(401, "not a reachable Wistlet instance");
        if (instance.IsBlocked) return new(403, "this instance is blocked");

        var signed = FederationProtocol.SigningString("POST", FederationProtocol.InboxPath, fed.Domain!, sender, date, digest);
        if (!FederationProtocol.VerifySignature(instance.PublicKey!, signed, signature))
        {
            // The key is pinned on first contact; a different key needs an admin to "forget" the old one.
            logger.LogWarning("Bad signature on a request from {Domain}", sender);
            return new(401, "bad signature");
        }

        // A signed request is accepted once.
        var replayKey = "federation-signature:" + signature;
        if (cache.TryGetValue(replayKey, out _)) return new(409, "already received");
        cache.Set(replayKey, true, FederationProtocol.MaxClockSkew * 2);

        Activity? activity;
        try { activity = JsonSerializer.Deserialize<Activity>(body, FederationContext.Json); }
        catch (JsonException) { activity = null; }
        if (activity is null || !FederationProtocol.IsValidName(activity.Actor)) return new(400, "malformed activity");

        return await ApplyAsync(sender, activity);
    }

    private async Task<Result> ApplyAsync(string sender, Activity a)
    {
        if (a.Type == ActivityTypes.DeleteAccount)
        {
            var normalized = $"{a.Actor}@{sender}".ToLowerInvariant();
            var gone = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Domain == sender);
            if (gone is not null)
            {
                db.Users.Remove(gone);
                await db.SaveChangesAsync();
            }
            return Ok("deleted");
        }

        var actor = await federation.ResolveActorAsync(a.Actor, sender);
        if (actor is null) return new(422, "couldn't fetch the actor's profile");
        if (actor.IsCurrentlyBanned) return new(403, "this account is suspended here");

        return a.Type switch
        {
            ActivityTypes.Follow => await FollowAsync(actor, a.Target, follow: true),
            ActivityTypes.Unfollow => await FollowAsync(actor, a.Target, follow: false),
            ActivityTypes.Post => await PostAsync(actor, a.Post),
            ActivityTypes.Comment => await CommentAsync(actor, a.Post),
            ActivityTypes.Delete => await DeleteAsync(actor, a.Object),
            ActivityTypes.Like => await LikeAsync(actor, a.Object, like: true),
            ActivityTypes.Unlike => await LikeAsync(actor, a.Object, like: false),
            ActivityTypes.Profile => await ProfileAsync(actor, a.Profile),
            _ => new(400, "unknown activity type"),
        };
    }

    private async Task<User?> LocalUserAsync(string? name)
    {
        if (!FederationProtocol.IsValidName(name)) return null;
        var normalized = AccountService.Normalize(name!);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Domain == null);
        return user is not null && FederationService.Federates(user) ? user : null;
    }

    private Task<bool> BlockedAsync(int a, int b) =>
        db.Blocks.AnyAsync(x => (x.BlockerId == a && x.BlockedId == b) || (x.BlockerId == b && x.BlockedId == a));

    private async Task<Result> FollowAsync(User actor, string? target, bool follow)
    {
        var local = await LocalUserAsync(target);
        if (local is null) return new(404, "no such account");
        var existing = await db.Follows.FindAsync(actor.Id, local.Id);
        if (!follow)
        {
            if (existing is not null) db.Follows.Remove(existing);
            await db.SaveChangesAsync();
            return Ok();
        }
        if (await BlockedAsync(actor.Id, local.Id)) return new(403, "not allowed");
        if (existing is null)
        {
            db.Follows.Add(new Follow { FollowerId = actor.Id, FolloweeId = local.Id });
            await notifications.NotifyAsync(local.Id, actor.Id, NotificationType.Follow);
            await db.SaveChangesAsync();
        }
        return Ok("following");
    }

    private async Task<Result> PostAsync(User actor, PostDocument? doc)
    {
        if (doc is null || doc.Post is not null) return new(400, "missing post");
        // Posts are only stored for accounts someone here follows (or for updates to ones already here).
        if (!await db.Follows.AnyAsync(f => f.FolloweeId == actor.Id) && !await db.Posts.AnyAsync(p => p.RemoteId == doc.Id))
            return new(202, "nobody here follows this account");
        var (post, _) = await federation.UpsertRemotePostAsync(doc, actor);
        return post is null ? new(422, "post refused") : Ok();
    }

    private async Task<Result> CommentAsync(User actor, PostDocument? doc)
    {
        if (doc is null) return new(400, "missing comment");
        var rootId = FederationProtocol.PostIdFromUrl(doc.Post, fed.Domain!);
        var root = rootId is null ? null : await posts.Visible(null, false).Include(p => p.Author)
            .FirstOrDefaultAsync(p => p.Id == rootId && p.ParentId == null && p.RemoteId == null);
        if (root is null) return new(404, "no such post");
        if (await BlockedAsync(actor.Id, root.AuthorId)) return new(403, "not allowed");

        // The comment answers the post itself, a local comment, or a comment from elsewhere stored here.
        Post? parent = root;
        if (doc.Parent is not null && doc.Parent != doc.Post)
        {
            var localId = FederationProtocol.PostIdFromUrl(doc.Parent, fed.Domain!);
            parent = localId is int id
                ? await db.Posts.FirstOrDefaultAsync(p => p.Id == id)
                : await db.Posts.FirstOrDefaultAsync(p => p.RemoteId == doc.Parent);
            if (parent is null || (parent.Id != root.Id && parent.RootId != root.Id)) return new(404, "no such comment");
        }

        var (comment, created) = await federation.UpsertRemotePostAsync(doc with { Headline = null, Tags = null, ContentWarnings = null, Audio = null },
            actor, parent.Id, root.Id);
        if (comment is null) return new(422, "comment refused");
        if (created)
        {
            await notifications.NotifyAsync(parent.AuthorId, actor.Id, NotificationType.Reply, comment.Id);
            await db.SaveChangesAsync();
        }
        return Ok();
    }

    private async Task<Result> DeleteAsync(User actor, string? url)
    {
        var post = url is null ? null : await db.Posts.FirstOrDefaultAsync(p => p.RemoteId == url);
        if (post is null) return Ok("not here");
        if (post.AuthorId != actor.Id) return new(403, "not the author");
        await posts.DeleteAsync(post);
        return Ok("deleted");
    }

    private async Task<Result> LikeAsync(User actor, string? url, bool like)
    {
        var id = FederationProtocol.PostIdFromUrl(url, fed.Domain!);
        var post = id is null ? null : await posts.Visible(null, false).FirstOrDefaultAsync(p => p.Id == id && p.RemoteId == null);
        if (post is null) return new(404, "no such post");
        var existing = await db.Likes.FindAsync(actor.Id, post.Id);
        if (like && existing is null && !await BlockedAsync(actor.Id, post.AuthorId))
        {
            db.Likes.Add(new Like { UserId = actor.Id, PostId = post.Id });
            await notifications.NotifyAsync(post.AuthorId, actor.Id, NotificationType.Like, post.Id);
        }
        else if (!like && existing is not null)
        {
            db.Likes.Remove(existing);
        }
        await db.SaveChangesAsync();
        return Ok();
    }

    private async Task<Result> ProfileAsync(User actor, ActorDocument? profile)
    {
        if (profile is null || !string.Equals(profile.Name, FederationService.LocalPart(actor), StringComparison.OrdinalIgnoreCase))
            return new(400, "missing profile");
        FederationService.ApplyActor(actor, profile, actor.Domain!);
        await db.SaveChangesAsync();
        return Ok();
    }
}
