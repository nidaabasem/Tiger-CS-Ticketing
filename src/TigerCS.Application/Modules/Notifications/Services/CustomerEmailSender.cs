using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// The default <see cref="ICustomerEmailSender"/>: applies the shared
/// customer-notification rules, then delegates the actual delivery to
/// whichever <see cref="IEmailSender"/> adapter is configured.
///
/// <para>
/// <b>What is logged.</b> Exactly one line per call: notification type,
/// ticket number, masked recipient, outcome, reason, correlation ID. Never
/// the subject or body (customer content), never the full address, and —
/// structurally — never a credential: this class has no access to any.
/// </para>
///
/// <para>
/// <b>What is never thrown.</b> A provider that throws is turned into a
/// transient result carrying only the exception's type name. The caller —
/// an Outbox handler running in a background job — can therefore never take
/// a ticket operation down, and a provider message that might echo a
/// server banner or an address never reaches a persisted error column.
/// </para>
/// </summary>
public sealed class CustomerEmailSender(
    IEmailSender emailSender,
    CustomerNotificationPolicy policy,
    ILogger<CustomerEmailSender> logger) : ICustomerEmailSender
{
    public async Task<CustomerEmailSendResult> SendAsync(CustomerEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CustomerEmailSendResult result;

        if (!policy.Enabled)
        {
            result = CustomerEmailSendResult.Skipped(CustomerEmailSkipReasons.NotificationsDisabled);
        }
        else if (CustomerEmailAddress.Normalize(request.RecipientAddress) is not { } recipient)
        {
            result = CustomerEmailSendResult.Skipped(CustomerEmailSkipReasons.NoCustomerEmail);
        }
        else if (!CustomerEmailAddress.IsValid(recipient))
        {
            result = CustomerEmailSendResult.Skipped(CustomerEmailSkipReasons.InvalidCustomerEmail);
        }
        else
        {
            result = await DeliverAsync(recipient, request, cancellationToken);
        }

        Log(request, result);
        return result;
    }

    private async Task<CustomerEmailSendResult> DeliverAsync(
        string recipient, CustomerEmailRequest request, CancellationToken cancellationToken)
    {
        var message = new EmailMessage(
            recipient,
            request.Content.Subject,
            request.Content.TextBody,
            request.CorrelationId,
            request.Content.HtmlBody);

        EmailSendResult providerResult;
        try
        {
            providerResult = await emailSender.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type name only — see this class's remarks.
            return CustomerEmailSendResult.Transient($"Email provider threw {ex.GetType().Name}.");
        }

        return providerResult.Outcome switch
        {
            EmailSendOutcome.Sent => CustomerEmailSendResult.Sent(),
            EmailSendOutcome.PermanentFailure => CustomerEmailSendResult.Permanent(
                providerResult.Error ?? "The email provider reported a permanent failure."),
            _ => CustomerEmailSendResult.Transient(
                providerResult.Error ?? "The email provider reported a transient failure.")
        };
    }

    private void Log(CustomerEmailRequest request, CustomerEmailSendResult result)
    {
        var masked = CustomerEmailAddress.Mask(request.RecipientAddress);

        switch (result.Outcome)
        {
            case CustomerEmailSendOutcome.Sent:
                logger.LogInformation(
                    "Customer email {NotificationType} for ticket {TicketNumber} sent to {MaskedRecipient}. CorrelationId {CorrelationId}.",
                    request.NotificationType, request.TicketNumber, masked, request.CorrelationId);
                break;

            case CustomerEmailSendOutcome.Skipped:
                logger.LogInformation(
                    "Customer email {NotificationType} for ticket {TicketNumber} skipped ({Reason}); recipient {MaskedRecipient}. CorrelationId {CorrelationId}.",
                    request.NotificationType, request.TicketNumber, result.Reason, masked, request.CorrelationId);
                break;

            default:
                logger.LogWarning(
                    "Customer email {NotificationType} for ticket {TicketNumber} to {MaskedRecipient} failed ({Outcome}): {Reason}. CorrelationId {CorrelationId}.",
                    request.NotificationType, request.TicketNumber, masked, result.Outcome, result.Reason, request.CorrelationId);
                break;
        }
    }
}
