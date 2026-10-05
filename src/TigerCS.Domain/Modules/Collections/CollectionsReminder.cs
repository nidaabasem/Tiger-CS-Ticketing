using System.Globalization;

namespace TigerCS.Domain.Modules.Collections;

public enum ReminderTrigger
{
    /// <summary>The designated scheduler queued it when the window opened.</summary>
    Scheduled = 1,

    /// <summary>An authorized user queued it from the Payment tab.</summary>
    User = 2,

    /// <summary>An integration (the Genesys outbound campaign) queued it.</summary>
    Integration = 3
}

/// <summary>
/// One queued payment reminder job: one account, one reminder type, one
/// cycle, one quoted amount, delivered on one or more channels.
///
/// <para>
/// <b>Duplicate prevention is per channel</b>: each
/// <see cref="CollectionsReminderChannel"/> carries the key
/// account | type | cycle | channel, unique in the database, so no request,
/// idempotency key or concurrent scheduler can send the same channel twice in
/// a cycle. The amount quoted is persisted when queued (<see cref="Amount"/>)
/// and never changes; the amount re-read immediately before each TigerCS
/// dispatch is stored on the channel.
/// </para>
/// </summary>
public class CollectionsReminder
{
    public const string PublicIdPrefix = "REM-";
    public const int AccountIdMaxLength = 64;
    public const int CycleKeyMaxLength = 40;
    public const int AmountBasisMaxLength = 80;
    public const int InstalmentIdsMaxLength = 1000;
    public const int IdempotencyKeyMaxLength = 128;
    public const int ReasonMaxLength = 500;

    public long CollectionsReminderId { get; private set; }

    public string PublicId => PublicIdPrefix + CollectionsReminderId.ToString(CultureInfo.InvariantCulture);

    public long CrmCustomerId { get; private set; }
    public string AccountId { get; private set; } = string.Empty;
    public long? UnitId { get; private set; }

    public ReminderType Type { get; private set; }
    public string CycleKey { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string AmountBasis { get; private set; } = string.Empty;

    /// <summary>The qualifying instalments, comma-separated.</summary>
    public string InstalmentIds { get; private set; } = string.Empty;

    public DateTime SourceAsOfUtc { get; private set; }
    public string Language { get; private set; } = "en";

    public ReminderTrigger Trigger { get; private set; }
    public Guid? RequestedByEmployeeId { get; private set; }

    /// <summary>The caller's Idempotency-Key, when one was sent. Unique.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>SHA-256 of the canonical request, to tell a replay from a conflicting reuse of the key.</summary>
    public string? RequestHash { get; private set; }

    public DateTime QueuedAtUtc { get; private set; }

    private readonly List<CollectionsReminderChannel> _channels = [];
    public IReadOnlyCollection<CollectionsReminderChannel> Channels => _channels;

    private readonly List<CollectionsReminderEvent> _events = [];
    public IReadOnlyCollection<CollectionsReminderEvent> Events => _events;

    private CollectionsReminder() { }

    public CollectionsReminder(
        long crmCustomerId,
        string accountId,
        long? unitId,
        ReminderType type,
        string cycleKey,
        ReminderEligibility eligibility,
        DateTime sourceAsOfUtc,
        string language,
        ReminderTrigger trigger,
        Guid? requestedByEmployeeId,
        string? idempotencyKey,
        string? requestHash,
        IReadOnlyCollection<ReminderChannel> channels,
        DateTime queuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(channels);

        if (crmCustomerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crmCustomerId));
        }

        if (string.IsNullOrWhiteSpace(accountId) || accountId.Trim().Length > AccountIdMaxLength)
        {
            throw new ArgumentException($"accountId is required and at most {AccountIdMaxLength} characters.", nameof(accountId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (!eligibility.IsEligible || eligibility.Amount <= 0m)
        {
            throw new ArgumentException("A reminder is only ever queued for an eligible amount actually owed.", nameof(eligibility));
        }

        if (channels.Count == 0 || channels.Distinct().Count() != channels.Count || channels.Any(c => !Enum.IsDefined(c)))
        {
            throw new ArgumentException("At least one distinct, known channel is required.", nameof(channels));
        }

        if (string.IsNullOrWhiteSpace(cycleKey) || cycleKey.Length > CycleKeyMaxLength)
        {
            throw new ArgumentException("A cycle key is required.", nameof(cycleKey));
        }

        CrmCustomerId = crmCustomerId;
        AccountId = accountId.Trim();
        UnitId = unitId;
        Type = type;
        CycleKey = cycleKey;
        Currency = eligibility.Currency;
        Amount = eligibility.Amount;
        AmountBasis = eligibility.AmountBasis;
        InstalmentIds = Truncate(string.Join(",", eligibility.InstalmentIds), InstalmentIdsMaxLength) ?? string.Empty;
        SourceAsOfUtc = sourceAsOfUtc;
        Language = language;
        Trigger = trigger;
        RequestedByEmployeeId = requestedByEmployeeId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        RequestHash = requestHash;
        QueuedAtUtc = queuedAtUtc;

        foreach (var channel in channels)
        {
            _channels.Add(new CollectionsReminderChannel(this, channel));
            _events.Add(CollectionsReminderEvent.Internal(this, channel, "queued", 1, queuedAtUtc, requestedByEmployeeId, null));
        }
    }

    /// <summary>account | type | cycle | channel — the duplicate-prevention identity of one channel's send.</summary>
    public static string BuildDeduplicationKey(string accountId, ReminderType type, string cycleKey, ReminderChannel channel) =>
        $"{accountId.Trim()}|{type}|{cycleKey}|{channel}";

    /// <summary>"REM-123" or "123" → 123.</summary>
    public static bool TryParsePublicId(string? value, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith(PublicIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[PublicIdPrefix.Length..];
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    public CollectionsReminderChannel? ChannelFor(ReminderChannel channel) => _channels.FirstOrDefault(c => c.Channel == channel);

    public CollectionsReminderEvent AddEvent(CollectionsReminderEvent reminderEvent)
    {
        ArgumentNullException.ThrowIfNull(reminderEvent);
        _events.Add(reminderEvent);
        return reminderEvent;
    }

    internal static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// One channel of a reminder job, with its own delivery state.
///
/// <para>
/// <b>Ordered, never overwritten backwards.</b> An event older than the last
/// one applied is recorded but does not change the status; a delivered SMS or
/// an answered call is final; a suppressed channel accepts nothing. A Failed
/// or NoAnswer channel may be re-queued (retries affect failed channels only),
/// within the same de-duplication key.
/// </para>
/// </summary>
public class CollectionsReminderChannel
{
    public const int DeduplicationKeyMaxLength = 200;
    public const int ProviderMessageIdMaxLength = 128;

    public long CollectionsReminderChannelId { get; private set; }
    public long CollectionsReminderId { get; private set; }
    public CollectionsReminder? Reminder { get; private set; }

    public ReminderChannel Channel { get; private set; }
    public string DeduplicationKey { get; private set; } = string.Empty;
    public ChannelStatus Status { get; private set; }
    public string? StatusReason { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public DateTime? LastEventAtUtc { get; private set; }
    public int Attempts { get; private set; }

    public decimal? DispatchAmount { get; private set; }
    public DateTime? DispatchSourceAsOfUtc { get; private set; }

    private CollectionsReminderChannel() { }

    internal CollectionsReminderChannel(CollectionsReminder reminder, ReminderChannel channel)
    {
        Reminder = reminder;
        Channel = channel;
        DeduplicationKey = CollectionsReminder.BuildDeduplicationKey(reminder.AccountId, reminder.Type, reminder.CycleKey, channel);
        Status = ChannelStatus.Queued;
        Attempts = 1;
    }

    public bool CanRetry => Status is ChannelStatus.Failed or ChannelStatus.NoAnswer;

    /// <summary>Applies a delivery status reported at <paramref name="occurredAtUtc"/>. Returns whether the status moved.</summary>
    public bool Apply(ChannelStatus status, DateTime occurredAtUtc, string? providerMessageId, string? reason)
    {
        if (!ReminderPolicy.IsValidDeliveryStatus(Channel, status))
        {
            throw new ArgumentException($"{status} is not a {Channel} delivery status.", nameof(status));
        }

        if (!string.IsNullOrWhiteSpace(providerMessageId))
        {
            ProviderMessageId ??= CollectionsReminder.Truncate(providerMessageId.Trim(), ProviderMessageIdMaxLength);
        }

        if (Status == ChannelStatus.Suppressed
            || ReminderPolicy.IsSuccessTerminal(Status)
            || (LastEventAtUtc is { } last && occurredAtUtc < last)
            || (Status == status))
        {
            return false;
        }

        Status = status;
        LastEventAtUtc = occurredAtUtc;
        StatusReason = status is ChannelStatus.Failed or ChannelStatus.NoAnswer
            ? CollectionsReminder.Truncate(reason, CollectionsReminder.ReasonMaxLength)
            : null;
        return true;
    }

    /// <summary>Revalidation found nothing left to remind about. Only a queued channel can be suppressed.</summary>
    public bool Suppress(string reason, DateTime atUtc)
    {
        if (Status != ChannelStatus.Queued)
        {
            return false;
        }

        Status = ChannelStatus.Suppressed;
        StatusReason = CollectionsReminder.Truncate(reason, CollectionsReminder.ReasonMaxLength);
        LastEventAtUtc = atUtc;
        Reminder?.AddEvent(CollectionsReminderEvent.Internal(Reminder, Channel, "suppressed", Attempts, atUtc, null, StatusReason));
        return true;
    }

    /// <summary>A retry of a failed channel in the same cycle — the same de-duplication key, a new attempt.</summary>
    public void Requeue(DateTime atUtc, Guid? actor)
    {
        if (!CanRetry)
        {
            throw new InvalidOperationException($"Only a failed channel can be retried; this one is {Status}.");
        }

        Attempts++;
        Status = ChannelStatus.Queued;
        StatusReason = null;
        LastEventAtUtc = atUtc;
        Reminder?.AddEvent(CollectionsReminderEvent.Internal(Reminder, Channel, "queued", Attempts, atUtc, actor, "Retry of a failed delivery."));
    }

    public void RecordDispatchRevalidation(decimal amount, DateTime sourceAsOfUtc)
    {
        DispatchAmount = amount;
        DispatchSourceAsOfUtc = sourceAsOfUtc;
    }

    /// <summary>TigerCS's own dispatch result: the provider accepted (Sent) or permanently refused (Failed).</summary>
    public void RecordDispatch(ChannelStatus status, DateTime atUtc, string? providerMessageId, string? reason)
    {
        if (Apply(status, atUtc, providerMessageId, reason) && Reminder is not null)
        {
            Reminder.AddEvent(CollectionsReminderEvent.Internal(Reminder, Channel, status.ToString().ToLowerInvariant(), Attempts, atUtc, null, reason ?? providerMessageId));
        }
    }
}

/// <summary>
/// One thing that happened to a reminder: a delivery event, a customer
/// response, or TigerCS's own queueing/dispatch. Idempotent on
/// (<see cref="CollectionsReminderId"/>, <see cref="ExternalEventId"/>) — a
/// unique index — so a redelivered callback is recognized and never recorded
/// twice.
/// </summary>
public class CollectionsReminderEvent
{
    public const int ExternalEventIdMaxLength = 128;
    public const int ConversationIdMaxLength = 128;
    public const int PhoneMaxLength = 32;
    public const int DetailMaxLength = 1000;
    public const int TicketNumberMaxLength = 64;
    public const string ReservedPrefix = "tigercs:";

    public long CollectionsReminderEventId { get; private set; }
    public long CollectionsReminderId { get; private set; }
    public CollectionsReminder? Reminder { get; private set; }

    public string ExternalEventId { get; private set; } = string.Empty;
    public string? IdempotencyKey { get; private set; }
    public string? RequestHash { get; private set; }

    public ReminderChannel? Channel { get; private set; }
    public ChannelStatus? DeliveryStatus { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? ConversationId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? ReportedByEmployeeId { get; private set; }
    public string? Detail { get; private set; }

    public bool CustomerResponded { get; private set; }
    public CustomerIntent? CustomerIntent { get; private set; }
    public string? CustomerPhone { get; private set; }

    /// <summary>Human follow-up must stay outstanding: requested by the caller, or implied by the intent.</summary>
    public bool RequiresHumanFollowUp { get; private set; }

    /// <summary>"I already paid": the claim must be verified against the source. Nothing is posted.</summary>
    public bool VerificationFollowUpRequired { get; private set; }

    public TicketResult TicketResult { get; private set; } = TicketResult.NotRequired;
    public long? TicketId { get; private set; }
    public string? TicketNumber { get; private set; }
    public DateTime? TicketLinkedAtUtc { get; private set; }
    public int TicketAttempts { get; private set; }
    public string? TicketLastError { get; private set; }

    private CollectionsReminderEvent() { }

    internal static CollectionsReminderEvent Internal(
        CollectionsReminder reminder, ReminderChannel channel, string what, int attempt, DateTime atUtc, Guid? actor, string? detail) =>
        new()
        {
            Reminder = reminder,
            ExternalEventId = $"{ReservedPrefix}{what}:{channel}:{attempt}",
            Channel = channel,
            OccurredAtUtc = atUtc,
            RecordedAtUtc = atUtc,
            ReportedByEmployeeId = actor,
            Detail = CollectionsReminder.Truncate(detail, DetailMaxLength)
        };

    /// <summary>
    /// An event reported by the channel (Genesys, the SMS/email provider):
    /// a delivery status, a customer response, or both. A ticket is owed
    /// exactly when the customer responded in a conversation.
    /// </summary>
    public static CollectionsReminderEvent Reported(
        CollectionsReminder reminder,
        string externalEventId,
        string? idempotencyKey,
        string requestHash,
        ReminderChannel channel,
        ChannelStatus? deliveryStatus,
        string? providerMessageId,
        string? conversationId,
        DateTime occurredAtUtc,
        DateTime recordedAtUtc,
        Guid? reportedBy,
        bool customerResponded,
        CustomerIntent? intent,
        bool requiresHumanFollowUp,
        string? customerPhone)
    {
        var id = externalEventId?.Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Length > ExternalEventIdMaxLength || id.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"eventId is required, at most {ExternalEventIdMaxLength} characters, and may not start with '{ReservedPrefix}'.", nameof(externalEventId));
        }

        var conversation = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
        if (conversation is { Length: > ConversationIdMaxLength })
        {
            throw new ArgumentException($"conversationId is at most {ConversationIdMaxLength} characters.", nameof(conversationId));
        }

        if (customerResponded && intent is null)
        {
            throw new ArgumentException("customerIntent is required when the customer responded.", nameof(intent));
        }

        var ticketOwed = customerResponded && conversation is not null;
        return new CollectionsReminderEvent
        {
            Reminder = reminder,
            ExternalEventId = id,
            IdempotencyKey = CollectionsReminder.Truncate(idempotencyKey?.Trim(), CollectionsReminder.IdempotencyKeyMaxLength),
            RequestHash = requestHash,
            Channel = channel,
            DeliveryStatus = deliveryStatus,
            ProviderMessageId = CollectionsReminder.Truncate(providerMessageId?.Trim(), CollectionsReminderChannel.ProviderMessageIdMaxLength),
            ConversationId = conversation,
            OccurredAtUtc = occurredAtUtc,
            RecordedAtUtc = recordedAtUtc,
            ReportedByEmployeeId = reportedBy,
            CustomerResponded = customerResponded,
            CustomerIntent = customerResponded ? intent : null,
            CustomerPhone = CollectionsReminder.Truncate(customerPhone?.Trim(), PhoneMaxLength),
            VerificationFollowUpRequired = customerResponded && intent == Collections.CustomerIntent.AlreadyPaid,
            RequiresHumanFollowUp = customerResponded && (requiresHumanFollowUp
                || intent is Collections.CustomerIntent.RequestedHuman or Collections.CustomerIntent.AiDisconnected
                    or Collections.CustomerIntent.AlreadyPaid or Collections.CustomerIntent.Disputed),
            TicketResult = ticketOwed ? TicketResult.Pending : TicketResult.NotRequired
        };
    }

    public bool FollowUpRequired => RequiresHumanFollowUp || VerificationFollowUpRequired;

    public void RecordTicketAttemptFailed(string error)
    {
        TicketAttempts++;
        TicketLastError = CollectionsReminder.Truncate(error, CollectionsReminder.ReasonMaxLength);
    }

    /// <summary>Links the ticket. Idempotent for the same ticket; a different ticket for a linked event is refused.</summary>
    public void LinkTicket(long ticketId, string ticketNumber, bool created, DateTime atUtc)
    {
        if (TicketResult == TicketResult.NotRequired)
        {
            throw new InvalidOperationException("This event does not owe a ticket.");
        }

        if (TicketId is { } existing)
        {
            if (existing != ticketId)
            {
                throw new InvalidOperationException($"Event is already linked to ticket {existing}.");
            }

            return;
        }

        TicketAttempts++;
        TicketId = ticketId;
        TicketNumber = CollectionsReminder.Truncate(ticketNumber, TicketNumberMaxLength);
        TicketLinkedAtUtc = atUtc;
        TicketResult = created ? TicketResult.Created : TicketResult.Reused;
        TicketLastError = null;
    }
}
