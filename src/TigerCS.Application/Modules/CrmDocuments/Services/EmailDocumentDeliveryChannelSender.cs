using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>
/// The one document delivery channel that has an integration: the customer's
/// own email address, over the existing <see cref="IEmailSender"/> (the
/// Microsoft 365 SMTP account). The document goes as an attachment — no link
/// is created, so nothing can be shared onward, and nothing needs to expire.
/// Respects <c>EmailNotifications:Enabled</c>: with email switched off it
/// reports a permanent failure rather than pretending to have sent.
/// </summary>
public sealed class EmailDocumentDeliveryChannelSender(IEmailSender emailSender, CustomerNotificationPolicy emailPolicy)
    : IDocumentDeliveryChannelSender
{
    public DocumentDeliveryChannel Channel => DocumentDeliveryChannel.Email;

    public async Task<DocumentDeliveryResult> SendAsync(DocumentDeliveryMessage message, CancellationToken cancellationToken = default)
    {
        if (!emailPolicy.Enabled)
        {
            return new DocumentDeliveryResult(DocumentDeliveryOutcome.PermanentFailure, "EMAIL_DISABLED");
        }

        var what = message.DocumentType switch
        {
            CrmDocumentType.ReservationForm => "reservation form",
            CrmDocumentType.UnitLayout => "unit layout",
            CrmDocumentType.RegistrationReceipt => "registration receipt",
            _ => "contract"
        };

        var greeting = string.IsNullOrWhiteSpace(message.CustomerDisplayName) ? "Dear customer," : $"Dear {message.CustomerDisplayName},";
        var body =
            $"{greeting}\r\n\r\nAs you requested through our virtual assistant, please find your {what} attached.\r\n\r\n"
            + "If you did not make this request, please contact Tiger Properties customer service.\r\n\r\nTiger Properties";

        var result = await emailSender.SendAsync(
            new EmailMessage(
                message.Destination, $"Your {what} — Tiger Properties", body, message.CorrelationId,
                Attachments: [new EmailAttachment(message.FileName, message.ContentType, message.Content)]),
            cancellationToken);

        return result.Outcome switch
        {
            EmailSendOutcome.Sent => new DocumentDeliveryResult(DocumentDeliveryOutcome.Sent),
            EmailSendOutcome.TransientFailure => new DocumentDeliveryResult(DocumentDeliveryOutcome.TransientFailure, "EMAIL_TRANSIENT_FAILURE"),
            _ => new DocumentDeliveryResult(DocumentDeliveryOutcome.PermanentFailure, "EMAIL_REJECTED")
        };
    }
}
