using System.Globalization;
using MailKit.Security;
using Wistlet.Services.Federation;

namespace Wistlet.Services;

/// <summary>
/// Reads and checks the settings that come from the environment (<c>.env</c> in Docker), once at
/// start-up. Each one gets a plain-language verdict, which is written to the log and shown on the
/// admin dashboard, so a value that was ignored never goes unnoticed.
/// </summary>
public sealed class ServerConfig
{
    public enum Level { Ok, NotSet, Problem }

    /// <summary>One setting: its <c>.env</c> name, what was found, and what that means.</summary>
    public sealed record Check(string Variable, Level Level, string Value, string Message);

    private readonly List<Check> _checks = [];

    public ServerConfig(IConfiguration config)
    {
        AllowHttp = config.GetValue("Wistlet:Federation:AllowHttp", false);

        // ---- public address ----
        var raw = config["Wistlet:PublicUrl"];
        var url = ParsePublicUrl(raw, AllowHttp);
        PublicOrigin = url.Origin;
        PublicDomain = url.Domain;
        _checks.Add(url.Origin is not null
            ? new("WISTLET_PUBLIC_URL", Level.Ok, url.Origin, url.Note ?? $"This site is {url.Domain}; e-mail links and federation use this address.")
            : string.IsNullOrWhiteSpace(raw)
                ? new("WISTLET_PUBLIC_URL", Level.NotSet, "", "Not set. Federation is unavailable, and e-mail links use whatever address a request came in on.")
                : new("WISTLET_PUBLIC_URL", Level.Problem, raw.Trim(), url.Problem!));

        // ---- e-mail ----
        var host = config["Wistlet:Smtp:Host"]?.Trim();
        var from = config["Wistlet:Smtp:From"]?.Trim();
        var portText = config["Wistlet:Smtp:Port"]?.Trim();
        var security = config["Wistlet:Smtp:Security"]?.Trim();
        SmtpPort = 587;
        SmtpSecurity = SecureSocketOptions.Auto;
        if (string.IsNullOrEmpty(host))
        {
            _checks.Add(new("WISTLET_SMTP_HOST", Level.NotSet, "", "Not set, so password resets by e-mail are off."));
        }
        else
        {
            var problems = new List<string>();
            if (!string.IsNullOrEmpty(portText))
            {
                if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536) SmtpPort = port;
                else problems.Add($"WISTLET_SMTP_PORT \"{portText}\" isn't a port number, so 587 is used");
            }
            if (!string.IsNullOrEmpty(security))
            {
                if (Enum.TryParse<SecureSocketOptions>(security, ignoreCase: true, out var s) && Enum.IsDefined(s)) SmtpSecurity = s;
                else problems.Add($"WISTLET_SMTP_SECURITY \"{security}\" isn't one of Auto, StartTls, SslOnConnect or None, so Auto is used");
            }
            if (string.IsNullOrEmpty(from) || !MimeKit.MailboxAddress.TryParse(from, out _))
            {
                _checks.Add(new("WISTLET_SMTP_HOST", Level.Problem, host,
                    string.IsNullOrEmpty(from) ? "WISTLET_SMTP_FROM is empty, so e-mail is off." : $"WISTLET_SMTP_FROM \"{from}\" isn't an e-mail address, so e-mail is off."));
            }
            else
            {
                SmtpConfigured = true;
                var who = string.IsNullOrEmpty(config["Wistlet:Smtp:User"]) ? "without logging in" : "logging in as " + config["Wistlet:Smtp:User"];
                _checks.Add(new("WISTLET_SMTP_HOST", problems.Count == 0 ? Level.Ok : Level.Problem, $"{host}:{SmtpPort}",
                    problems.Count == 0 ? $"E-mail goes through {host}:{SmtpPort} ({SmtpSecurity}, {who}), from {from}." : string.Join("; ", problems) + "."));
            }
        }

        // ---- admin account ----
        var adminUser = config["Wistlet:AdminUser"]?.Trim();
        var adminPassword = config["Wistlet:AdminPassword"];
        AdminCheck = string.IsNullOrEmpty(adminUser)
            ? new("WISTLET_ADMIN_USER", Level.NotSet, "", "Not set. That's fine: the first account registered becomes the admin, and admins can promote others.")
            : string.IsNullOrEmpty(adminPassword)
                ? new("WISTLET_ADMIN_USER", Level.Ok, adminUser, "Set without a password: an existing account of that name is made an admin; no account is created.")
                : new("WISTLET_ADMIN_USER", Level.Ok, adminUser, "Checked at start-up.");
    }

    public bool AllowHttp { get; }

    /// <summary>"https://wistlet.example", or null when WISTLET_PUBLIC_URL is missing or unusable.</summary>
    public string? PublicOrigin { get; }

    /// <summary>"wistlet.example" (with the port if it isn't the default one).</summary>
    public string? PublicDomain { get; }

    public bool SmtpConfigured { get; }
    public int SmtpPort { get; }
    public SecureSocketOptions SmtpSecurity { get; }

    /// <summary>What happened to WISTLET_ADMIN_USER; updated by the start-up check that creates or promotes the account.</summary>
    public Check AdminCheck { get; set; }

    public IReadOnlyList<Check> Checks => [.. _checks, AdminCheck];

    public IEnumerable<Check> Problems => Checks.Where(c => c.Level == Level.Problem);

    /// <summary>
    /// Makes sense of the public address. Forgiving where the meaning is clear (a bare domain means
    /// https, quotes and spaces are trimmed, a path is dropped), and explains itself where it isn't.
    /// </summary>
    public static (string? Origin, string? Domain, string? Problem, string? Note) ParsePublicUrl(string? raw, bool allowHttp)
    {
        var value = (raw ?? "").Trim().Trim('"', '\'').Trim();
        if (value.Length == 0) return (null, null, null, null);

        string? note = null;
        if (!value.Contains("://"))
        {
            value = "https://" + value;
            note = "Written without https://, so https is assumed.";
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return (null, null, $"\"{raw!.Trim()}\" isn't a web address. Write it like https://wistlet.example.com", null);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return (null, null, $"\"{raw!.Trim()}\" isn't an http(s) address. Write it like https://wistlet.example.com", null);
        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp)
            return (null, null, "It starts with http://, but the public address must be https:// (Caddy serves the site over https). Change it to https://" + uri.Authority, null);
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return (null, null, "Write just the site's address, like https://wistlet.example.com, without a user name, ?query or #part.", null);

        var domain = uri.Authority.ToLowerInvariant();
        if (!FederationProtocol.IsValidDomain(domain))
            return (null, null, $"\"{uri.Authority}\" isn't a usable host name.", null);
        if (uri.AbsolutePath is not ("/" or ""))
            note = (note is null ? "" : note + " ") + $"The path \"{uri.AbsolutePath}\" is ignored: Wistlet runs at the root of its domain.";
        return (uri.GetLeftPart(UriPartial.Authority), domain, null, note);
    }

    /// <summary>The start-up summary in the log: one line per setting.</summary>
    public void Log(ILogger logger)
    {
        foreach (var c in Checks)
        {
            var line = $"{c.Variable}: {(c.Level switch { Level.Ok => "ok", Level.NotSet => "not set", _ => "PROBLEM" })}" +
                       $"{(c.Value.Length > 0 && c.Level != Level.NotSet ? $" ({c.Value})" : "")} - {c.Message}";
            if (c.Level == Level.Problem) logger.LogWarning("{Line}", line);
            else logger.LogInformation("{Line}", line);
        }
    }
}
