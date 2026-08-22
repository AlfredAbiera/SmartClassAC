using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;

namespace SmartClassAC.Services;

public sealed class PasswordResetOptions
{
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "SmartClass AC";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public interface IPasswordResetEmailSender
{
    Task SendAsync(string recipientEmail, string resetUrl, CancellationToken cancellationToken = default);
}

public sealed class SmtpPasswordResetEmailSender : IPasswordResetEmailSender
{
    private readonly PasswordResetOptions _options;

    public SmtpPasswordResetEmailSender(IOptions<PasswordResetOptions> options) => _options = options.Value;

    public async Task SendAsync(string recipientEmail, string resetUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.FromAddress) ||
            string.IsNullOrWhiteSpace(_options.SmtpHost) ||
            string.IsNullOrWhiteSpace(_options.SmtpUsername) ||
            string.IsNullOrWhiteSpace(_options.SmtpPassword))
        {
            throw new InvalidOperationException(
                "Password-reset email delivery is not configured. Set PasswordReset SMTP settings in User Secrets.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = "Reset your SmartClass AC password",
            Body = $"""
                <p>A password reset was requested for your SmartClass AC account.</p>
                <p><a href="{WebUtility.HtmlEncode(resetUrl)}">Reset your password</a></p>
                <p>This link expires in 15 minutes and can be used once. If you did not request it, you can ignore this email.</p>
                """,
            IsBodyHtml = true
        };
        message.To.Add(recipientEmail);

        using var smtp = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword)
        };

        await smtp.SendMailAsync(message, cancellationToken);
    }
}
