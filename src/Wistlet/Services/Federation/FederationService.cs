using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;

namespace Wistlet.Services.Federation;

/// <summary>
/// Outgoing federation: the documents this instance publishes, the activities it queues for
/// other instances when local users act, and fetching (and caching) accounts and posts from
/// other Wistlet instances.
/// </summary>
public class FederationService(AppDbContext db, FederationContext fed, SiteSettingsService settings, ILogger<FederationService> logger)
{
    /// <summary>Remote profiles are fetched again after this long.</summary>
    private static readonly TimeSpan ProfileLifetime = TimeSpan.FromDays(1);

    /// <summary>On a new follow, recent posts from this far back are fetched.</summary>
    private static readonly TimeSpan BackfillWindow = TimeSpan.FromDays(7);
    private const int BackfillCount = 10;

    public bool Enabled => fed.IsEnabled;
    public string? Domain => fed.Domain;

    // ---------- documents this instance publishes ----------

    public string PostUrl(int id) => $"{fed.Origin}/post/{id}";

    private string? Absolute(string? path) => path is not null && path.StartsWith('/') ? fed.Origin + path : null;

    /// <summary>Whether an account's posts go to other instances (not for banned or logged-in-only pages).</summary>
    public static bool Federates(User u) => !u.IsRemote && !u.IsCurrentlyBanned && !u.HiddenFromGuests;

    public ActorDocument ActorFor(User u) => new(
        u.UserName, u.DisplayName, u.Dek, u.Pronouns, u.Bio,
        Absolute(u.AvatarPath), AvatarShapes.Resolve(u.AvatarShape), Absolute(u.BannerPath),
        u.IsAdultPage, $"{fed.Origin}/u/{u.UserName}");

    public PostDocument PostFor(Post p, string authorName, string? rootUrl = null, string? parentUrl = null) => new(
        PostUrl(p.Id), authorName,
        DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc),
        p.EditedAt is DateTime e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
        p.Headline, p.Content,
        p.Tags.OrderBy(t => t.Position).Select(t => t.Tag).ToList(),
        p.ContentWarnings, p.IsAdult,
        Absolute(p.ImagePath),
        p.AudioPath is null ? null : new AudioDocument(Absolute(p.AudioPath)!, p.AudioTitle, p.AudioArtist),
        rootUrl, parentUrl);

    // ---------- queueing activities ----------

    private async Task EnqueueAsync(IEnumerable<string> domains, Activity activity)
    {
        var targets = domains.Where(d => d != fed.Domain).Distinct().ToList();
        if (targets.Count == 0) return;
        var blocked = await db.RemoteInstances.Where(i => i.IsBlocked && targets.Contains(i.Domain)).Select(i => i.Domain).ToListAsync();
        var body = JsonSerializer.Serialize(activity, FederationContext.Json);
        foreach (var domain in targets.Except(blocked))
            db.FederationDeliveries.Add(new FederationDelivery { Domain = domain, Body = body });
        await db.SaveChangesAsync();
    }

    private Task<List<string>> FollowerDomainsAsync(int userId) =>
        db.Follows.Where(f => f.FolloweeId == userId && f.Follower.Domain != null)
            .Select(f => f.Follower.Domain!).Distinct().ToListAsync();

    /// <summary>A local post or comment was written (or edited): send it where it needs to go.</summary>
    public async Task PostChangedAsync(Post post)
    {
        if (!Enabled) return;
        var author = await db.Users.FirstAsync(u => u.Id == post.AuthorId);
        if (!Federates(author)) return;
        await db.Entry(post).Collection(p => p.Tags).LoadAsync();

        if (post.ParentId is null)
        {
            // Plain posts go to every instance with followers of the author. Shares stay local.
            if (post.IsShare) return;
            await EnqueueAsync(await FollowerDomainsAsync(author.Id), new Activity(ActivityTypes.Post, author.UserName, Post: PostFor(post, author.UserName)));
            return;
        }

        // Comments on a post from another instance go back to that instance.
        var root = await db.Posts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == post.RootId);
        if (root?.RemoteId is null || root.Author.Domain is null) return;
        var parent = post.ParentId == root.Id ? root : await db.Posts.FirstOrDefaultAsync(p => p.Id == post.ParentId);
        var parentUrl = parent is null || parent.Id == root.Id ? root.RemoteId : parent.RemoteId ?? PostUrl(parent.Id);
        await EnqueueAsync([root.Author.Domain],
            new Activity(ActivityTypes.Comment, author.UserName, Post: PostFor(post, author.UserName, root.RemoteId, parentUrl)));
    }

    /// <summary>A local post or comment is about to be deleted: tell the instances that have a copy.</summary>
    public async Task PostDeletingAsync(Post post)
    {
        if (!Enabled || post.RemoteId is not null) return;
        var author = await db.Users.FirstAsync(u => u.Id == post.AuthorId);
        if (author.IsRemote) return;
        var activity = new Activity(ActivityTypes.Delete, author.UserName, Object: PostUrl(post.Id));
        if (post.ParentId is null)
        {
            if (!post.IsShare) await EnqueueAsync(await FollowerDomainsAsync(author.Id), activity);
            return;
        }
        var rootDomain = await db.Posts.Where(p => p.Id == post.RootId && p.RemoteId != null).Select(p => p.Author.Domain).FirstOrDefaultAsync();
        if (rootDomain is not null) await EnqueueAsync([rootDomain], activity);
    }

    /// <summary>A local user liked or unliked a post; posts from other instances hear about it.</summary>
    public async Task LikeChangedAsync(Post post, int userId, bool liked)
    {
        if (!Enabled || post.RemoteId is null) return;
        var domain = await db.Users.Where(u => u.Id == post.AuthorId).Select(u => u.Domain).FirstAsync();
        var me = await db.Users.FirstAsync(u => u.Id == userId);
        if (domain is null || !Federates(me)) return;
        await EnqueueAsync([domain], new Activity(liked ? ActivityTypes.Like : ActivityTypes.Unlike, me.UserName, Object: post.RemoteId));
    }

    public async Task ProfileChangedAsync(User user)
    {
        if (!Enabled || !Federates(user)) return;
        await EnqueueAsync(await FollowerDomainsAsync(user.Id), new Activity(ActivityTypes.Profile, user.UserName, Profile: ActorFor(user)));
    }

    /// <summary>A local account is about to be deleted: instances that know it remove their copy.</summary>
    public async Task AccountDeletingAsync(User user)
    {
        if (!Enabled || user.IsRemote) return;
        var domains = await db.Follows.Where(f => f.FolloweeId == user.Id && f.Follower.Domain != null).Select(f => f.Follower.Domain!)
            .Union(db.Follows.Where(f => f.FollowerId == user.Id && f.Followee.Domain != null).Select(f => f.Followee.Domain!))
            .ToListAsync();
        await EnqueueAsync(domains, new Activity(ActivityTypes.DeleteAccount, user.UserName));
    }

    /// <summary>
    /// Follows an account on another instance. This is sent straight away (not queued) so the
    /// person gets an answer: returns an error message, or null once the other instance accepted.
    /// </summary>
    public async Task<string?> FollowAsync(User me, User remote)
    {
        if (!Enabled) return "Federation is turned off on this site.";
        if (!Federates(me)) return "Pages hidden from logged-out visitors can't follow accounts on other sites.";
        var activity = new Activity(ActivityTypes.Follow, me.UserName, Target: LocalPart(remote));
        try
        {
            var (status, body) = await fed.PostToInboxAsync(remote.Domain!, JsonSerializer.SerializeToUtf8Bytes(activity, FederationContext.Json));
            if ((int)status is >= 200 and < 300) return null;
            logger.LogInformation("{Domain} refused a follow of {User}: {Status} {Body}", remote.Domain, remote.UserName, (int)status, body);
            return status == HttpStatusCode.Forbidden ? $"{remote.Domain} didn't accept the follow." : $"{remote.Domain} couldn't be reached ({(int)status}). Try again later.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(e, "Could not reach {Domain}", remote.Domain);
            return $"{remote.Domain} couldn't be reached. Try again later.";
        }
    }

    public async Task UnfollowAsync(User me, User remote)
    {
        if (!Enabled) return;
        await EnqueueAsync([remote.Domain!], new Activity(ActivityTypes.Unfollow, me.UserName, Target: LocalPart(remote)));
    }

    /// <summary>"annie" from "annie@other.example".</summary>
    public static string LocalPart(User u) => u.UserName.Split('@')[0];

    // ---------- other instances ----------

    /// <summary>
    /// The record for another instance, fetching and pinning its key on first contact. Null if the
    /// domain isn't a reachable Wistlet instance (or is blocked).
    /// </summary>
    public async Task<RemoteInstance?> EnsureInstanceAsync(string domain, bool includeBlocked = false)
    {
        if (!FederationProtocol.IsValidDomain(domain) || domain == fed.Domain) return null;
        var instance = await db.RemoteInstances.FirstOrDefaultAsync(i => i.Domain == domain);
        if (instance is { IsBlocked: true }) return includeBlocked ? instance : null;

        if (instance?.PublicKey is null)
        {
            InstanceDocument? doc;
            try { doc = await fed.GetJsonAsync<InstanceDocument>(fed.OriginOf(domain) + FederationProtocol.DiscoveryPath); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { doc = null; }
            // Only other Wistlet instances federate.
            if (doc is null || doc.Software != FederationProtocol.Software || doc.Protocol != FederationProtocol.Version ||
                !string.Equals(doc.Domain, domain, StringComparison.OrdinalIgnoreCase) || !IsValidKey(doc.PublicKey))
                return null;
            instance ??= db.RemoteInstances.Add(new RemoteInstance { Domain = domain }).Entity;
            instance.PublicKey = doc.PublicKey;
            instance.Name = Truncate(doc.Name, 100);
        }
        instance.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return instance;
    }

    private static bool IsValidKey(string? spki)
    {
        if (string.IsNullOrEmpty(spki) || spki.Length > 500) return false;
        try
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return true;
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds an account from "@name@domain": a local account if the domain is this site, otherwise
    /// the account on that Wistlet instance (fetched now if it isn't known or is out of date).
    /// </summary>
    public async Task<User?> ResolveHandleAsync(string handle)
    {
        if (!FederationProtocol.TryParseHandle(handle, out var name, out var domain)) return null;
        if (domain == fed.Domain)
        {
            var normalized = AccountService.Normalize(name);
            return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized && u.Domain == null);
        }
        return Enabled ? await ResolveActorAsync(name, domain) : null;
    }

    /// <summary>The local copy of an account on another instance, created or refreshed from its profile.</summary>
    public async Task<User?> ResolveActorAsync(string name, string domain, bool refresh = false)
    {
        if (!FederationProtocol.IsValidName(name) || await EnsureInstanceAsync(domain) is null) return null;
        var normalized = $"{name}@{domain}".ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized);
        if (user is not null && !refresh && user.RemoteFetchedAt > DateTime.UtcNow - ProfileLifetime) return user;

        ActorDocument? doc;
        try { doc = await fed.GetJsonAsync<ActorDocument>($"{fed.OriginOf(domain)}/federation/users/{name}"); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { doc = null; }
        if (doc is null || !string.Equals(doc.Name, name, StringComparison.OrdinalIgnoreCase)) return user;

        user ??= db.Users.Add(new User { Domain = domain, PasswordHash = "", Email = "" }).Entity;
        ApplyActor(user, doc, domain);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Copies a remote profile onto its local record, within the same limits local profiles have.</summary>
    public static void ApplyActor(User user, ActorDocument doc, string domain)
    {
        user.UserName = $"{doc.Name}@{domain}";
        user.NormalizedUserName = user.UserName.ToLowerInvariant();
        user.Domain = domain;
        user.DisplayName = Truncate(doc.DisplayName, 50) ?? doc.Name;
        user.Dek = Truncate(doc.Dek, 100) ?? "";
        user.Pronouns = Truncate(doc.Pronouns, 40) ?? "";
        user.Bio = Truncate(doc.Bio, 5000) ?? "";
        user.AvatarPath = FederationProtocol.IsUrlOn(doc.Avatar, domain, "/uploads/") ? doc.Avatar : null;
        user.BannerPath = FederationProtocol.IsUrlOn(doc.Banner, domain, "/uploads/") ? doc.Banner : null;
        user.AvatarShape = AvatarShapes.Resolve(doc.AvatarShape);
        user.IsAdultPage = doc.AdultPage;
        user.RemoteFetchedAt = DateTime.UtcNow;
    }

    private static string? Truncate(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>Renders a remote post's Markdown with this site's sanitizer, pointing relative links at its instance.</summary>
    public static (string Html, string? PreviewHtml) RenderRemote(string markdown, string remoteId)
    {
        var origin = new Uri(remoteId).GetLeftPart(UriPartial.Authority);
        var r = PostRenderer.Render(markdown);
        return (FederationProtocol.Absolutize(r.Html, origin), r.PreviewHtml is null ? null : FederationProtocol.Absolutize(r.PreviewHtml, origin));
    }

    /// <summary>
    /// Stores (or updates) a post or comment from another instance. Returns null when it's refused:
    /// wrong author, not from that instance, too long, banned words.
    /// </summary>
    public async Task<(Post? Post, bool Created)> UpsertRemotePostAsync(PostDocument doc, User author, int? parentId = null, int? rootId = null)
    {
        var domain = author.Domain!;
        var isComment = parentId is not null;
        if (!FederationProtocol.IsUrlOn(doc.Id, domain, "/post/") || !string.Equals(doc.Author, LocalPart(author), StringComparison.OrdinalIgnoreCase))
            return (null, false);

        var cfg = settings.Current;
        var markdown = (doc.Markdown ?? "").Replace("\r\n", "\n").Trim();
        if (markdown.Length > cfg.MaxPostLength) return (null, false);
        var headline = isComment ? null : Truncate(doc.Headline, 140);
        var tags = isComment ? [] : PostRenderer.ParseTags(string.Join(',', doc.Tags ?? []));
        var warnings = isComment ? [] : PostRenderer.ParseWarnings(doc.ContentWarnings);
        var image = FederationProtocol.IsUrlOn(doc.Image, domain, "/uploads/") ? doc.Image : null;
        var audio = !isComment && doc.Audio is { } a && FederationProtocol.IsUrlOn(a.Url, domain, "/uploads/") ? a : null;
        var text = string.Join('\n', [markdown, headline ?? "", .. tags, .. warnings, audio?.Title ?? "", audio?.Artist ?? ""]);
        if (cfg.GetBannedWords().Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return (null, false);
        if (markdown.Length == 0 && headline is null && image is null && audio is null) return (null, false);

        var post = await db.Posts.Include(p => p.Tags).FirstOrDefaultAsync(p => p.RemoteId == doc.Id);
        // An id keeps its author and its place: a post can't turn into a comment or move threads.
        if (post is not null && (post.AuthorId != author.Id || post.ParentId != parentId)) return (null, false);
        var created = post is null;
        if (post is null)
        {
            var at = DateTime.SpecifyKind(doc.CreatedAt, DateTimeKind.Utc).ToUniversalTime();
            post = db.Posts.Add(new Post
            {
                AuthorId = author.Id,
                RemoteId = doc.Id,
                CreatedAt = at > DateTime.UtcNow ? DateTime.UtcNow : at,
                ParentId = parentId,
                RootId = rootId,
            }).Entity;
        }
        else
        {
            post.EditedAt = DateTime.UtcNow;
            db.PostTags.RemoveRange(post.Tags);
            post.Tags.Clear();
        }

        var (html, preview) = RenderRemote(markdown, doc.Id);
        post.Content = markdown;
        post.Headline = headline;
        post.RenderedHtml = html;
        post.RenderedPreviewHtml = preview;
        post.RenderVersion = PostRenderer.Version;
        post.ContentWarnings = warnings.Count > 0 ? string.Join(", ", warnings) : null;
        post.IsAdult = !isComment && doc.Adult;
        post.ImagePath = image;
        post.AudioPath = audio?.Url;
        post.AudioTitle = Truncate(audio?.Title, 140);
        post.AudioArtist = Truncate(audio?.Artist, 140);
        for (var i = 0; i < tags.Count; i++) post.Tags.Add(new PostTag { Tag = tags[i], Position = i });
        await db.SaveChangesAsync();
        return (post, created);
    }

    /// <summary>After a new follow: fetches the account's recent posts so their page isn't empty.</summary>
    public async Task BackfillAsync(User remote)
    {
        List<PostDocument>? docs;
        try { docs = await fed.GetJsonAsync<List<PostDocument>>($"{fed.OriginOf(remote.Domain!)}/federation/users/{LocalPart(remote)}/posts"); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return; }
        if (docs is null) return;

        var since = DateTime.UtcNow - BackfillWindow;
        foreach (var doc in docs.Where(d => d.CreatedAt >= since).OrderBy(d => d.CreatedAt).TakeLast(BackfillCount))
        {
            if (await db.Posts.AnyAsync(p => p.RemoteId == doc.Id)) continue;
            await UpsertRemotePostAsync(doc with { Post = null, Parent = null }, remote);
        }
    }
}
