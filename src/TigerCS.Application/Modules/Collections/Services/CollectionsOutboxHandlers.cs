using System.Globalization;
using System.Text.Json;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Sends a queued SMS/email reminder — after re-reading the account from the
/// financial source one last time. A settled (or now-inconsistent) account
/// is suppressed instead of reminded, and the amount actually sent is
/// recorded beside the amount originally queued.
///
/// <para>
/// Runs on the existing outbox dispatcher, so retries, attempt limits and
/// dead-lettering are the platform's, and two workers never send the same
/// message twice (the outbox claim). Provider acceptance is recorded as
/// <see cref="ReminderStatus.Sent"/>; <see cref="ReminderStatus.Delivered"/>
/// comes only from the provider's own delivery report.
/// </para>
/// </summary>
public sealed class CollectionsReminderDispatchHandler(
    ICollectionsReminderRepository reminderRepository,
    ICollectionsFinancialSource source,
    IEnumerable<IReminderDeliveryProvider> providers,
    CollectionsClock clock) : IOutboxEventHandler
{
    public string EventType => CollectionsReminderAppService.DispatchEventType;

    public async Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<CollectionsReminderAppService.DispatchPayload>(message.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DeduplicationKey))
        {
            return OutboxHandlingResult.Permanent("Malformed Collections dispatch payload.");
        }

        var reminder = await reminderRepository.GetByDeduplicationKeyAsync(payload.DeduplicationKey, cancellationToken);
        if (reminder is null)
        {
            return OutboxHandlingResult.Permanent("The queued reminder no longer exists.");
        }

        if (reminder.Status != ReminderStatus.Queued)
        {
            return OutboxHandlingResult.Succeeded();
        }

        // Revalidate immediately before dispatch.
        FinancialAccountSnapshot? account;
        try
        {
            var accounts = await source.GetCustomerAccountsAsync(reminder.CrmCustomerId, cancellationToken);
            account = accounts?.FirstOrDefault(a => a.AccountId == reminder.AccountId);
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            // Never send on stale figures: wait for the source.
            return OutboxHandlingResult.Transient($"Financial source unavailable: {ex.Message}");
        }

        var now = clock.UtcNow;
        if (account is null)
        {
            reminder.Suppress("The financial source no longer reports this account.", now);
            return OutboxHandlingResult.Succeeded();
        }

        var eligibility = ReminderPolicy.Evaluate(account, reminder.Type, clock.Today, clock.Rules);
        if (!eligibility.IsEligible)
        {
            reminder.Suppress(eligibility.Reason == ReminderPolicy.SettledReason
                ? "Settled before dispatch."
                : $"Not eligible at dispatch ({eligibility.Reason}).", now);
            return OutboxHandlingResult.Succeeded();
        }

        if (eligibility.Currency != reminder.Currency)
        {
            reminder.Suppress($"The account currency changed from {reminder.Currency} to {eligibility.Currency}.", now);
            return OutboxHandlingResult.Succeeded();
        }

        reminder.RecordDispatchRevalidation(eligibility.Amount!.Value, account.AsOfUtc);

        var provider = providers.FirstOrDefault(p => p.Channel == reminder.Channel);
        if (provider is null)
        {
            const string error = "No approved delivery provider is configured for this channel.";
            reminder.ApplyDelivery(ReminderEventType.Failed, now, error, null);
            reminder.AddEvent(CollectionsReminderEvent.Dispatched(reminder, ReminderEventType.Failed, now, error));
            return OutboxHandlingResult.Succeeded();
        }

        var result = await provider.SendAsync(new ReminderDeliveryRequest(
            reminder.CollectionsReminderId, reminder.Type, account.CustomerName, account.CustomerPhone, account.CustomerEmail,
            eligibility.Amount.Value, eligibility.Currency, account.UnitNumber, account.ProjectName, message.CorrelationId),
            cancellationToken);

        switch (result.Outcome)
        {
            case ReminderDeliveryOutcome.Accepted:
                reminder.ApplyDelivery(ReminderEventType.Sent, clock.UtcNow, null, result.ProviderReference);
                reminder.AddEvent(CollectionsReminderEvent.Dispatched(reminder, ReminderEventType.Sent, clock.UtcNow, result.ProviderReference));
                return OutboxHandlingResult.Succeeded();

            case ReminderDeliveryOutcome.PermanentFailure:
                reminder.ApplyDelivery(ReminderEventType.Failed, clock.UtcNow, result.Error, null);
                reminder.AddEvent(CollectionsReminderEvent.Dispatched(reminder, ReminderEventType.Failed, clock.UtcNow, result.Error));
                return OutboxHandlingResult.Succeeded();

            default:
                return OutboxHandlingResult.Transient(result.Error ?? "Transient delivery failure.");
        }
    }
}

/// <summary>The durable retry behind a customer response's ticket. See <see cref="CollectionsReminderOutcomeAppService"/>.</summary>
public sealed class CollectionsResponseTicketHandler(CollectionsReminderOutcomeAppService outcomes) : IOutboxEventHandler
{
    public string EventType => CollectionsReminderOutcomeAppService.ResponseTicketEventType;

    public async Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<CollectionsReminderOutcomeAppService.ResponseTicketPayload>(message.Payload);
        if (payload is null)
        {
            return OutboxHandlingResult.Permanent("Malformed Collections response-ticket payload.");
        }

        var result = await outcomes.ProcessResponseTicketAsync(payload.ReminderId, payload.EventId, payload.ActorEmployeeId, cancellationToken);
        return result.Outcome switch
        {
            ResponseTicketProcessingOutcome.Done => OutboxHandlingResult.Succeeded(),
            ResponseTicketProcessingOutcome.Gone => OutboxHandlingResult.Permanent(result.Detail ?? "Gone."),
            _ => OutboxHandlingResult.Transient(result.Detail ?? "Ticket still pending.")
        };
    }
}

/// <summary>
/// Email reminders through the existing EmailNotifications sender — the one
/// approved outbound email path (SMTP in a real environment, the in-memory
/// Recording sender in tests). Registered only when the email channel is
/// enabled. There is no SMS counterpart: TigerCS has no approved SMS
/// provider, so an SMS reminder fails closed at dispatch.
/// </summary>
public sealed class EmailReminderDeliveryProvider(IEmailSender emailSender) : IReminderDeliveryProvider
{
    public ReminderChannel Channel => ReminderChannel.Email;

    public async Task<ReminderDeliveryResult> SendAsync(ReminderDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return new ReminderDeliveryResult(ReminderDeliveryOutcome.PermanentFailure, Error: "No email address.");
        }

        var amount = request.Amount.ToString("N2", CultureInfo.InvariantCulture);
        var unit = request.UnitNumber is null ? "" : $" for unit {request.UnitNumber}{(request.ProjectName is null ? "" : $", {request.ProjectName}")}";
        var greeting = string.IsNullOrWhiteSpace(request.CustomerName) ? "Dear customer," : $"Dear {request.CustomerName},";
        var body =
            $"{greeting}\n\nThis is a reminder that {request.Currency} {amount} is due on your payment plan{unit}.\n\n"
            + "If you have already paid, please disregard this message or reply with your payment reference so our Collections team can verify it.\n\n"
            + "Tiger Group Collections";

        var result = await emailSender.SendAsync(
            new EmailMessage(request.Email, $"Payment reminder{unit}", body, request.CorrelationId), cancellationToken);

        return result.Outcome switch
        {
            EmailSendOutcome.Sent => new ReminderDeliveryResult(ReminderDeliveryOutcome.Accepted),
            EmailSendOutcome.PermanentFailure => new ReminderDeliveryResult(ReminderDeliveryOutcome.PermanentFailure, Error: result.Error),
            _ => new ReminderDeliveryResult(ReminderDeliveryOutcome.TransientFailure, Error: result.Error)
        };
    }
}
