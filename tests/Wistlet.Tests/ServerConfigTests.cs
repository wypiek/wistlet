using Microsoft.Extensions.Configuration;
using Wistlet.Services;

namespace Wistlet.Tests;

public class ServerConfigTests
{
    private static ServerConfig Config(params (string Key, string? Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, v.Value))).Build());

    [Theory]
    [InlineData("https://wistlet.example.com", "https://wistlet.example.com", "wistlet.example.com")]
    [InlineData("https://wistlet.example.com/", "https://wistlet.example.com", "wistlet.example.com")]
    [InlineData("  https://Wistlet.Example.com  ", "https://wistlet.example.com", "wistlet.example.com")]
    [InlineData("\"https://wistlet.example.com\"", "https://wistlet.example.com", "wistlet.example.com")]
    [InlineData("wistlet.example.com", "https://wistlet.example.com", "wistlet.example.com")]
    [InlineData("https://wistlet.example.com:8443", "https://wistlet.example.com:8443", "wistlet.example.com:8443")]
    [InlineData("https://wistlet.example.com/some/path", "https://wistlet.example.com", "wistlet.example.com")]
    public void AcceptsUsableAddresses(string raw, string origin, string domain)
    {
        var r = ServerConfig.ParsePublicUrl(raw, allowHttp: false);
        Assert.Null(r.Problem);
        Assert.Equal(origin, r.Origin);
        Assert.Equal(domain, r.Domain);
    }

    [Fact]
    public void ExplainsWhatItAssumed()
    {
        Assert.Contains("https is assumed", ServerConfig.ParsePublicUrl("wistlet.example.com", false).Note);
        Assert.Contains("ignored", ServerConfig.ParsePublicUrl("https://wistlet.example.com/app", false).Note);
        Assert.Null(ServerConfig.ParsePublicUrl("https://wistlet.example.com", false).Note);
    }

    [Theory]
    [InlineData("http://wistlet.example.com", "must be https://")]
    [InlineData("ftp://wistlet.example.com", "isn't an http(s) address")]
    [InlineData("https://user:pw@wistlet.example.com", "without a user name")]
    [InlineData("https://wistlet.example.com/?x=1", "without a user name")]
    [InlineData("https://wistlet_example.com", "isn't a usable host name")]
    [InlineData("https://", "isn't a web address")]
    public void ExplainsUnusableAddresses(string raw, string reason)
    {
        var r = ServerConfig.ParsePublicUrl(raw, allowHttp: false);
        Assert.Null(r.Origin);
        Assert.Contains(reason, r.Problem);
    }

    [Fact]
    public void AllowsHttpOnlyForTesting()
    {
        Assert.Equal("http://localhost:6969", ServerConfig.ParsePublicUrl("http://localhost:6969", allowHttp: true).Origin);
        Assert.Null(ServerConfig.ParsePublicUrl("", false).Origin);
        Assert.Null(ServerConfig.ParsePublicUrl("", false).Problem);
    }

    [Fact]
    public void ReportsEachSetting()
    {
        var empty = Config();
        Assert.All(empty.Checks, c => Assert.Equal(ServerConfig.Level.NotSet, c.Level));
        Assert.Null(empty.PublicOrigin);
        Assert.False(empty.SmtpConfigured);

        var good = Config(("Wistlet:PublicUrl", "wistlet.example.com"), ("Wistlet:Smtp:Host", "smtp.example.com"),
            ("Wistlet:Smtp:From", "Wistlet <hi@example.com>"), ("Wistlet:Smtp:Port", "465"), ("Wistlet:Smtp:Security", "sslonconnect"));
        Assert.Equal("https://wistlet.example.com", good.PublicOrigin);
        Assert.True(good.SmtpConfigured);
        Assert.Equal(465, good.SmtpPort);
        Assert.Equal(MailKit.Security.SecureSocketOptions.SslOnConnect, good.SmtpSecurity);
        Assert.Empty(good.Problems);
    }

    [Fact]
    public void FlagsBrokenValues()
    {
        var c = Config(("Wistlet:PublicUrl", "http://wistlet.example.com"), ("Wistlet:Smtp:Host", "smtp.example.com"),
            ("Wistlet:Smtp:Port", "five-eight-seven"), ("Wistlet:Smtp:From", "hi@example.com"));
        Assert.Equal(["WISTLET_PUBLIC_URL", "WISTLET_SMTP_HOST"], c.Problems.Select(p => p.Variable));
        Assert.Null(c.PublicOrigin);
        Assert.Equal(587, c.SmtpPort); // a bad port falls back instead of stopping the site
        Assert.True(c.SmtpConfigured);

        var noFrom = Config(("Wistlet:Smtp:Host", "smtp.example.com"));
        Assert.False(noFrom.SmtpConfigured);
        Assert.Contains("WISTLET_SMTP_FROM is empty", noFrom.Problems.Single().Message);
    }
}
