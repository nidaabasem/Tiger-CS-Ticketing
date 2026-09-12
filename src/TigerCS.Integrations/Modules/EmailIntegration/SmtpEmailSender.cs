using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The Microsoft 365 SMTP <see cref="IEmailSender"/> adapter
/// (<c>EmailNotifications:Provider = "Smtp"</c>).
///
/// <para>
/// <b>Classifies rather than throws.</b> An SMTP status that a later attempt
/// could survive — service unavailable, mailbox busy, storage full, a
/// dropped connection — is <see cref="EmailSendOutcome.TransientFailure"/>;
/// a rejected mailbox, a refused sender identity, or an authentication /
/// TLS negotiation error that no retry will fix is
/// <see cref="EmailSendOutcome.PermanentFailure"/>.
/// </para>
///
/// <para>
/// <b>What can appear in an error or a log line:</b> the exception type and
/// the SMTP status code. Never the password (this class only ever passes it
/// to the transport), never the message body, never the full recipient — a
/// masked one is logged for correlation.
/// </para>
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailNotificationOptions> options,
    ISmtpTransport transport,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.FromEmail))
        {
            return EmailSendResult.Permanent("EmailNotifications:FromEmail is not configured.");
        }

        MailMessage mail;
        try
        {
            mail = BuildMailMessage(message, o.FromEmail, o.FromName);
        }
        catch (FormatException)
        {
            return EmailSendResult.Permanent("The recipient or sender address was rejected as malformed.");
        }

        using (mail)
        {
            try
            {
                await transport.SendAsync(mail, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SmtpFailedRecipientException ex)
            {
                return Classify(ex.StatusCode, ex, message);
            }
            catch (SmtpException ex)
            {
                return Classify(ex.StatusCode, ex, message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Sockets, TLS handshakes, DNS: nothing here is a verdict on
                // the message, so a later attempt is worth making.
                logger.LogWarning(
                    "SMTP send to {MaskedRecipient} failed with {ExceptionType}; will retry. CorrelationId {CorrelationId}.",
                    Mask(message.ToAddress), ex.GetType().Name, message.CorrelationId);
                return EmailSendResult.Transient($"SMTP transport threw {ex.GetType().Name}.");
            }
        }

        logger.LogInformation(
            "SMTP accepted a message for {MaskedRecipient}. CorrelationId {CorrelationId}.",
            Mask(message.ToAddress), message.CorrelationId);

        return EmailSendResult.Sent();
    }

    /// <summary>Multipart/alternative: plain text always, HTML when supplied, so any client renders something sensible.</summary>
    public static MailMessage BuildMailMessage(EmailMessage message, string fromEmail, string? fromName)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mail = new MailMessage
        {
            From = new MailAddress(fromEmail, string.IsNullOrWhiteSpace(fromName) ? null : fromName, Encoding.UTF8),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        mail.To.Add(new MailAddress(message.ToAddress));
        mail.Headers.Add("X-TigerCS-Correlation-Id", message.CorrelationId.ToString());

        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            mail.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));
        }

        return mail;
    }

    private EmailSendResult Classify(SmtpStatusCode status, SmtpException ex, EmailMessage message)
    {
        var permanent = IsPermanent(status);

        logger.LogWarning(
            "SMTP send to {MaskedRecipient} failed: {ExceptionType} status {StatusCode} ({Classification}). CorrelationId {CorrelationId}.",
            Mask(message.ToAddress), ex.GetType().Name, status, permanent ? "permanent" : "transient", message.CorrelationId);

        var error = $"SMTP {status} ({ex.GetType().Name}).";
        return permanent ? EmailSendResult.Permanent(error) : EmailSendResult.Transient(error);
    }

    /// <summary>Status codes no retry can fix: the address, the sender identity, the credentials or the protocol itself was refused.</summary>
    public static bool IsPermanent(SmtpStatusCode status) => status switch
    {
        SmtpStatusCode.MailboxUnavailable => true,
        SmtpStatusCode.MailboxNameNotAllowed => true,
        SmtpStatusCode.UserNotLocalTryAlternatePath => true,
        SmtpStatusCode.ExceededStorageAllocation => true,
        SmtpStatusCode.ClientNotPermitted => true,
        SmtpStatusCode.MustIssueStartTlsFirst => true,
        SmtpStatusCode.CommandNotImplemented => true,
        SmtpStatusCode.CommandParameterNotImplemented => true,
        SmtpStatusCode.SyntaxError => true,
        SmtpStatusCode.CommandUnrecognized => true,
        SmtpStatusCode.BadCommandSequence => true,
        SmtpStatusCode.TransactionFailed => true,
        _ => false
    };

    private static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{address[..1]}***{address[at..]}";
    }
}
