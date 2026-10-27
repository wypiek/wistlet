namespace Wistlet.Services;

/// <summary>
/// Stores user-uploaded images and audio on disk under the data directory. Files are
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

    /// <summary>Content types for the files uploads can have (the default table lacks some audio types).</summary>
    public static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
    };

    public async Task<(string? Url, string? Error)> SaveImageAsync(IFormFile? file, string folder, int? maxMbOverride = null)
    {
        var maxMb = maxMbOverride ?? settings.Current.MaxUploadMb;
        return await SaveAsync(file, folder, maxMb, DetectImageExtension,
            $"Images must be smaller than {maxMb} MB.", "Only PNG, JPEG, GIF and WebP images are supported.");
    }

    public async Task<(string? Url, string? Error)> SaveAudioAsync(IFormFile? file, string folder)
    {
        var maxMb = settings.Current.MaxAudioUploadMb;
        return await SaveAsync(file, folder, maxMb, DetectAudioExtension,
            $"Audio files must be smaller than {maxMb} MB.", "Only MP3, Ogg, FLAC, WAV and M4A audio files are supported.");
    }

    private async Task<(string? Url, string? Error)> SaveAsync(IFormFile? file, string folder, int maxMb,
        Func<byte[], string?> detect, string tooLarge, string wrongType)
    {
        if (file is null || file.Length == 0) return (null, null);
        if (file.Length > (long)maxMb * 1024 * 1024) return (null, tooLarge);

        var header = new byte[16];
        await using (var peek = file.OpenReadStream())
        {
            var read = await peek.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            Array.Resize(ref header, read);
        }

        var ext = detect(header);
        if (ext is null) return (null, wrongType);

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

    private static string? DetectImageExtension(byte[] header)
    {
        foreach (var (ext, magic) in Signatures)
            if (header.AsSpan().StartsWith(magic)) return ext;
        // WebP: "RIFF" ???? "WEBP"
        if (IsRiff(header, "WEBP"u8)) return ".webp";
        return null;
    }

    public static string? DetectAudioExtension(byte[] header)
    {
        var h = header.AsSpan();
        if (h.StartsWith("ID3"u8)) return ".mp3";
        // An MPEG audio frame starts with 11 set sync bits; layer bits 00 are reserved.
        if (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0 && (h[1] & 0x06) != 0) return ".mp3";
        if (h.StartsWith("OggS"u8)) return ".ogg";
        if (h.StartsWith("fLaC"u8)) return ".flac";
        if (IsRiff(header, "WAVE"u8)) return ".wav";
        // MPEG-4 audio: "ftyp" box with an audio-capable brand.
        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = h.Slice(8, 4);
            if (brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) || brand.SequenceEqual("mp42"u8) ||
                brand.SequenceEqual("isom"u8) || brand.SequenceEqual("dash"u8))
                return ".m4a";
        }
        return null;
    }

    private static bool IsRiff(byte[] header, ReadOnlySpan<byte> format) =>
        header.Length >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual(format);

    /// <summary>The file on disk behind an upload URL, or null if the URL isn't one of ours.</summary>
    public string? PathFor(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UrlPrefix + "/")) return null;
        var full = Path.GetFullPath(Path.Combine(Root, url[(UrlPrefix.Length + 1)..]));
        return full.StartsWith(Root + Path.DirectorySeparatorChar) ? full : null;
    }

    public void Delete(string? url)
    {
        if (PathFor(url) is not { } full) return;
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
