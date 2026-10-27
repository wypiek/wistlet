using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;
using Wistlet.Models;

namespace Wistlet.Services;

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 6 digits, 30-second steps), the format
/// authenticator apps use, plus single-use recovery codes. The shared key is stored encrypted.
/// </summary>
public class TwoFactorService(IDataProtectionProvider protection)
{
    public const int RecoveryCodeCount = 10;
    private const int Digits = 6;
    private const int StepSeconds = 30;

    private readonly IDataProtector _protector = protection.CreateProtector("Wistlet.TwoFactor.v1");

    /// <summary>A new random 160-bit key, Base32-encoded as authenticator apps expect.</summary>
    public static string GenerateKey() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public string Protect(string key) => _protector.Protect(key);

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return _protector.Unprotect(stored); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>The otpauth:// URI that authenticator apps read from the QR code.</summary>
    public static string AuthenticatorUri(string issuer, string account, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    /// <summary>The URI as a QR code, as a PNG data URL.</summary>
    public static string QrCodeDataUrl(string uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public static string Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>
    /// Checks a code against the current step and one step either side (clock drift). Steps at or
    /// before <paramref name="lastUsedStep"/> are refused so an observed code can't be replayed.
    /// </summary>
    public static long? VerifyCode(string base32Key, string? code, long lastUsedStep, DateTimeOffset now)
    {
        code = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (code.Length != Digits) return null;
        byte[] key;
        try { key = Base32.Decode(base32Key); }
        catch (FormatException) { return null; }

        var current = CurrentStep(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    /// <summary>Checks a code from the user's authenticator app and records it as used.</summary>
    public bool VerifyAndConsume(User user, string? code)
    {
        if (Unprotect(user.TwoFactorSecret) is not { } key) return false;
        if (VerifyCode(key, code, user.TwoFactorLastStep, DateTimeOffset.UtcNow) is not long step) return false;
        user.TwoFactorLastStep = step;
        return true;
    }

    /// <summary>Creates new recovery codes, stores their hashes on the user and returns the codes to show once.</summary>
    public static List<string> ResetRecoveryCodes(User user)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ =>
        {
            var raw = string.Create(10, alphabet, (span, a) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
            });
            return $"{raw[..5]}-{raw[5..]}";
        }).ToList();
        user.RecoveryCodes = string.Join(' ', codes.Select(HashRecoveryCode));
        return codes;
    }

    public static int RemainingRecoveryCodes(User user) =>
        (user.RecoveryCodes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Uses up a recovery code if it matches one of the user's unused codes.</summary>
    public static bool RedeemRecoveryCode(User user, string? code)
    {
        var hash = HashRecoveryCode(code ?? "");
        var hashes = (user.RecoveryCodes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!hashes.Remove(hash)) return false;
        user.RecoveryCodes = string.Join(' ', hashes);
        return true;
    }

    private static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static void Disable(User user)
    {
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.RecoveryCodes = null;
        user.TwoFactorLastStep = 0;
    }
}

/// <summary>RFC 4648 Base32 without padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid Base32 character.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        return bytes.ToArray();
    }
}
