using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Infrastructure;
using Wistlet.Models;

namespace Wistlet.Pages;

[AllowGuests]
public class AboutModel(AppDbContext db) : PageModel
{
    public int Users { get; private set; }
    public int Posts { get; private set; }
    public List<UserSummary> Staff { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Users = await db.Users.CountAsync();
        Posts = await db.Posts.CountAsync(p => !p.IsHidden);
        Staff = await db.Users.AsNoTracking()
            .Where(u => u.Role != UserRole.User)
            .OrderByDescending(u => u.Role).ThenBy(u => u.UserName)
            .Select(u => new UserSummary(u.Id, u.UserName, u.DisplayName, u.AvatarPath, u.IsVerified, u.Role, u.Role == UserRole.Admin ? "Administrator" : "Moderator"))
            .ToListAsync();
    }
}
