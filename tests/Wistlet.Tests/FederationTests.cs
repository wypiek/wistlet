using System.Net;
using System.Security.Cryptography;
using System.Text;
using Wistlet.Services.Federation;

namespace Wistlet.Tests;

public class FederationProtocolTests
{
    [Theory]
    [InlineData("@annie@other.example", "annie", "other.example")]
    [InlineData("annie@Other.Example", "annie", "other.example")]
    [InlineData("  @sol_2@wistlet.example:8443 ", "sol_2", "wistlet.example:8443")]
    [InlineData("@pete@localhost:6970", "pete", "localhost:6970")]
    public void ParsesHandles(string handle, string name, string domain)
    {
        Assert.True(FederationProtocol.TryParseHandle(handle, out var n, out var d));
        Assert.Equal(name, n);
        Assert.Equal(domain, d);
    }

    [Theory]
    [InlineData("annie")]
    [InlineData("@annie")]
    [InlineData("@annie@")]
    [InlineData("@a@other.example")]
    [InlineData("@annie@other..example")]
    [InlineData("@annie@-bad.example")]
    [InlineData("@annie@other.example/path")]
    [InlineData("@annie@user:pw@other.example")]
    [InlineData("@an nie@other.example")]
    public void RejectsBadHandles(string handle) => Assert.False(FederationProtocol.TryParseHandle(handle, out _, out _));

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void SignedRequestsVerify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var body = Encoding.UTF8.GetBytes("""{"type":"like","actor":"annie","object":"https://b.example/post/3"}""");
        var h = FederationProtocol.Sign(key, "POST", "/federation/inbox", "b.example", "a.example", body, Now);

        Assert.Equal("a.example", h[FederationProtocol.InstanceHeader]);
        Assert.Null(FederationProtocol.CheckDateAndDigest(h[FederationProtocol.DateHeader], h[FederationProtocol.DigestHeader], body, Now.AddSeconds(30)));

        var signed = FederationProtocol.SigningString("POST", "/federation/inbox", "b.example", "a.example", h[FederationProtocol.DateHeader], h[FederationProtocol.DigestHeader]);
        Assert.True(FederationProtocol.VerifySignature(publicKey, signed, h[FederationProtocol.SignatureHeader]));

        // Sent to another instance, or claiming another sender: the signature no longer matches.
        var elsewhere = FederationProtocol.SigningString("POST", "/federation/inbox", "c.example", "a.example", h[FederationProtocol.DateHeader], h[FederationProtocol.DigestHeader]);
        Assert.False(FederationProtocol.VerifySignature(publicKey, elsewhere, h[FederationProtocol.SignatureHeader]));
        var impostor = FederationProtocol.SigningString("POST", "/federation/inbox", "b.example", "evil.example", h[FederationProtocol.DateHeader], h[FederationProtocol.DigestHeader]);
        Assert.False(FederationProtocol.VerifySignature(publicKey, impostor, h[FederationProtocol.SignatureHeader]));

        // Another key doesn't verify, and garbage doesn't crash.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(FederationProtocol.VerifySignature(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), signed, h[FederationProtocol.SignatureHeader]));
        Assert.False(FederationProtocol.VerifySignature(publicKey, signed, "not base64!"));
        Assert.False(FederationProtocol.VerifySignature("nope", signed, h[FederationProtocol.SignatureHeader]));
        Assert.False(FederationProtocol.VerifySignature(publicKey, signed, null));
    }

    [Fact]
    public void RejectsChangedBodiesAndOldDates()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = "{\"type\":\"follow\"}"u8.ToArray();
        var h = FederationProtocol.Sign(key, "POST", "/federation/inbox", "b.example", "a.example", body, Now);
        var date = h[FederationProtocol.DateHeader];
        var digest = h[FederationProtocol.DigestHeader];

        Assert.NotNull(FederationProtocol.CheckDateAndDigest(date, digest, "{\"type\":\"unfollow\"}"u8, Now));
        Assert.NotNull(FederationProtocol.CheckDateAndDigest(date, digest, body, Now.AddMinutes(6)));
        Assert.NotNull(FederationProtocol.CheckDateAndDigest(date, digest, body, Now.AddMinutes(-6)));
        Assert.NotNull(FederationProtocol.CheckDateAndDigest("yesterday", digest, body, Now));
        Assert.NotNull(FederationProtocol.CheckDateAndDigest(date, null, body, Now));
    }

    [Fact]
    public void ChecksUrlsBelongToTheInstance()
    {
        Assert.True(FederationProtocol.IsUrlOn("https://b.example/uploads/posts/x.png", "b.example", "/uploads/"));
        Assert.True(FederationProtocol.IsUrlOn("http://localhost:6970/post/3", "localhost:6970", "/post/"));
        Assert.False(FederationProtocol.IsUrlOn("https://evil.example/uploads/x.png", "b.example", "/uploads/"));
        Assert.False(FederationProtocol.IsUrlOn("https://b.example.evil.example/uploads/x.png", "b.example"));
        Assert.False(FederationProtocol.IsUrlOn("https://b.example/elsewhere/x.png", "b.example", "/uploads/"));
        Assert.False(FederationProtocol.IsUrlOn("https://user@b.example/uploads/x.png", "b.example"));
        Assert.False(FederationProtocol.IsUrlOn("javascript:alert(1)", "b.example"));
        Assert.False(FederationProtocol.IsUrlOn("/uploads/x.png", "b.example"));
        Assert.False(FederationProtocol.IsUrlOn(null, "b.example"));

        Assert.Equal(12, FederationProtocol.PostIdFromUrl("https://a.example/post/12", "a.example"));
        Assert.Null(FederationProtocol.PostIdFromUrl("https://a.example/post/12/edit", "a.example"));
        Assert.Null(FederationProtocol.PostIdFromUrl("https://b.example/post/12", "a.example"));
        Assert.Null(FederationProtocol.PostIdFromUrl("https://a.example/post/-1", "a.example"));
    }

    [Fact]
    public void PointsRelativeLinksAtTheOrigin()
    {
        var html = "<a href=\"/u/annie\" class=\"co-mention\">@annie</a> <img src=\"/uploads/a.png\"> <a href=\"https://x.example/\">x</a> <a href=\"//cdn.example/y\">y</a>";
        var result = FederationProtocol.Absolutize(html, "https://b.example");
        Assert.Contains("href=\"https://b.example/u/annie\"", result);
        Assert.Contains("src=\"https://b.example/uploads/a.png\"", result);
        Assert.Contains("href=\"https://x.example/\"", result);
        Assert.Contains("href=\"//cdn.example/y\"", result);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:4700::1111", false)]
    public void KnowsWhichAddressesArePrivate(string address, bool nonPublic) =>
        Assert.Equal(nonPublic, FederationContext.IsNonPublic(IPAddress.Parse(address)));

    [Fact]
    public void BacksOffBetweenDeliveries()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), FederationDeliveryWorker.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(1), FederationDeliveryWorker.Backoff(2));
        Assert.Equal(TimeSpan.FromHours(6), FederationDeliveryWorker.Backoff(20));
    }
}
