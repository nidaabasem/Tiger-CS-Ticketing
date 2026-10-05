namespace TigerCS.Domain.Modules.Collections;

public enum ReminderTrigger
{
    /// <summary>The designated scheduler opened a window and found the account eligible.</summary>
    Scheduled = 1,

    /// <summary>An authorized user pressed Send Reminder.</summary>
    Manual = 2,

    /// <summary>An integration (the Genesys outbound campaign) recorded the reminder before placing the call.</summary>
    Integration = 3
}

/// <summary>
/// One payment reminder for one account, of one type, in one cycle, on one
/// channel — that combination is <see cref="DeduplicationKey"/>, unique in the
/// database, which is what makes a second send in the same window impossible
/// rather than merely unlikely.
///
/// <para>
/// The amount told to the customer is persisted at the moment it was
/// decided (<see cref="Amount"/>) and again when it was re-checked
/// immediately before sending (<see cref="DispatchAmount"/>), each with the
/// source timestamp it came from — so what a customer was told can always be
/// reconstructed, even after the source's figures move on.
/// </para>
/// </summary>
public class CollectionsReminder
{
    public const int IdentifierMaxLength = 64;
    public const int CycleKeyMaxLength = 10;
    public const int DeduplicationKeyMaxLength = 256;
    public const int ReasonMaxLength = 500;
    public const int ProviderReferenceMaxLength = 128;

    public long CollectionsReminderId { get; private set; }

    public string CrmCustomerId { get; private set; } = string.Empty;
    public string AccountId { get; private set; } = string.Empty;
    public string? CrmUnitId { get; private set; }

    public ReminderType Type { get; private set; }
    public ReminderChannel Channel { get; private set; }
    public string CycleKey { get; private set; } = string.Empty;
    public string DeduplicationKey { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public bool AmountIncludesFines { get; private set; }
    public DateTime SourceAsOfUtc { get; private set; }

    public decimal? DispatchAmount { get; private set; }
    public DateTime? DispatchSourceAsOfUtc { get; private set; }

    public ReminderStatus Status { get; private set; }
    public string? StatusReason { get; private set; }
    public string? ProviderReference { get; private set; }

    public ReminderTrigger Trigger { get; private set; }
    public Guid? RequestedByEmployeeId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }
    public DateTime? FailedAtUtc { get; private set; }
    public DateTime? SuppressedAtUtc { get; private set; }

    private readonly List<CollectionsReminderEvent> _events = [];
    public IReadOnlyCollection<CollectionsReminderEvent> Events => _events;

    private CollectionsReminder() { }

    public CollectionsReminder(
        string crmCustomerId,
        string accountId,
        string? crmUnitId,
        ReminderType type,
        ReminderChannel channel,
        string cycleKey,
        string currency,
        decimal amount,
        bool amountIncludesFines,
        DateTime sourceAsOfUtc,
        ReminderTrigger trigger,
        Guid? requestedByEmployeeId,
        DateTime createdAtUtc)
    {
        CrmCustomerId = Required(crmCustomerId, nameof(crmCustomerId));
        AccountId = Required(accountId, nameof(accountId));
        CrmUnitId = string.IsNullOrWhiteSpace(crmUnitId) ? null : Required(crmUnitId, nameof(crmUnitId));

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (string.IsNullOrWhiteSpace(cycleKey) || cycleKey.Length > CycleKeyMaxLength)
        {
            throw new ArgumentException("A cycle key is required.", nameof(cycleKey));
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new ArgumentException("Currency must be an ISO 4217 code.", nameof(currency));
        }

        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A reminder is only ever recorded for an amount actually owed.");
        }

        Type = type;
        Channel = channel;
        CycleKey = cycleKey;
        DeduplicationKey = BuildDeduplicationKey(AccountId, type, cycleKey, channel);
        Currency = currency;
        Amount = amount;
        AmountIncludesFines = amountIncludesFines;
        SourceAsOfUtc = sourceAsOfUtc;
        Trigger = trigger;
        RequestedByEmployeeId = requestedByEmployeeId;
        CreatedAtUtc = createdAtUtc;
        Status = ReminderStatus.Queued;

        _events.Add(CollectionsReminderEvent.Internal(this, ReminderEventType.Queued, createdAtUtc, requestedByEmployeeId, null));
    }

    /// <summary>account | type | cycle | channel — the duplicate-prevention identity.</summary>
    public static string BuildDeduplicationKey(string accountId, ReminderType type, string cycleKey, ReminderChannel channel) =>
        $"{accountId.Trim()}|{type}|{cycleKey}|{channel}";

    /// <summary>The figures re-read from the source immediately before sending.</summary>
    public void RecordDispatchRevalidation(decimal amount, DateTime sourceAsOfUtc)
    {
        DispatchAmount = amount;
        DispatchSourceAsOfUtc = sourceAsOfUtc;
    }

    /// <summary>Revalidation found nothing left to remind about. Only a queued reminder can be suppressed.</summary>
    public bool Suppress(string reason, DateTime atUtc)
    {
        if (Status != ReminderStatus.Queued)
        {
            return false;
        }

        Status = ReminderStatus.Suppressed;
        StatusReason = Truncate(reason, ReasonMaxLength);
        SuppressedAtUtc = atUtc;
        _events.Add(CollectionsReminderEvent.Internal(this, ReminderEventType.Suppressed, atUtc, null, StatusReason));
        return true;
    }

    /// <summary>
    /// Applies a delivery event. Each event moves the status forward only:
    /// a late or redelivered "Sent" never undoes "Delivered", and a
    /// suppressed reminder accepts nothing. Returns whether the status moved.
    /// </summary>
    public bool ApplyDelivery(ReminderEventType eventType, DateTime atUtc, string? detail, string? providerReference)
    {
        if (Status == ReminderStatus.Suppressed)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(providerReference))
        {
            ProviderReference ??= Truncate(providerReference.Trim(), ProviderReferenceMaxLength);
        }

        switch (eventType)
        {
            case ReminderEventType.Sent when Status == ReminderStatus.Queued:
                Status = ReminderStatus.Sent;
                SentAtUtc = atUtc;
                return true;

            case ReminderEventType.Delivered when Status is ReminderStatus.Queued or ReminderStatus.Sent or ReminderStatus.Failed:
                Status = ReminderStatus.Delivered;
                SentAtUtc ??= atUtc;
                DeliveredAtUtc = atUtc;
                StatusReason = null;
                return true;

            case ReminderEventType.Failed when Status is ReminderStatus.Queued or ReminderStatus.Sent:
                Status = ReminderStatus.Failed;
                FailedAtUtc = atUtc;
                StatusReason = Truncate(detail, ReasonMaxLength);
                return true;

            default:
                return false;
        }
    }

    public CollectionsReminderEvent AddEvent(CollectionsReminderEvent reminderEvent)
    {
        ArgumentNullException.ThrowIfNull(reminderEvent);
        _events.Add(reminderEvent);
        return reminderEvent;
    }

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > IdentifierMaxLength)
        {
            throw new ArgumentException($"{name} is required and at most {IdentifierMaxLength} characters.", name);
        }

        return value.Trim();
    }

    internal static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// One thing that happened to a reminder: a delivery event, or a customer
/// response. Idempotent on (<see cref="CollectionsReminderId"/>,
/// <see cref="ExternalEventId"/>) — a unique index — so a redelivered
/// callback is recognized and never recorded twice.
/// </summary>
public class CollectionsReminderEvent
{
    public const int ExternalEventIdMaxLength = 128;
    public const int ConversationIdMaxLength = 128;
    public const int PhoneMaxLength = 32;
    public const int NoteMaxLength = 1000;
    public const int TicketNumberMaxLength = 64;

    public long CollectionsReminderEventId { get; private set; }
    public long CollectionsReminderId { get; private set; }
    public CollectionsReminder? Reminder { get; private set; }

    public string ExternalEventId { get; private set; } = string.Empty;
    public ReminderEventType EventType { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? ReportedByEmployeeId { get; private set; }
    public string? Detail { get; private set; }

    public CustomerResponseKind? ResponseKind { get; private set; }
    public string? ConversationId { get; private set; }
    public string? CustomerPhone { get; private set; }
    public DateOnly? PromisedPaymentDate { get; private set; }
    public decimal? PromisedAmount { get; private set; }

    /// <summary>"I already paid": a human must verify the claim against the source. Nothing is posted.</summary>
    public bool VerificationFollowUpRequired { get; private set; }

    /// <summary>The customer asked for a person, or the AI dropped: human follow-up must stay outstanding.</summary>
    public bool HumanFollowUpRequired { get; private set; }

    public ResponseTicketStatus? TicketStatus { get; private set; }
    public long? TicketId { get; private set; }
    public string? TicketNumber { get; private set; }
    public DateTime? TicketLinkedAtUtc { get; private set; }
    public int TicketAttempts { get; private set; }
    public string? TicketLastError { get; private set; }

    private CollectionsReminderEvent() { }

    internal static CollectionsReminderEvent Internal(
        CollectionsReminder reminder, ReminderEventType type, DateTime atUtc, Guid? actor, string? detail) =>
        new()
        {
            Reminder = reminder,
            ExternalEventId = $"tigercs:{type.ToString().ToLowerInvariant()}",
            EventType = type,
            OccurredAtUtc = atUtc,
            RecordedAtUtc = atUtc,
            ReportedByEmployeeId = actor,
            Detail = CollectionsReminder.Truncate(detail, NoteMaxLength)
        };

    /// <summary>TigerCS's own dispatch outcome (the provider accepted, or permanently refused, the message). Recorded at most once per type.</summary>
    public static CollectionsReminderEvent Dispatched(CollectionsReminder reminder, ReminderEventType type, DateTime atUtc, string? detail) =>
        type is ReminderEventType.Sent or ReminderEventType.Failed
            ? Internal(reminder, type, atUtc, null, detail)
            : throw new ArgumentOutOfRangeException(nameof(type));

    /// <summary>A delivery event reported by the channel (Genesys, the SMS/email provider).</summary>
    public static CollectionsReminderEvent Delivery(
        CollectionsReminder reminder, string externalEventId, ReminderEventType type,
        DateTime occurredAtUtc, DateTime recordedAtUtc, Guid? reportedBy, string? detail)
    {
        if (type is not (ReminderEventType.Sent or ReminderEventType.Delivered or ReminderEventType.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Only Sent, Delivered and Failed are delivery events.");
        }

        return new CollectionsReminderEvent
        {
            Reminder = reminder,
            ExternalEventId = ExternalId(externalEventId),
            EventType = type,
            OccurredAtUtc = occurredAtUtc,
            RecordedAtUtc = recordedAtUtc,
            ReportedByEmployeeId = reportedBy,
            Detail = CollectionsReminder.Truncate(detail, NoteMaxLength)
        };
    }

    /// <summary>
    /// A customer response. A ticket is owed exactly when the response came
    /// from a conversation (a conversation id is what the Genesys ingestion
    /// is idempotent on); otherwise the response is recorded and nothing
    /// else.
    /// </summary>
    public static CollectionsReminderEvent Response(
        CollectionsReminder reminder, string externalEventId, CustomerResponseKind kind,
        DateTime occurredAtUtc, DateTime recordedAtUtc, Guid? reportedBy,
        string? conversationId, string? customerPhone, string? note,
        DateOnly? promisedPaymentDate, decimal? promisedAmount)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (promisedAmount is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(promisedAmount), "A promised amount must be positive.");
        }

        var conversation = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
        if (conversation is { Length: > ConversationIdMaxLength })
        {
            throw new ArgumentException($"conversationId is at most {ConversationIdMaxLength} characters.", nameof(conversationId));
        }

        return new CollectionsReminderEvent
        {
            Reminder = reminder,
            ExternalEventId = ExternalId(externalEventId),
            EventType = ReminderEventType.CustomerResponded,
            OccurredAtUtc = occurredAtUtc,
            RecordedAtUtc = recordedAtUtc,
            ReportedByEmployeeId = reportedBy,
            ResponseKind = kind,
            ConversationId = conversation,
            CustomerPhone = CollectionsReminder.Truncate(customerPhone?.Trim(), PhoneMaxLength),
            Detail = CollectionsReminder.Truncate(note, NoteMaxLength),
            PromisedPaymentDate = promisedPaymentDate,
            PromisedAmount = promisedAmount,
            VerificationFollowUpRequired = kind == CustomerResponseKind.AlreadyPaid,
            HumanFollowUpRequired = kind is CustomerResponseKind.RequestedHuman or CustomerResponseKind.AiDisconnected
                or CustomerResponseKind.AlreadyPaid or CustomerResponseKind.Disputed,
            TicketStatus = conversation is null ? ResponseTicketStatus.NotApplicable : ResponseTicketStatus.Pending
        };
    }

    public void RecordTicketAttemptFailed(string error)
    {
        TicketAttempts++;
        TicketLastError = CollectionsReminder.Truncate(error, CollectionsReminder.ReasonMaxLength);
    }

    /// <summary>Links the ticket. Idempotent for the same ticket; a different ticket for a linked response is refused.</summary>
    public void LinkTicket(long ticketId, string ticketNumber, DateTime atUtc)
    {
        if (TicketStatus != ResponseTicketStatus.Pending && TicketStatus != ResponseTicketStatus.Linked)
        {
            throw new InvalidOperationException("This response does not owe a ticket.");
        }

        if (TicketId is { } existing)
        {
            if (existing != ticketId)
            {
                throw new InvalidOperationException($"Response is already linked to ticket {existing}.");
            }

            return;
        }

        TicketAttempts++;
        TicketId = ticketId;
        TicketNumber = CollectionsReminder.Truncate(ticketNumber, TicketNumberMaxLength);
        TicketLinkedAtUtc = atUtc;
        TicketStatus = ResponseTicketStatus.Linked;
        TicketLastError = null;
    }

    private static string ExternalId(string externalEventId)
    {
        if (string.IsNullOrWhiteSpace(externalEventId) || externalEventId.Trim().Length > ExternalEventIdMaxLength)
        {
            throw new ArgumentException($"eventId is required and at most {ExternalEventIdMaxLength} characters.", nameof(externalEventId));
        }

        var id = externalEventId.Trim();
        if (id.StartsWith("tigercs:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("eventId may not use the reserved 'tigercs:' prefix.", nameof(externalEventId));
        }

        return id;
    }
}
