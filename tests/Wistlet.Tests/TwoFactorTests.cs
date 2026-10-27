using System.Text;
using Wistlet.Models;
using Wistlet.Services;

namespace Wistlet.Tests;

public class TwoFactorTests
{
    // RFC 6238 appendix B (SHA-1 key "12345678901234567890"), last six digits of each value.
    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void MatchesRfc6238TestVectors(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, TwoFactorService.Compute(key, unixTime / 30));
    }

    [Fact]
    public void Base32RoundTrips()
    {
        for (var length = 0; length < 40; length++)
        {
            var data = Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();
            Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        }
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Equal("foobar"u8.ToArray(), Base32.Decode("mzxw 6ytb oi======"));
    }

    [Fact]
    public void AcceptsNeighbouringStepsButNotReplays()
    {
        var key = TwoFactorService.GenerateKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = TwoFactorService.CurrentStep(now);
        var bytes = Base32.Decode(key);

        Assert.Equal(step, TwoFactorService.VerifyCode(key, TwoFactorService.Compute(bytes, step), 0, now));
        Assert.Equal(step - 1, TwoFactorService.VerifyCode(key, TwoFactorService.Compute(bytes, step - 1), 0, now));
        Assert.Equal(step, TwoFactorService.VerifyCode(key, TwoFactorService.Compute(bytes, step)[..3] + " " + TwoFactorService.Compute(bytes, step)[3..], 0, now));
        Assert.Null(TwoFactorService.VerifyCode(key, TwoFactorService.Compute(bytes, step - 2), 0, now));
        // A code that was already used (same or earlier step) is refused.
        Assert.Null(TwoFactorService.VerifyCode(key, TwoFactorService.Compute(bytes, step), step, now));
        Assert.Null(TwoFactorService.VerifyCode(key, "12345", 0, now));
    }

    [Fact]
    public void RecoveryCodesWorkOnce()
    {
        var user = new User();
        var codes = TwoFactorService.ResetRecoveryCodes(user);
        Assert.Equal(TwoFactorService.RecoveryCodeCount, codes.Distinct().Count());
        Assert.DoesNotContain(codes[0], user.RecoveryCodes);

        Assert.True(TwoFactorService.RedeemRecoveryCode(user, " " + codes[0].ToUpperInvariant() + " "));
        Assert.False(TwoFactorService.RedeemRecoveryCode(user, codes[0]));
        Assert.True(TwoFactorService.RedeemRecoveryCode(user, codes[1].Replace("-", "")));
        Assert.Equal(TwoFactorService.RecoveryCodeCount - 2, TwoFactorService.RemainingRecoveryCodes(user));
        Assert.False(TwoFactorService.RedeemRecoveryCode(user, "nope-nope"));
    }

    [Fact]
    public void AuthenticatorUriEscapesNames()
    {
        var uri = TwoFactorService.AuthenticatorUri("My Site", "annie", "ABC");
        Assert.Equal("otpauth://totp/My%20Site:annie?secret=ABC&issuer=My%20Site&digits=6&period=30", uri);
    }
}
