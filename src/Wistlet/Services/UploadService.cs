namespace Wistlet.Services;

/// <summary>
/// Stores user-uploaded images on disk under the data directory. Files are
/// identified by their magic bytes (never by the client-supplied name or type)
/// and saved under random names.
/// </summary>
public class UploadService(IConfiguration config, SiteSettingsService settings, ILogger<UploadService> logger)
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(config["Wistlet:DataPath"] ?? "data", "uploads"));

    public const string UrlPrefix = "/uploads";

    private static readonly (string Ext, byte[] Magic)[] Signatures =
    [
        (".png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        (".jpg", [0xFF, 0xD8, 0xFF]),
        (".gif", "GIF87a"u8.ToArray()),
        (".gif", "GIF89a"u8.ToArray()),
    ];

    public async Task<(string? Url, string? Error)> SaveImageAsync(IFormFile? file, string folder, int? maxMbOverride = null)
    {
        if (file is null || file.Length == 0) return (null, null);

        var maxBytes = (long)(maxMbOverride ?? settings.Current.MaxUploadMb) * 1024 * 1024;
        if (file.Length > maxBytes)
            return (null, $"Images must be smaller than {maxBytes / 1024 / 1024} MB.");

        var header = new byte[16];
        await using (var peek = file.OpenReadStream())
        {
            var read = await peek.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            Array.Resize(ref header, read);
        }

        var ext = DetectExtension(header);
        if (ext is null)
            return (null, "Only PNG, JPEG, GIF and WebP images are supported.");

        var dir = Path.Combine(Root, folder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(dir, name);
        await using (var target = File.Create(path))
        await using (var source = file.OpenReadStream())
        {
            await source.CopyToAsync(target);
        }
        return ($"{UrlPrefix}/{folder}/{name}", null);
    }

    private static string? DetectExtension(byte[] header)
    {
        foreach (var (ext, magic) in Signatures)
            if (header.AsSpan().StartsWith(magic)) return ext;
        // WebP: "RIFF" ???? "WEBP"
        if (header.Length >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }

    public void Delete(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UrlPrefix + "/")) return;
        var full = Path.GetFullPath(Path.Combine(Root, url[(UrlPrefix.Length + 1)..]));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar)) return;
        try
        {
            if (File.Exists(full)) File.Delete(full);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete upload {Path}", full);
        }
    }
}
