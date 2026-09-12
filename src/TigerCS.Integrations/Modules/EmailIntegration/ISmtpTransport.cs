using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The thinnest possible seam over <see cref="SmtpClient"/>, so
/// <see cref="SmtpEmailSender"/>'s message construction and failure
/// classification can be tested without a mail server, while the real
/// transport stays a dozen lines that need no test of their own.
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(MailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends through the configured Microsoft 365 SMTP account. A new
/// <see cref="SmtpClient"/> per send: the type is not thread-safe, and the
/// dispatcher may run on several Hangfire workers.
/// </summary>
public sealed class SmtpClientTransport(IOptions<EmailNotificationOptions> options) : ISmtpTransport
{
    public async Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var o = options.Value;

        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort)
        {
            EnableSsl = o.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(o.Username, o.Password),
            Timeout = Math.Max(5, o.TimeoutSeconds) * 1000
        };

        await client.SendMailAsync(message, cancellationToken);
    }
}
