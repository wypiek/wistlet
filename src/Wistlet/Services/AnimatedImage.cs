using System.Buffers.Binary;

namespace Wistlet.Services;

/// <summary>
/// Finds out whether a GIF or WebP is animated and cuts out its first frame as a still image of
/// the same format, by rearranging the file's blocks: nothing is decoded, so there's no image
/// library involved and no decompression to abuse.
/// </summary>
public static class AnimatedImage
{
    /// <summary>The first frame of an animated GIF or WebP, or null if the image isn't animated (or isn't readable).</summary>
    public static byte[]? FirstFrame(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return GifFirstFrame(data);
            if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return WebPFirstFrame(data);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated or malformed: treat it as not animated.
        }
        return null;
    }

    /// <summary>
    /// GIF: header, screen descriptor and colour table, then extension and image blocks made of
    /// length-prefixed sub-blocks. The still is everything up to the end of the first image, then
    /// the trailer.
    /// </summary>
    private static byte[]? GifFirstFrame(ReadOnlySpan<byte> data)
    {
        var pos = 13;
        var screenFlags = data[10];
        if ((screenFlags & 0x80) != 0) pos += 3 << ((screenFlags & 7) + 1);

        var frames = 0;
        var firstFrameEnd = -1;
        while (pos < data.Length)
        {
            switch (data[pos])
            {
                case 0x21: // extension: label, then sub-blocks
                    pos = SkipSubBlocks(data, pos + 2);
                    break;
                case 0x2C: // image: descriptor, optional local colour table, LZW code size, sub-blocks
                    var imageFlags = data[pos + 9];
                    pos += 10;
                    if ((imageFlags & 0x80) != 0) pos += 3 << ((imageFlags & 7) + 1);
                    pos = SkipSubBlocks(data, pos + 1);
                    if (++frames == 1) firstFrameEnd = pos;
                    if (frames > 1)
                    {
                        var still = new byte[firstFrameEnd + 1];
                        data[..firstFrameEnd].CopyTo(still);
                        still[^1] = 0x3B;
                        return still;
                    }
                    break;
                case 0x3B: // trailer
                    return null;
                default:
                    return null;
            }
        }
        return null;
    }

    private static int SkipSubBlocks(ReadOnlySpan<byte> data, int pos)
    {
        while (true)
        {
            var size = data[pos];
            pos += 1 + size;
            if (size == 0) return pos;
        }
    }

    /// <summary>
    /// WebP: a RIFF container of chunks. Animated files have a VP8X header with the animation flag,
    /// an ANIM chunk and one ANMF chunk per frame, whose payload is a 16-byte frame header followed
    /// by the frame's own chunks (ALPH, VP8 or VP8L). The still is a VP8X header sized to the first
    /// frame, followed by that frame's chunks.
    /// </summary>
    private static byte[]? WebPFirstFrame(ReadOnlySpan<byte> data)
    {
        var riffEnd = Math.Min(data.Length, 8 + (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]), int.MaxValue - 8));
        var pos = 12;
        var frames = 0;
        int canvasWidth = 0, canvasHeight = 0;
        ReadOnlySpan<byte> firstFrame = default;
        while (pos + 8 <= riffEnd)
        {
            var fourcc = data.Slice(pos, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (size < 0 || pos + 8 + size > riffEnd) return null;
            if (fourcc.SequenceEqual("VP8X"u8) && size >= 10)
            {
                canvasWidth = ReadUInt24(data[(pos + 12)..]) + 1;
                canvasHeight = ReadUInt24(data[(pos + 15)..]) + 1;
            }
            if (fourcc.SequenceEqual("ANMF"u8) && ++frames == 1) firstFrame = data.Slice(pos + 8, size);
            pos += 8 + size + (size & 1);
        }
        if (frames < 2 || firstFrame.Length < 16 + 8) return null;

        // Frame header: X, Y, width − 1, height − 1, duration (24 bits each), then flags.
        // A still WebP is exactly its frame, so a first frame that doesn't cover the whole canvas
        // would come out cropped and shifted: such images just keep playing.
        var width = ReadUInt24(firstFrame[6..]) + 1;
        var height = ReadUInt24(firstFrame[9..]) + 1;
        if (ReadUInt24(firstFrame) != 0 || ReadUInt24(firstFrame[3..]) != 0 || width != canvasWidth || height != canvasHeight)
            return null;
        var frameChunks = firstFrame[16..];
        var hasAlpha = false;
        for (var p = 0; p + 8 <= frameChunks.Length;)
        {
            var id = frameChunks.Slice(p, 4);
            hasAlpha |= id.SequenceEqual("ALPH"u8) || id.SequenceEqual("VP8L"u8);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(frameChunks[(p + 4)..]);
            if (size < 0) return null;
            p += 8 + size + (size & 1);
        }

        var still = new byte[12 + 18 + frameChunks.Length];
        "RIFF"u8.CopyTo(still);
        BinaryPrimitives.WriteUInt32LittleEndian(still.AsSpan(4), (uint)(still.Length - 8));
        "WEBP"u8.CopyTo(still.AsSpan(8));
        "VP8X"u8.CopyTo(still.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(still.AsSpan(16), 10);
        still[20] = (byte)(hasAlpha ? 0x10 : 0); // flags: alpha, no animation
        WriteUInt24(still.AsSpan(24), width - 1);
        WriteUInt24(still.AsSpan(27), height - 1);
        frameChunks.CopyTo(still.AsSpan(30));
        return still;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16);

    private static void WriteUInt24(Span<byte> b, int value)
    {
        b[0] = (byte)value;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)(value >> 16);
    }
}
