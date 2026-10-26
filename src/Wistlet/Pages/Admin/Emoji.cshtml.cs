using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Wistlet.Data;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Pages.Admin;

/// <summary>The site's custom emoji: upload an image under a shortcode, and posts can use it as :shortcode:.</summary>
public class EmojiModel(AppDbContext db, UploadService uploads, EmojiService emoji, PostService posts, AuditService audit) : PageModel
{
    public List<CustomEmoji> Items { get; private set; } = [];

    public async Task OnGetAsync() =>
        Items = await db.CustomEmoji.AsNoTracking().OrderBy(e => e.Shortcode).ToListAsync();

    public async Task<IActionResult> OnPostAddAsync(string? shortcode, IFormFile? image)
    {
        shortcode = (shortcode ?? "").Trim().Trim(':').ToLowerInvariant();
        if (!PostRenderer.IsValidEmojiShortcode(shortcode))
            return Fail("Shortcodes are 1–32 characters: lower-case letters, digits, - _ and +.");
        if (await db.CustomEmoji.AnyAsync(e => e.Shortcode == shortcode))
            return Fail($":{shortcode}: already exists. Delete it first to replace it.");
        if (image is null || image.Length == 0)
            return Fail("Choose an image for the emoji.");

        var (url, error) = await uploads.SaveImageAsync(image, "emoji", maxMbOverride: 1);
        if (error is not null) return Fail(error);

        db.CustomEmoji.Add(new CustomEmoji { Shortcode = shortcode, Path = url! });
        audit.Log("emoji.add", $":{shortcode}:");
        await db.SaveChangesAsync();
        await emoji.ReloadAsync();
        await posts.RerenderUsingEmojiAsync(shortcode);
        TempData["Flash"] = $"Added :{shortcode}:.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var item = await db.CustomEmoji.FindAsync(id);
        if (item is null) return NotFound();
        db.CustomEmoji.Remove(item);
        audit.Log("emoji.delete", $":{item.Shortcode}:");
        await db.SaveChangesAsync();
        uploads.Delete(item.Path);
        await emoji.ReloadAsync();
        await posts.RerenderUsingEmojiAsync(item.Shortcode);
        TempData["Flash"] = $"Deleted :{item.Shortcode}:. Posts using it now show the shortcode as text.";
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        TempData["Error"] = message;
        return RedirectToPage();
    }
}
