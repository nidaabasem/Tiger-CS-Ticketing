using System.Globalization;
using System.Text.Json;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Sends one queued SMS/email channel — after re-reading the account from the
/// financial source immediately before delivery. A settled, ineligible,
/// stale-read or inconsistent account is suppressed instead of reminded, and
/// the amount re-read is stored next to the amount quoted.
///
/// <para>
/// Runs on the existing outbox dispatcher, so retries, attempt limits and
/// dead-lettering are the platform's, and two workers never send the same
/// message twice (the outbox claim). Provider acceptance is <c>Sent</c>;
/// <c>Delivered</c> comes only from the provider's own report.
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

        var row = (await reminderRepository.GetChannelsByDeduplicationKeysAsync([payload.DeduplicationKey], cancellationToken)).SingleOrDefault();
        var reminder = row is null ? null : await reminderRepository.GetByIdAsync(row.CollectionsReminderId, cancellationToken);
        var channel = reminder?.ChannelFor(row!.Channel);
        if (reminder is null || channel is null)
        {
            return OutboxHandlingResult.Permanent("The queued reminder no longer exists.");
        }

        // A message for an earlier attempt, or a channel already past Queued, is done.
        if (channel.Status != ChannelStatus.Queued || channel.Attempts != payload.Attempt)
        {
            return OutboxHandlingResult.Succeeded();
        }

        FinancialAccountSnapshot? account;
        try
        {
            account = (await source.GetCustomerAccountsAsync(reminder.CrmCustomerId, cancellationToken))?
                .FirstOrDefault(a => a.AccountId == reminder.AccountId);
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            // Never send on figures that could not be re-read: wait for the source.
            return OutboxHandlingResult.Transient($"Financial source unavailable: {ex.Message}");
        }

        var now = clock.UtcNow;
        if (account is null)
        {
            channel.Suppress("The financial source no longer reports this account.", now);
            return OutboxHandlingResult.Succeeded();
        }

        if (clock.IsStale(account.AsOfUtc))
        {
            return OutboxHandlingResult.Transient("The financial source's figures are stale; a stale read never authorizes a send.");
        }

        var eligibility = ReminderPolicy.Evaluate(account, reminder.Type, clock.BusinessDate, clock.Rules);
        if (!eligibility.IsEligible)
        {
            channel.Suppress(eligibility.Reason == ReminderPolicy.SettledReason
                ? "Settled before dispatch."
                : $"Not eligible at dispatch ({eligibility.Reason}).", now);
            return OutboxHandlingResult.Succeeded();
        }

        if (eligibility.Currency != reminder.Currency)
        {
            channel.Suppress($"The account currency changed from {reminder.Currency} to {eligibility.Currency}.", now);
            return OutboxHandlingResult.Succeeded();
        }

        channel.RecordDispatchRevalidation(eligibility.Amount, account.AsOfUtc);

        var provider = providers.FirstOrDefault(p => p.Channel == channel.Channel);
        if (provider is null)
        {
            channel.RecordDispatch(ChannelStatus.Failed, now, null, "No approved delivery provider is configured for this channel.");
            return OutboxHandlingResult.Succeeded();
        }

        // The amount stated is the one re-read now — never more than the
        // quoted amount's basis, and never a settled one.
        var result = await provider.SendAsync(new ReminderDeliveryRequest(
            reminder.PublicId, reminder.Type, reminder.Language, account.CustomerName, account.CustomerPhone, account.CustomerEmail,
            eligibility.Amount, eligibility.Currency, account.UnitNumber, account.TowerName, message.CorrelationId),
            cancellationToken);

        switch (result.Outcome)
        {
            case ReminderDeliveryOutcome.Accepted:
                channel.RecordDispatch(ChannelStatus.Sent, clock.UtcNow, result.ProviderMessageId, null);
                return OutboxHandlingResult.Succeeded();

            case ReminderDeliveryOutcome.PermanentFailure:
                channel.RecordDispatch(ChannelStatus.Failed, clock.UtcNow, null, result.Error ?? "Permanent delivery failure.");
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
/// approved outbound email path. English only: no Arabic template has been
/// approved, so an "ar" reminder is refused permanently rather than sent in
/// the wrong language. There is no SMS counterpart: TigerCS has no approved
/// SMS provider, so an SMS reminder fails closed at dispatch.
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

        if (request.Language != "en")
        {
            return new ReminderDeliveryResult(ReminderDeliveryOutcome.PermanentFailure, Error: $"No approved '{request.Language}' email template.");
        }

        var amount = request.Amount.ToString("N2", CultureInfo.InvariantCulture);
        var unit = request.UnitNumber is null ? "" : $" for unit {request.UnitNumber}{(request.TowerName is null ? "" : $", {request.TowerName}")}";
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
