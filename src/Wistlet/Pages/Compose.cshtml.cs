using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;
using Wistlet.Models;
using Wistlet.Services;
using Microsoft.EntityFrameworkCore;

namespace Wistlet.Pages;

[Authorize]
public class ComposeModel(PostService posts, Data.AppDbContext db) : PageModel
{
    /// <summary>The post being shared with added content, if any.</summary>
    public PostView? Sharing { get; private set; }

    /// <summary>The ask being answered, if any.</summary>
    public AskView? Answering { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? share, int? ask)
    {
        if (ask is int askId)
        {
            var me = User.RequireUserId();
            Answering = await db.Asks.AsNoTracking()
                .Where(a => a.Id == askId && a.RecipientId == me && !a.IsAnswered)
                .Select(a => new AskView(a.Id, a.Content,
                    a.IsAnonymous || a.Sender == null ? null
                        : new UserSummary(a.Sender.Id, a.Sender.UserName, a.Sender.DisplayName, a.Sender.AvatarPath, a.Sender.IsVerified, a.Sender.Role, "", a.Sender.AvatarShape),
                    a.CreatedAt))
                .FirstOrDefaultAsync();
            if (Answering is null) return NotFound();
        }
        if (share is int id)
        {
            var me = User.GetUserId();
            Sharing = (await posts.ProjectAsync(posts.Visible(me, User.IsStaff()).Where(p => p.Id == id && p.ParentId == null), me)).FirstOrDefault();
            if (Sharing is null) return NotFound();
        }
        return Page();
    }
}
