namespace TigerCS.Application.Modules.Collections.Dto;

// ---------------------------------------------------------------------------
// GET /api/genesys/collections/customers/{crmCustomerId}/outstanding
// ---------------------------------------------------------------------------

/// <summary>
/// A customer's accounts, balances and instalment schedules, as the authoritative source reports them.
/// <para><c>CrmCustomerId</c>: The Tiger CRM customer.</para>
/// <para><c>Source</c>: Which system the figures came from.</para>
/// <para><c>AsOfUtc</c>: The oldest source timestamp among the returned accounts.</para>
/// <para><c>IsStale</c>: True when any returned account's figures are older than the configured freshness window.</para>
/// <para><c>BusinessDate</c>: "Today" in the business time zone — the date overdue/due-today/future are judged against.</para>
/// <para><c>Accounts</c>: One entry per account (unit / payment plan). Currencies are never mixed or summed across accounts.</para>
/// <para><c>Viewer</c>: What the caller may do.</para>
/// <para><c>Documents</c>: Whether receipt and statement downloads exist.</para>
/// </summary>
public sealed record CollectionsOutstandingResponseDto(
    string CrmCustomerId,
    string Source,
    DateTime AsOfUtc,
    bool IsStale,
    DateOnly BusinessDate,
    IReadOnlyList<CollectionsAccountDto> Accounts,
    CollectionsViewerDto Viewer,
    CollectionsDocumentsDto Documents);

/// <summary>
/// <para><c>Consistency</c>: "Consistent", "Mismatch" (the schedule disagrees with the source's own total — figures shown, reminders refused) or "InvalidSourceData" (no figures shown).</para>
/// <para><c>Balance</c>: Null when the source data is invalid. Never zero-filled.</para>
/// </summary>
public sealed record CollectionsAccountDto(
    string AccountId,
    string? CrmUnitId,
    string? UnitNumber,
    string? ProjectName,
    string Currency,
    DateTime AsOfUtc,
    bool IsStale,
    string Consistency,
    IReadOnlyList<string> Problems,
    CollectionsBalanceDto? Balance,
    CollectionsReminderEligibilityDto ReminderEligibility,
    IReadOnlyList<CollectionsInstalmentDto> Instalments,
    IReadOnlyList<CollectionsChargeDto> Charges);

/// <summary>
/// All amounts are in the account's currency and are sums of what the source reports as outstanding.
/// <para><c>RemainingUnpaidPrincipal</c>: All principal not yet paid.</para>
/// <para><c>OverduePrincipal</c>: Unpaid principal on instalments due before today.</para>
/// <para><c>PrincipalDueToday</c>: Unpaid principal on instalments due today.</para>
/// <para><c>FuturePrincipal</c>: Unpaid principal on instalments due after today.</para>
/// <para><c>PayableFinesAndFees</c>: Fines and fees payable now (not on hold, already due).</para>
/// <para><c>AmountDueNow</c>: Overdue + due today + payable fines and fees.</para>
/// <para><c>CurrentMonthRemaining</c>: Unpaid principal on instalments due in the current calendar month.</para>
/// <para><c>NextPayment</c>: The next instalment with principal outstanding, due today or later.</para>
/// </summary>
public sealed record CollectionsBalanceDto(
    decimal RemainingUnpaidPrincipal,
    decimal OverduePrincipal,
    decimal PrincipalDueToday,
    decimal FuturePrincipal,
    decimal PayableFinesAndFees,
    decimal AmountDueNow,
    decimal CurrentMonthRemaining,
    CollectionsNextPaymentDto? NextPayment);

public sealed record CollectionsNextPaymentDto(string InstalmentId, DateOnly DueDate, decimal Amount);

/// <summary>
/// <para><c>PrincipalPaid</c>: Scheduled minus outstanding — the source's own allocation, shown, never recomputed.</para>
/// <para><c>Status</c>: "Paid", "PartiallyPaid", "Overdue", "DueToday" or "Upcoming".</para>
/// </summary>
public sealed record CollectionsInstalmentDto(
    string InstalmentId,
    int Sequence,
    DateOnly DueDate,
    decimal PrincipalAmount,
    decimal PrincipalPaid,
    decimal PrincipalOutstanding,
    string Status);

public sealed record CollectionsChargeDto(
    string ChargeId,
    string Kind,
    string? Description,
    decimal Amount,
    decimal Outstanding,
    DateOnly? DueDate,
    bool IsPayable);

/// <summary>
/// <para><c>Eligible</c>: Whether a manual reminder could be sent today.</para>
/// <para><c>Amount</c>: The amount it would state.</para>
/// <para><c>Reason</c>: Why not, when not: "Settled", "SourceInconsistent".</para>
/// <para><c>OpenWindows</c>: Scheduled reminder types whose window is open today and for which this account qualifies.</para>
/// </summary>
public sealed record CollectionsReminderEligibilityDto(
    bool Eligible,
    decimal? Amount,
    string? Reason,
    IReadOnlyList<string> OpenWindows);

/// <summary>
/// <para><c>CanSendReminder</c>: The caller holds the reminder permission.</para>
/// <para><c>EnabledChannels</c>: Channels switched on in configuration.</para>
/// </summary>
public sealed record CollectionsViewerDto(bool CanSendReminder, IReadOnlyList<string> EnabledChannels);

public sealed record CollectionsDocumentsDto(bool ReceiptDownloadAvailable, bool StatementDownloadAvailable, string? Reason);

// ---------------------------------------------------------------------------
// GET /api/genesys/collections/customers/{crmCustomerId}/payments
// ---------------------------------------------------------------------------

public sealed record CollectionsPaymentsResponseDto(
    string CrmCustomerId,
    string Source,
    DateTime AsOfUtc,
    bool IsStale,
    IReadOnlyList<CollectionsPaymentDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// <para><c>Status</c>: "Posted", "PendingVerification", "Reversed" or "Rejected".</para>
/// <para><c>CountsTowardBalance</c>: True only for Posted. An unverified proof never reduces a balance.</para>
/// </summary>
public sealed record CollectionsPaymentDto(
    string PaymentId,
    string AccountId,
    string? CrmUnitId,
    string? UnitNumber,
    DateOnly ReceivedOn,
    DateOnly? PostedOn,
    decimal Amount,
    string Currency,
    string? Method,
    string? Reference,
    string Status,
    bool CountsTowardBalance,
    bool ReceiptAvailable);

// ---------------------------------------------------------------------------
// GET /api/genesys/collections/reminders/candidates
// ---------------------------------------------------------------------------

public sealed record CollectionsReminderCandidatesResponseDto(
    DateOnly BusinessDate,
    IReadOnlyList<string> OpenWindows,
    string Source,
    IReadOnlyList<CollectionsReminderCandidateDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    bool Truncated);

/// <summary>
/// <para><c>PendingChannels</c>: Enabled channels that have not yet had this reminder in this cycle.</para>
/// </summary>
public sealed record CollectionsReminderCandidateDto(
    string CrmCustomerId,
    string AccountId,
    string? CrmUnitId,
    string? UnitNumber,
    string? ProjectName,
    string? CustomerName,
    string? CustomerPhone,
    string ReminderType,
    string CycleKey,
    decimal Amount,
    string Currency,
    DateTime SourceAsOfUtc,
    IReadOnlyList<string> PendingChannels);

// ---------------------------------------------------------------------------
// POST /api/genesys/collections/reminders
// ---------------------------------------------------------------------------

/// <summary>
/// <para><c>CrmCustomerId</c>: Required.</para>
/// <para><c>AccountId</c>: Required. Must belong to the customer.</para>
/// <para><c>Channel</c>: Required. "VoiceBot", "Sms" or "Email".</para>
/// <para><c>ReminderType</c>: "OverdueMoreThanOneMonth", "CurrentMonthDue", "MonthEndFollowUp" (only while that window is open) or "Manual" (the default).</para>
/// </summary>
public sealed record CreateCollectionsReminderRequestDto(
    string? CrmCustomerId,
    string? AccountId,
    string? Channel,
    string? ReminderType = null);

/// <summary>
/// <para><c>Outcome</c>: "Created", or "AlreadyExists" when this account/type/cycle/channel already has a reminder — the existing one is returned and nothing is sent.</para>
/// </summary>
public sealed record CreateCollectionsReminderResponseDto(string Outcome, CollectionsReminderDto Reminder);

public sealed record CollectionsReminderDto(
    long ReminderId,
    string CrmCustomerId,
    string AccountId,
    string? CrmUnitId,
    string ReminderType,
    string Channel,
    string CycleKey,
    string Status,
    string? StatusReason,
    decimal Amount,
    string Currency,
    bool AmountIncludesFines,
    DateTime SourceAsOfUtc,
    decimal? DispatchAmount,
    string Trigger,
    DateTime CreatedAtUtc,
    DateTime? SentAtUtc,
    DateTime? DeliveredAtUtc,
    DateTime? FailedAtUtc,
    DateTime? SuppressedAtUtc,
    IReadOnlyList<CollectionsReminderEventDto> Events);

public sealed record CollectionsReminderEventDto(
    long ReminderEventId,
    string EventId,
    string EventType,
    DateTime OccurredAtUtc,
    DateTime RecordedAtUtc,
    string? Detail,
    string? ResponseKind,
    string? ConversationId,
    DateOnly? PromisedPaymentDate,
    decimal? PromisedAmount,
    bool VerificationFollowUpRequired,
    bool HumanFollowUpRequired,
    string? TicketStatus,
    long? TicketId,
    string? TicketNumber);

// ---------------------------------------------------------------------------
// POST /api/genesys/collections/reminders/{reminderId}/outcomes
// ---------------------------------------------------------------------------

/// <summary>
/// <para><c>EventId</c>: Required. The caller's own id for this event — the idempotency key. A resend with the same id records nothing twice.</para>
/// <para><c>Outcome</c>: Required. "Sent", "Delivered", "Failed" or "CustomerResponded".</para>
/// <para><c>OccurredAtUtc</c>: When it happened. Defaults to now.</para>
/// <para><c>ProviderReference</c>: The channel's own message/call id.</para>
/// <para><c>FailureReason</c>: Why delivery failed. Used with "Failed".</para>
/// <para><c>Response</c>: Required with "CustomerResponded".</para>
/// </summary>
public sealed record RecordReminderOutcomeRequestDto(
    string? EventId,
    string? Outcome,
    DateTime? OccurredAtUtc = null,
    string? ProviderReference = null,
    string? FailureReason = null,
    CollectionsCustomerResponseDto? Response = null);

/// <summary>
/// <para><c>Kind</c>: Required. "PromiseToPay", "AlreadyPaid", "RequestedHuman", "AiDisconnected", "Disputed" or "Other".</para>
/// <para><c>ConversationId</c>: The Genesys conversation the response was given in. When present a ticket is created or reused for it; when absent the response is recorded only.</para>
/// <para><c>CustomerPhone</c>: The number reached, as Genesys reports it.</para>
/// <para><c>Note</c>: What the customer said, or the flow's note.</para>
/// <para><c>PromisedPaymentDate</c>: For PromiseToPay.</para>
/// <para><c>PromisedAmount</c>: For PromiseToPay. Recorded only — never posted.</para>
/// </summary>
public sealed record CollectionsCustomerResponseDto(
    string? Kind,
    string? ConversationId = null,
    string? CustomerPhone = null,
    string? Note = null,
    DateOnly? PromisedPaymentDate = null,
    decimal? PromisedAmount = null);

/// <summary>
/// <para><c>Outcome</c>: "Recorded", or "AlreadyRecorded" for a resent event id.</para>
/// <para><c>ReminderStatus</c>: The reminder's delivery status after this event.</para>
/// <para><c>Event</c>: The stored event, including its ticket link.</para>
/// </summary>
public sealed record RecordReminderOutcomeResponseDto(
    string Outcome,
    long ReminderId,
    string ReminderStatus,
    CollectionsReminderEventDto Event);

// ---------------------------------------------------------------------------
// GET /api/genesys/collections/customers/{crmCustomerId}/reminders
// ---------------------------------------------------------------------------

public sealed record CollectionsReminderListResultDto(
    IReadOnlyList<CollectionsReminderDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
