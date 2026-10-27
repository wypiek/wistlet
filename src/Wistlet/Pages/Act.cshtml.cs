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
        bool isAdult, IFormFile? image, int? parentId, int? shareOfId, IFormFile? audio, string? audioTitle, string? audioArtist, string? returnUrl)
    {
        var draft = new PostDraft(content, headline, tags, contentWarnings, isAdult, image, parentId, shareOfId, audio, audioTitle, audioArtist);
        var (post, error) = await posts.CreateAsync(Me, draft);
        if (error is not null)
        {
            TempData["Error"] = error;
            TempData["Draft"] = content;
            return Back(returnUrl);
        }
        if (parentId is not null) return LocalRedirect($"{Fmt.SafeReturnUrl(returnUrl)}#post-{post!.Id}");
        TempData["Flash"] = shareOfId is not null ? "Shared!" : "Your post is live.";
        return shareOfId is not null ? LocalRedirect($"/post/{post!.Id}") : Back(returnUrl);
    }

    /// <summary>The share button: shares a post without adding anything.</summary>
    public async Task<IActionResult> OnPostShareAsync(int id, string? returnUrl)
    {
        var (_, error) = await posts.CreateAsync(Me, new PostDraft(null, ShareOfId: id));
        TempData[error is null ? "Flash" : "Error"] = error ?? "Shared to your page.";
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostEditAsync(int id, string? content, string? headline, string? tags, string? contentWarnings, bool isAdult,
        string? audioTitle, string? audioArtist)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id);
        if (post is null) return NotFound();
        if (post.AuthorId != Me) return Forbid();
        var error = await posts.UpdateAsync(post, new PostDraft(content, headline, tags, contentWarnings, isAdult, AudioTitle: audioTitle, AudioArtist: audioArtist));
        if (error is not null)
        {
            TempData["Error"] = error;
            return Redirect($"/post/{id}/edit");
        }
        TempData["Flash"] = "Post updated.";
        return Redirect(post.ParentId is int parent ? $"/post/{parent}#post-{id}" : $"/post/{id}");
    }

    /// <summary>Renders a draft exactly as it will appear, for the composer's preview.</summary>
    public IActionResult OnPostPreview(string? content)
    {
        content ??= "";
        if (content.Length > settings.Current.MaxPostLength) content = content[..settings.Current.MaxPostLength];
        var rendered = PostRenderer.Render(content);
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

    /// <summary>Lets a post's author hide (or show again) a comment on their post.</summary>
    public async Task<IActionResult> OnPostHideCommentAsync(int id, string? returnUrl)
    {
        var comment = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.RootId != null);
        if (comment is null) return NotFound();
        if (!await db.Posts.AnyAsync(p => p.Id == comment.RootId && p.AuthorId == Me)) return Forbid();
        comment.HiddenByPostAuthor = !comment.HiddenByPostAuthor;
        await db.SaveChangesAsync();
        TempData["Flash"] = comment.HiddenByPostAuthor
            ? "Comment hidden. Only you and its author can still see it."
            : "Comment shown again.";
        return Back(returnUrl);
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
        if (existing is not null)
        {
            db.Follows.Remove(existing);
        }
        else
        {
            if (await db.Blocks.AnyAsync(b => (b.BlockerId == Me && b.BlockedId == id) || (b.BlockerId == id && b.BlockedId == Me)))
            {
                TempData["Error"] = "You can't follow this account.";
                return Back(returnUrl);
            }
            db.Follows.Add(new Follow { FollowerId = Me, FolloweeId = id });
            await notifications.NotifyAsync(id, Me, NotificationType.Follow);
        }
        await db.SaveChangesAsync();
        return Back(returnUrl);
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
            TempData["Flash"] = $"Blocked @{target.UserName}. You won't see each other's posts or messages.";
        }
        await db.SaveChangesAsync();
        return Back(returnUrl);
    }

    private static string Excerpt(string s) => s.Length <= 120 ? s : s[..117] + "…";
}
