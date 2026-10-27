using MailKit.Net.Smtp;
using MimeKit;

namespace Wistlet.Services;

/// <summary>
/// Sends plain-text e-mail through the configured SMTP server. Without one, e-mail is
/// unavailable, except in development where messages are written to the log instead.
/// </summary>
/// <remarks>The SMTP settings (<c>Wistlet:Smtp:*</c>) are read and checked by <see cref="ServerConfig"/>.</remarks>
public class EmailService(IConfiguration config, ServerConfig server, IHostEnvironment env, SiteSettingsService settings, ILogger<EmailService> logger)
{
    /// <summary>An SMTP server is configured.</summary>
    public bool HasServer => server.SmtpConfigured;

    public bool IsAvailable => HasServer || env.IsDevelopment();

    /// <summary>
    /// An absolute link for e-mails. Uses <c>Wistlet:PublicUrl</c> when set, so a forged Host
    /// header can't point links elsewhere; otherwise the address the request came in on.
    /// </summary>
    public string AbsoluteUrl(HttpRequest request, string path) =>
        (server.PublicOrigin ?? $"{request.Scheme}://{request.Host}") + path;

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!HasServer)
        {
            if (!env.IsDevelopment()) throw new InvalidOperationException("No SMTP server is configured.");
            logger.LogInformation("E-mail to {To} (no SMTP server configured)\nSubject: {Subject}\n\n{Body}", to, subject, body);
            return;
        }

        var message = new MimeMessage();
        var from = MailboxAddress.Parse(config["Wistlet:Smtp:From"]!.Trim());
        if (string.IsNullOrEmpty(from.Name)) from.Name = settings.Current.SiteName;
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(config["Wistlet:Smtp:Host"]!.Trim(), server.SmtpPort, server.SmtpSecurity, ct);
        var user = config["Wistlet:Smtp:User"];
        if (!string.IsNullOrEmpty(user))
            await client.AuthenticateAsync(user, config["Wistlet:Smtp:Password"] ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
