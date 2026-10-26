using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages;

/// <summary>Form-post endpoints for social actions. Every handler redirects back to <c>returnUrl</c>.</summary>
[Authorize]
[EnableRateLimiting("write")]
public class ActModel(AppDbContext db, PostService posts, NotificationService notifications, AuditService audit, SiteSettingsService settings) : PageModel
{
    private int Me => User.RequireUserId();

    public IActionResult OnGet() => Redirect("/");

    private IActionResult Back(string? returnUrl, string fallback = "/") => LocalRedirect(Fmt.SafeReturnUrl(returnUrl, fallback));

    public async Task<IActionResult> OnPostPostAsync(string? content, string? headline, string? tags, string? contentWarnings,
        bool isAdult, List<IFormFile>? images, List<string>? alt, List<string>? row, int? parentId, int? shareOfId, int? askId,
        bool commentsLocked, bool sharesLocked, string? save, string? returnUrl)
    {
        var asDraft = save == "draft";
        var draft = new PostDraft(content, headline, tags, contentWarnings, isAdult, images, parentId, shareOfId)
        {
            AltTexts = alt, Rows = row, AskId = askId, IsDraft = asDraft, CommentsLocked = commentsLocked, SharesLocked = sharesLocked,
        };
        var (post, error) = await posts.CreateAsync(Me, draft);
        if (error is not null)
        {
            TempData["Error"] = error;
            TempData["Draft"] = content;
            return Back(returnUrl);
        }
        if (parentId is not null) return LocalRedirect($"{Fmt.SafeReturnUrl(returnUrl)}#post-{post!.Id}");
        if (asDraft)
        {
            TempData["Flash"] = "Draft saved. Only people you send the link to can see it.";
            return LocalRedirect("/drafts");
        }
        TempData["Flash"] = shareOfId is not null ? "Shared!" : askId is not null ? "Answered!" : "Your post is live.";
        return shareOfId is not null || askId is not null ? LocalRedirect($"/post/{post!.Id}") : Back(returnUrl);
    }

    public async Task<IActionResult> OnPostPublishAsync(int id)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.IsDraft);
        if (post is null) return NotFound();
        if (post.AuthorId != Me) return Forbid();
        var (published, error) = await posts.PublishDraftAsync(post);
        if (error is not null)
        {
            TempData["Error"] = error;
            return LocalRedirect("/drafts");
        }
        TempData["Flash"] = "Your post is live.";
        return LocalRedirect($"/post/{published!.Id}");
    }

    /// <summary>Pins (or unpins) one of your own posts to the top of your page.</summary>
    public async Task<IActionResult> OnPostPinProfileAsync(int id, string? returnUrl)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.ParentId == null && !p.IsDraft);
        if (post is null) return NotFound();
        if (post.AuthorId != Me) return Forbid();
        post.PinnedToProfile = !post.PinnedToProfile;
        await db.SaveChangesAsync();
        TempData["Flash"] = post.PinnedToProfile ? "Pinned to your page." : "Unpinned from your page.";
        return Back(returnUrl);
    }

    /// <summary>The share button: shares a post without adding anything.</summary>
    public async Task<IActionResult> OnPostShareAsync(int id, string? returnUrl)
    {
        var (_, error) = await posts.CreateAsync(Me, new PostDraft(null, ShareOfId: id));
        TempData[error is null ? "Flash" : "Error"] = error ?? "Shared to your page.";
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostEditAsync(int id, string? content, string? headline, string? tags, string? contentWarnings, bool isAdult,
        List<IFormFile>? images, List<string>? alt, List<string>? row, Dictionary<int, string>? existingAlt, Dictionary<int, string>? existingRow,
        List<int>? removeAttachment,
        bool commentsLocked, bool sharesLocked, string? save)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();
        if (post.AuthorId != Me) return Forbid();
        var draft = new PostDraft(content, headline, tags, contentWarnings, isAdult, images)
        {
            AltTexts = alt, Rows = row, ExistingAltTexts = existingAlt, ExistingRows = existingRow, RemoveAttachments = removeAttachment,
            CommentsLocked = commentsLocked, SharesLocked = sharesLocked,
        };
        var error = await posts.UpdateAsync(post, draft);
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect($"/post/{id}/edit");
        }
        if (post.IsDraft)
        {
            if (save != "publish")
            {
                TempData["Flash"] = "Draft saved.";
                return Redirect("/drafts");
            }
            return await OnPostPublishAsync(id);
        }
        TempData["Flash"] = "Post updated.";
        return Redirect(post.ParentId is int parent ? $"/post/{parent}#post-{id}" : $"/post/{id}");
    }

    /// <summary>Renders a draft exactly as it will appear, for the composer's preview.</summary>
    public IActionResult OnPostPreview(string? content)
    {
        content ??= "";
        if (content.Length > settings.Current.MaxPostLength) content = content[..settings.Current.MaxPostLength];
        var rendered = posts.Render(content);
        return new JsonResult(new { html = rendered.Html, hasReadMore = rendered.PreviewHtml is not null });
    }

    public async Task<IActionResult> OnPostLikeAsync(int id, string? returnUrl)
    {
        var post = await posts.Visible(Me, User.IsStaff()).FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();

        var existing = await db.Likes.FindAsync(Me, id);
        if (existing is null)
        {
            db.Likes.Add(new Like { UserId = Me, PostId = id });
            await notifications.NotifyAsync(post.AuthorId, Me, NotificationType.Like, id);
        }
        else
        {
            db.Likes.Remove(existing);
        }
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostBookmarkAsync(int id, string? returnUrl)
    {
        if (!await posts.Visible(Me, User.IsStaff()).AnyAsync(p => p.Id == id)) return NotFound();
        var existing = await db.Bookmarks.FindAsync(Me, id);
        if (existing is null) db.Bookmarks.Add(new Bookmark { UserId = Me, PostId = id });
        else db.Bookmarks.Remove(existing);
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, string? returnUrl)
    {
        var post = await db.Posts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();
        if (post.AuthorId != Me && !User.IsStaff()) return Forbid();

        if (post.AuthorId != Me)
            audit.Log("post.delete", $"post #{post.Id} by @{post.Author.UserName}", Excerpt(post.Content));
        await posts.DeleteAsync(post);
        TempData["Flash"] = "Post deleted.";

        // Don't send people back to the page of the post they just deleted.
        var back = Fmt.SafeReturnUrl(returnUrl);
        if (back.StartsWith($"/post/{id}", StringComparison.OrdinalIgnoreCase))
            back = post.ParentId is int parent ? $"/post/{parent}" : "/";
        return LocalRedirect(back);
    }

    public async Task<IActionResult> OnPostHideAsync(int id, string? returnUrl)
    {
        if (!User.IsStaff()) return Forbid();
        var post = await db.Posts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();
        post.IsHidden = !post.IsHidden;
        audit.Log(post.IsHidden ? "post.hide" : "post.unhide", $"post #{post.Id} by @{post.Author.UserName}", Excerpt(post.Content));
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostPinAsync(int id, string? returnUrl)
    {
        if (!User.IsStaff()) return Forbid();
        var post = await db.Posts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();
        post.IsPinned = !post.IsPinned;
        audit.Log(post.IsPinned ? "post.pin" : "post.unpin", $"post #{post.Id} by @{post.Author.UserName}");
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostFollowAsync(int id, string? returnUrl)
    {
        if (id == Me) return Back(returnUrl);
        var target = await db.Users.FindAsync(id);
        if (target is null) return NotFound();

        var existing = await db.Follows.FindAsync(Me, id);
        var request = await db.FollowRequests.FindAsync(Me, id);
        if (existing is not null)
        {
            db.Follows.Remove(existing);
        }
        else if (request is not null)
        {
            // Pressing "requested" again withdraws the request.
            db.FollowRequests.Remove(request);
            await db.Notifications.Where(n => n.UserId == id && n.ActorId == Me && n.Type == NotificationType.FollowRequest).ExecuteDeleteAsync();
        }
        else
        {
            if (await db.Blocks.AnyAsync(b => (b.BlockerId == Me && b.BlockedId == id) || (b.BlockerId == id && b.BlockedId == Me)))
            {
                TempData["Error"] = "You can't follow this account.";
                return Back(returnUrl);
            }
            if (target.IsPrivate)
            {
                db.FollowRequests.Add(new FollowRequest { FollowerId = Me, FolloweeId = id });
                await notifications.NotifyAsync(id, Me, NotificationType.FollowRequest);
                TempData["Flash"] = $"@{target.UserName}'s page is private. They'll see your follow request.";
            }
            else
            {
                db.Follows.Add(new Follow { FollowerId = Me, FolloweeId = id });
                await notifications.NotifyAsync(id, Me, NotificationType.Follow);
            }
        }
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    /// <summary>Approves (or declines) a follow request to your private page.</summary>
    public async Task<IActionResult> OnPostFollowRequestAsync(int id, bool accept, string? returnUrl)
    {
        var request = await db.FollowRequests.FindAsync(id, Me);
        if (request is null) return Back(returnUrl, "/notifications");
        db.FollowRequests.Remove(request);
        await db.Notifications.Where(n => n.UserId == Me && n.ActorId == id && n.Type == NotificationType.FollowRequest).ExecuteDeleteAsync();
        if (accept && !await db.Follows.AnyAsync(f => f.FollowerId == id && f.FolloweeId == Me))
        {
            db.Follows.Add(new Follow { FollowerId = id, FolloweeId = Me });
            await notifications.NotifyAsync(id, Me, NotificationType.FollowAccepted);
        }
        await db.SaveChangesAsync();
        return Back(returnUrl, "/notifications");
    }

    /// <summary>Removes an unanswered ask from your inbox.</summary>
    public async Task<IActionResult> OnPostDeleteAskAsync(int id)
    {
        var ask = await db.Asks.FirstOrDefaultAsync(a => a.Id == id && a.RecipientId == Me);
        if (ask is null) return NotFound();
        db.Asks.Remove(ask);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Ask deleted.";
        return LocalRedirect("/inbox");
    }

    /// <summary>Blocks whoever sent an ask (also for anonymous asks, without revealing who it was) and deletes it.</summary>
    public async Task<IActionResult> OnPostBlockAskerAsync(int id)
    {
        var ask = await db.Asks.FirstOrDefaultAsync(a => a.Id == id && a.RecipientId == Me);
        if (ask is null) return NotFound();
        if (ask.SenderId is int sender && sender != Me && !await db.Blocks.AnyAsync(b => b.BlockerId == Me && b.BlockedId == sender))
        {
            db.Blocks.Add(new Block { BlockerId = Me, BlockedId = sender });
            db.Follows.RemoveRange(db.Follows.Where(f => (f.FollowerId == Me && f.FolloweeId == sender) || (f.FollowerId == sender && f.FolloweeId == Me)));
        }
        db.Asks.Remove(ask);
        await db.SaveChangesAsync();
        TempData["Flash"] = ask.SenderId is null
            ? "Ask deleted. It was sent while logged out, so there's no account to block."
            : "Ask deleted and the sender blocked.";
        return LocalRedirect("/inbox");
    }

    /// <summary>Follows, silences or CWs a tag (or undoes it).</summary>
    public async Task<IActionResult> OnPostTagAsync(string? tag, TagPreferenceKind kind, string? returnUrl, [FromServices] TagService tagService)
    {
        var parsed = PostRenderer.ParseTags(tag);
        if (parsed.Count == 0 || !Enum.IsDefined(kind)) return Back(returnUrl);
        var name = tagService.Canonical(parsed[0]);
        // Undoing also clears the setting for any synonym of the tag.
        var group = tagService.Group(name);
        var existing = await db.TagPreferences.Where(t => t.UserId == Me && t.Kind == kind && group.Contains(t.Tag)).ToListAsync();
        if (existing.Count > 0) db.TagPreferences.RemoveRange(existing);
        else db.TagPreferences.Add(new TagPreference { UserId = Me, Tag = name, Kind = kind });
        await db.SaveChangesAsync();
        return Back(returnUrl, $"/tag/{Uri.EscapeDataString(name)}");
    }

    public async Task<IActionResult> OnPostBlockAsync(int id, string? returnUrl)
    {
        if (id == Me) return Back(returnUrl);
        var target = await db.Users.FindAsync(id);
        if (target is null) return NotFound();

        var existing = await db.Blocks.FindAsync(Me, id);
        if (existing is not null)
        {
            db.Blocks.Remove(existing);
            TempData["Flash"] = $"Unblocked @{target.UserName}.";
        }
        else
        {
            db.Blocks.Add(new Block { BlockerId = Me, BlockedId = id });
            db.Follows.RemoveRange(db.Follows.Where(f => (f.FollowerId == Me && f.FolloweeId == id) || (f.FollowerId == id && f.FolloweeId == Me)));
            db.FollowRequests.RemoveRange(db.FollowRequests.Where(f => (f.FollowerId == Me && f.FolloweeId == id) || (f.FollowerId == id && f.FolloweeId == Me)));
            TempData["Flash"] = $"Blocked @{target.UserName}. You won't see each other's posts or messages.";
        }
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    private static string Excerpt(string s) => s.Length <= 120 ? s : s[..117] + "…";
}
