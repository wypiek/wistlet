using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Pages;

/// <summary>Unanswered asks, newest first.</summary>
[Authorize]
public class InboxModel(AppDbContext db) : PageModel
{
    public List<AskView> Asks { get; private set; } = [];
    public User Me { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var me = User.RequireUserId();
        Me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me);
        Asks = await db.Asks.AsNoTracking()
            .Where(a => a.RecipientId == me && !a.IsAnswered)
            .OrderByDescending(a => a.Id)
            .Take(200)
            .Select(a => new AskView(a.Id, a.Content,
                a.IsAnonymous || a.Sender == null ? null
                    : new UserSummary(a.Sender.Id, a.Sender.UserName, a.Sender.DisplayName, a.Sender.AvatarPath, a.Sender.IsVerified, a.Sender.Role, "", a.Sender.AvatarShape),
                a.CreatedAt))
            .ToListAsync();
    }
}
