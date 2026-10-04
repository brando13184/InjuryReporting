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
/// Sends mail via SMTP (STARTTLS), e.g. the Amazon SES SMTP endpoint. When no host is configured: in
/// Development the message is written to the console so links can be followed; elsewhere an error is logged.
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
            if (_env.IsDevelopment())
                _log.LogWarning("DEV EMAIL (no SMTP configured)\nTo: {To}\nSubject: {Subject}\n{Body}", toEmail, subject, htmlBody);
            else
                _log.LogError("Email '{Subject}' was not sent: Smtp:Host is not configured.", subject);
            return;
        }

        // Delivery failures are logged, never thrown. Callers send mail only for accounts that exist, so a
        // thrown error would make the response differ for real vs unknown addresses (account enumeration).
        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port) { EnableSsl = true };
            if (!string.IsNullOrEmpty(_options.Username))
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            using var msg = new MailMessage(_options.From, toEmail, subject, htmlBody) { IsBodyHtml = true };
            await client.SendMailAsync(msg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Email '{Subject}' could not be sent via {Host}.", subject, _options.Host);
        }
    }
}
