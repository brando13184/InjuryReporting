using System.Net;
using System.Net.Mail;

namespace InjuryReporting.Web.Services;

public interface IAppEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody);
}

public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@example.org";
}

/// <summary>
/// Sends mail via SMTP (STARTTLS). When no host is configured: in Development the message is written to
/// the console so links can be followed; in any other environment sending fails loudly rather than
/// silently dropping security email.
/// </summary>
public class SmtpEmailSender : IAppEmailSender
{
    private readonly SmtpOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(Microsoft.Extensions.Options.IOptions<SmtpOptions> options, IHostEnvironment env, ILogger<SmtpEmailSender> log)
    {
        _options = options.Value; _env = env; _log = log;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            if (!_env.IsDevelopment())
                throw new InvalidOperationException("Smtp:Host is not configured; cannot send email.");
            _log.LogWarning("DEV EMAIL (no SMTP configured)\nTo: {To}\nSubject: {Subject}\n{Body}", toEmail, subject, htmlBody);
            return;
        }

        using var client = new SmtpClient(_options.Host, _options.Port) { EnableSsl = true };
        if (!string.IsNullOrEmpty(_options.Username))
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        using var msg = new MailMessage(_options.From, toEmail, subject, htmlBody) { IsBodyHtml = true };
        await client.SendMailAsync(msg);
    }
}
