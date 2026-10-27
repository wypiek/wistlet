using System.Buffers.Binary;
using Wistlet.Services;

namespace Wistlet.Tests;

public class AnimatedImageTests
{
    // Two-frame 6×4 images and single-frame ones, made with Pillow.
    private static readonly byte[] AnimatedGif = Convert.FromBase64String("R0lGODlhBgAEAIEAAIMlTwAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAABgAEAAAICwABCBxIsKDBgQEBACH5BAEKAAEALAAAAAAGAAQAgf+rXAAAAAAAAAAAAAgLAAEIHEiwoMGBAQEAOw==");
    private static readonly byte[] StillGif = Convert.FromBase64String("R0lGODdhBgAEAIEAAIMlTwAAAAAAAAAAACwAAAAABgAEAAAICwABCBxIsKDBgQEBADs=");
    private static readonly byte[] AnimatedWebP = Convert.FromBase64String("UklGRroAAABXRUJQVlA4WAoAAAACAAAABQAAAwAAQU5JTQYAAAAAAAAAAABBTk1GPgAAAAAAAAAAAAUAAAMAAGQAAAJWUDggJgAAADABAJ0BKgYABAABQCYloAADcAD+9SdfbJ3pf//5mj/0m/7DeGgAQU5NRkgAAAAAAAAAAAAFAAADAABkAAAAVlA4IDAAAAA0AQCdASoGAAQAAAAmJaAAA3AA/vP8N//6aR//TSP/6aR8xb//56Z/7A/+SPhYAAA=");
    private static readonly byte[] AnimatedLosslessWebP = Convert.FromBase64String("UklGRogAAABXRUJQVlA4WAoAAAASAAAABQAAAwAAQU5JTQYAAAAAAAAAAABBTk1GKgAAAAAAAAAAAAUAAAMAAGQAAAJWUDhMEQAAAC8FwAAAB9CSDvap/4GI6H8AAEFOTUYqAAAAAAAAAAAABQAAAwAAZAAAAlZQOEwRAAAALwXAABAH0NX+l6uAgYjofwAA");
    private static readonly byte[] StillWebP = Convert.FromBase64String("UklGRjQAAABXRUJQVlA4ICgAAABwAQCdASoGAAQAAUAmJaACdAFAAAD+9Sc1FFH//zNH/pN/2G8NAAAA");

    [Fact]
    public void CutsTheFirstFrameOutOfAGif()
    {
        var still = AnimatedImage.FirstFrame(AnimatedGif);
        Assert.NotNull(still);
        Assert.True(still.Length < AnimatedGif.Length);
        Assert.Equal("GIF89a"u8.ToArray(), still[..6]);
        Assert.Equal(0x3B, still[^1]);
        // The still is a well-formed GIF with a single frame.
        Assert.Null(AnimatedImage.FirstFrame(still));
        Assert.Equal(AnimatedGif[..(still.Length - 1)], still[..^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CutsTheFirstFrameOutOfAWebP(bool lossless)
    {
        var source = lossless ? AnimatedLosslessWebP : AnimatedWebP;
        var still = AnimatedImage.FirstFrame(source);
        Assert.NotNull(still);
        Assert.Equal("RIFF"u8.ToArray(), still[..4]);
        Assert.Equal(still.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(still.AsSpan(4)));
        Assert.Equal("VP8X"u8.ToArray(), still[12..16]);
        Assert.Equal(0, still[20] & 0x02); // no animation flag
        Assert.Equal(lossless ? 0x10 : 0, still[20] & 0x10); // alpha flag for the lossless (VP8L) frame
        Assert.Equal(5, still[24] | still[25] << 8 | still[26] << 16); // width − 1
        Assert.Equal(3, still[27] | still[28] << 8 | still[29] << 16); // height − 1
        Assert.Equal(lossless ? "VP8L"u8.ToArray() : "VP8 "u8.ToArray(), still[30..34]);
        Assert.Equal(-1, still.AsSpan().IndexOf("ANMF"u8));
    }

    [Fact]
    public void SkipsWebPsWhoseFirstFrameDoesNotFillTheCanvas()
    {
        // Move the first frame one step right (X is stored in units of two pixels).
        var shifted = (byte[])AnimatedWebP.Clone();
        var anmf = shifted.AsSpan().IndexOf("ANMF"u8);
        shifted[anmf + 8] = 1;
        Assert.Null(AnimatedImage.FirstFrame(shifted));
    }

    [Fact]
    public void LeavesStillImagesAlone()
    {
        Assert.Null(AnimatedImage.FirstFrame(StillGif));
        Assert.Null(AnimatedImage.FirstFrame(StillWebP));
        Assert.Null(AnimatedImage.FirstFrame("\x89PNG\r\n\x1a\n"u8));
    }

    [Fact]
    public void SurvivesBrokenFiles()
    {
        for (var cut = 0; cut < AnimatedGif.Length - 1; cut++) AnimatedImage.FirstFrame(AnimatedGif[..cut]);
        for (var cut = 0; cut < AnimatedWebP.Length - 1; cut++) AnimatedImage.FirstFrame(AnimatedWebP[..cut]);
        var lying = (byte[])AnimatedWebP.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(4), uint.MaxValue);
        Assert.NotNull(AnimatedImage.FirstFrame(lying)); // the RIFF size is clamped to the data
        Assert.Null(AnimatedImage.FirstFrame("GIF89a"u8));
    }

    [Theory]
    [InlineData("/uploads/avatars/0123456789abcdef0123456789abcdef.anim.gif", "/uploads/avatars/0123456789abcdef0123456789abcdef.still.gif")]
    [InlineData("/uploads/banners/0123456789abcdef0123456789abcdef.anim.webp", "/uploads/banners/0123456789abcdef0123456789abcdef.still.webp")]
    [InlineData("/uploads/avatars/0123456789abcdef0123456789abcdef.gif", null)]
    [InlineData("https://other.example/uploads/avatars/0123456789abcdef0123456789abcdef.anim.gif", null)]
    [InlineData("/avatar/annie.svg", null)]
    [InlineData(null, null)]
    public void FindsTheStillNextToAnAnimation(string? url, string? still) => Assert.Equal(still, UploadService.StillFor(url));
}
