namespace TigerCS.Application.Modules.Collections.Dto;

// Contracts follow TigerCS_Collections_API_Specification.md (5 Oct 2026).
// Fields beyond the specification are marked "extension" and are additive.

// ---------------------------------------------------------------------------
// §3 GET …/customers/{crmCustomerId}/outstanding
// ---------------------------------------------------------------------------

/// <summary>
/// The customer's accounts and their amounts. <c>dataStatus</c>: "Current";
/// "Stale" when any account's figures are older than the freshness window.
/// Extensions: <c>source</c>.
/// </summary>
public sealed record CollectionsOutstandingResponseDto(
    long CrmCustomerId,
    DateOnly BusinessDate,
    DateTime AsOfUtc,
    string DataStatus,
    string Source,
    IReadOnlyList<CollectionsAccountDto> Accounts,
    string? NextCursor);

/// <summary>
/// One finance account. Amounts are null — never zero — when the source's
/// data is invalid (<c>dataStatus: "Invalid"</c>). <c>currentMonthRemainingAmount</c>
/// overlaps the overdue/due-today/future amounts and is never added to them.
/// Extensions: <c>asOfUtc</c>, <c>dataStatus</c> ("Current", "Stale",
/// "Inconsistent" — the schedule disagrees with the source total, reminders
/// refused — or "Invalid"), <c>appliedCreditAmount</c>, <c>problems</c>.
/// </summary>
public sealed record CollectionsAccountDto(
    string AccountId,
    long? UnitId,
    string? TowerName,
    string? UnitNumber,
    string Currency,
    DateTime AsOfUtc,
    string DataStatus,
    decimal? RemainingPrincipalAmount,
    decimal? OverduePrincipalAmount,
    decimal? DueTodayPrincipalAmount,
    decimal? FuturePrincipalAmount,
    decimal? PayablePenaltyAmount,
    decimal? PayableFeeAmount,
    decimal? AppliedCreditAmount,
    decimal? AmountDueNow,
    decimal? CurrentMonthRemainingAmount,
    DateOnly? OldestUnpaidDueDate,
    CollectionsNextPaymentDto? NextPayment,
    IReadOnlyList<string> Problems);

public sealed record CollectionsNextPaymentDto(string InstalmentId, DateOnly DueDate, decimal RemainingAmount);

// ---------------------------------------------------------------------------
// §4 GET …/customers/{crmCustomerId}/payments?view=instalments|history
// ---------------------------------------------------------------------------

/// <summary>view=instalments.</summary>
public sealed record CollectionsInstalmentsResponseDto(
    long CrmCustomerId,
    string AccountId,
    string Currency,
    DateTime AsOfUtc,
    string DataStatus,
    string View,
    IReadOnlyList<CollectionsInstalmentDto> Items,
    string? NextCursor);

/// <summary>
/// Status: Upcoming, DueToday, Overdue, PartiallyPaid or Paid.
/// <c>isOverdue</c> is returned on every row so a PartiallyPaid row still says whether it is overdue.
/// </summary>
public sealed record CollectionsInstalmentDto(
    string InstalmentId,
    DateOnly DueDate,
    decimal ScheduledAmount,
    decimal AllocatedPaidAmount,
    decimal RemainingAmount,
    string Status,
    bool IsOverdue);

/// <summary>view=history — posted payments only. Unverified proof is never listed as a payment.</summary>
public sealed record CollectionsPaymentHistoryResponseDto(
    long CrmCustomerId,
    string AccountId,
    string Currency,
    DateTime AsOfUtc,
    string DataStatus,
    string View,
    IReadOnlyList<CollectionsPaymentDto> Items,
    string? NextCursor);

/// <summary>
/// <c>receiptAvailable</c> is the source's statement that a receipt exists;
/// no download is offered until a verified document API is connected.
/// </summary>
public sealed record CollectionsPaymentDto(
    string PaymentId,
    DateOnly PaymentDate,
    decimal Amount,
    string? Method,
    string Status,
    string? ReceiptNumber,
    bool ReceiptAvailable,
    IReadOnlyList<CollectionsPaymentAllocationDto> Allocations);

/// <summary>Extension: <c>accountId</c>, set when a receipt covering several units allocates to another account.</summary>
public sealed record CollectionsPaymentAllocationDto(string InstalmentId, decimal Amount, string? AccountId);

// ---------------------------------------------------------------------------
// §5 GET …/reminders/candidates
// ---------------------------------------------------------------------------

/// <summary>Extensions: <c>windowOpen</c> (whether the reminder type's window is open on businessDate).</summary>
public sealed record CollectionsReminderCandidatesResponseDto(
    string ReminderType,
    string CycleKey,
    DateOnly BusinessDate,
    string TimeZone,
    bool WindowOpen,
    IReadOnlyList<CollectionsReminderCandidateDto> Items,
    string? NextCursor);

/// <summary>
/// One account due a reminder. <c>candidateId</c> is opaque and expires at
/// <c>expiresAtUtc</c>. Contact details are never exposed;
/// <c>availableChannels</c> lists the enabled channels with an approved
/// contact that have not been used (or may be retried) this cycle.
/// </summary>
public sealed record CollectionsReminderCandidateDto(
    string CandidateId,
    long CrmCustomerId,
    string AccountId,
    long? UnitId,
    string? TowerName,
    string? UnitNumber,
    string Currency,
    decimal ReminderAmount,
    string AmountBasis,
    IReadOnlyList<string> InstalmentIds,
    DateOnly? OldestUnpaidDueDate,
    IReadOnlyList<string> AvailableChannels,
    DateTime AsOfUtc,
    DateTime ExpiresAtUtc);

// ---------------------------------------------------------------------------
// §6 POST …/reminders  (Idempotency-Key header)
// ---------------------------------------------------------------------------

/// <param name="CandidateId">Required. From the candidates list; never a client-supplied balance.</param>
/// <param name="Channels">Required. One or more of VoiceBot, Sms, Email — each from the candidate's availableChannels.</param>
/// <param name="Language">"en" (default) or "ar".</param>
public sealed record QueueCollectionsReminderRequestDto(
    string? CandidateId,
    IReadOnlyList<string>? Channels,
    string? Language = null);

/// <summary>The queued reminder job. Extensions: <c>amountBasis</c>, <c>instalmentIds</c>, <c>channels[].attempts</c>.</summary>
public sealed record CollectionsReminderJobDto(
    string ReminderId,
    long CrmCustomerId,
    string AccountId,
    string ReminderType,
    string CycleKey,
    string Currency,
    decimal ReminderAmount,
    string AmountBasis,
    IReadOnlyList<string> InstalmentIds,
    DateTime QueuedAtUtc,
    string Status,
    IReadOnlyList<CollectionsChannelStatusDto> Channels);

public sealed record CollectionsChannelStatusDto(
    string Channel,
    string Status,
    DateTime? LastEventAtUtc,
    int Attempts,
    string? StatusReason);

// ---------------------------------------------------------------------------
// §7 POST …/reminders/{reminderId}/outcomes  (Idempotency-Key header)
// ---------------------------------------------------------------------------

/// <param name="EventId">Required. The caller's event id — the idempotency key for this event.</param>
/// <param name="Channel">Required. A channel of this reminder.</param>
/// <param name="ProviderMessageId">The channel's own message/call id.</param>
/// <param name="ConversationId">Required for a customer voice response.</param>
/// <param name="OccurredAtUtc">When it happened. Defaults to now.</param>
/// <param name="DeliveryStatus">VoiceBot: Answered, NoAnswer, Failed. Sms/Email: Sent, Delivered, Failed.</param>
/// <param name="CustomerResponded">True only when the customer actually responded — an answered call alone is not a response.</param>
/// <param name="CustomerIntent">PromiseToPay, AlreadyPaid, RequestedHuman, AiDisconnected, Disputed or Other. Required when customerResponded.</param>
/// <param name="RequiresHumanFollowUp">The caller requests human follow-up.</param>
/// <param name="CustomerPhone">Extension: the number reached, so the ticket's customer lookup can run.</param>
public sealed record RecordReminderOutcomeRequestDto(
    string? EventId,
    string? Channel,
    string? ProviderMessageId = null,
    string? ConversationId = null,
    DateTime? OccurredAtUtc = null,
    string? DeliveryStatus = null,
    bool CustomerResponded = false,
    string? CustomerIntent = null,
    bool RequiresHumanFollowUp = false,
    string? CustomerPhone = null);

/// <summary>
/// <c>ticketResult</c>: Created, Reused, NotRequired or Pending (retried durably;
/// the HTTP status is then 202). Extensions: <c>channelStatus</c>,
/// <c>ticketNumber</c>, <c>replayed</c>.
/// </summary>
public sealed record RecordReminderOutcomeResponseDto(
    string ReminderId,
    string EventId,
    string Result,
    string? DeliveryStatus,
    string ChannelStatus,
    long? TicketId,
    string? TicketNumber,
    string TicketResult,
    bool FollowUpRequired,
    bool Replayed);

// ---------------------------------------------------------------------------
// §8 GET …/customers/{crmCustomerId}/reminders
// ---------------------------------------------------------------------------

public sealed record CollectionsReminderHistoryResponseDto(
    long CrmCustomerId,
    string? AccountId,
    IReadOnlyList<CollectionsReminderHistoryItemDto> Items,
    string? NextCursor);

/// <summary>
/// The amount quoted when queued — never the current balance.
/// <c>customerIntent</c>/<c>ticketId</c> are the latest response's.
/// Extensions: <c>accountId</c>, <c>cycleKey</c>, <c>amountBasis</c>,
/// <c>trigger</c>, <c>ticketNumber</c>, <c>responses</c>.
/// </summary>
public sealed record CollectionsReminderHistoryItemDto(
    string ReminderId,
    string AccountId,
    string ReminderType,
    string CycleKey,
    string Currency,
    decimal ReminderAmount,
    string AmountBasis,
    DateTime QueuedAtUtc,
    string Trigger,
    IReadOnlyList<CollectionsChannelStatusDto> Channels,
    string? CustomerIntent,
    long? TicketId,
    string? TicketNumber,
    IReadOnlyList<CollectionsReminderResponseDto> Responses);

public sealed record CollectionsReminderResponseDto(
    string EventId,
    string Channel,
    string CustomerIntent,
    DateTime OccurredAtUtc,
    bool FollowUpRequired,
    bool VerificationFollowUpRequired,
    string TicketResult,
    long? TicketId,
    string? TicketNumber);
