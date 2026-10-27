using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Wistlet.Services;

/// <summary>SMTP settings, from the <c>Wistlet:Smtp</c> configuration section (environment variables in Docker).</summary>
public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;

    /// <summary>MailKit's <see cref="SecureSocketOptions"/>: Auto, None, SslOnConnect, StartTls or StartTlsWhenAvailable.</summary>
    public string Security { get; set; } = "Auto";

    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
}

/// <summary>
/// Sends plain-text e-mail through the configured SMTP server. Without one, e-mail is
/// unavailable, except in development where messages are written to the log instead.
/// </summary>
public class EmailService(IConfiguration config, IHostEnvironment env, SiteSettingsService settings, ILogger<EmailService> logger)
{
    private readonly SmtpOptions _options = config.GetSection("Wistlet:Smtp").Get<SmtpOptions>() ?? new SmtpOptions();

    /// <summary>An SMTP server is configured.</summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.From);

    public bool IsAvailable => HasServer || env.IsDevelopment();

    /// <summary>
    /// An absolute link for e-mails. Uses <c>Wistlet:PublicUrl</c> when set, so a forged Host
    /// header can't point links elsewhere; otherwise the address the request came in on.
    /// </summary>
    public string AbsoluteUrl(HttpRequest request, string path)
    {
        var configured = config["Wistlet:PublicUrl"];
        var origin = string.IsNullOrWhiteSpace(configured) ? $"{request.Scheme}://{request.Host}" : configured.TrimEnd('/');
        return origin + path;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!HasServer)
        {
            if (!env.IsDevelopment()) throw new InvalidOperationException("No SMTP server is configured.");
            logger.LogInformation("E-mail to {To} (no SMTP server configured)\nSubject: {Subject}\n\n{Body}", to, subject, body);
            return;
        }

        var message = new MimeMessage();
        var from = MailboxAddress.Parse(_options.From);
        if (string.IsNullOrEmpty(from.Name)) from.Name = settings.Current.SiteName;
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        var security = Enum.TryParse<SecureSocketOptions>(_options.Security, ignoreCase: true, out var s) ? s : SecureSocketOptions.Auto;
        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, security, ct);
        if (!string.IsNullOrEmpty(_options.User))
            await client.AuthenticateAsync(_options.User, _options.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
