using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Models;

/// <summary>What the Payment tab shows, decided from the Api's answers — never inferred from roles in the browser.</summary>
public enum PaymentPanelState
{
    /// <summary>Not loaded yet: the tab fetches its content when opened (the loading state).</summary>
    Deferred,

    /// <summary>The customer is not identified in Tiger CRM, so there is no finance account to look up.</summary>
    NotCrmCustomer,

    /// <summary>The finance source has no linked account for this customer. No speculative record is shown.</summary>
    NoAccounts,

    /// <summary>Several accounts and none chosen yet: selection is required before any detail is shown.</summary>
    SelectAccount,

    Loaded,

    /// <summary>A PACT customer: EDSM's payment summary only — no instalments, transactions or reminders.</summary>
    EdsmSummary,

    /// <summary>The viewer lacks the Collections financial-read permission.</summary>
    Forbidden,

    /// <summary>Collections is switched off in this environment.</summary>
    Disabled,

    /// <summary>The finance source is unavailable — shown with Retry, never as AED 0.00.</summary>
    Unavailable,

    /// <summary>The Api could not be reached, or answered unexpectedly.</summary>
    Error
}

public sealed class CustomerPaymentPanel
{
    public const string DisabledCode = "CollectionsDisabled";

    public required string CustomerKey { get; init; }
    public long? CrmCustomerId { get; init; }
    public PaymentPanelState State { get; init; }

    public CollectionsOutstandingResponseDto? Outstanding { get; init; }

    /// <summary>EDSM's payment summary, for <see cref="PaymentPanelState.EdsmSummary"/>.</summary>
    public CollectionsPaymentSummaryResponseDto? PaymentSummary { get; init; }

    /// <summary>The PACT customer key prefix (<c>ext:Pact:{tenantID}</c>) — the only identity EDSM's summary can be resolved for.</summary>
    public const string PactKeyPrefix = "ext:Pact:";

    public static bool IsPactCustomer(string customerKey) => customerKey.StartsWith(PactKeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the tab has a source to ask: a CRM customer (finance accounts) or a PACT customer (EDSM summary).</summary>
    public static bool HasPaymentSource(string customerKey, long? crmCustomerId) => crmCustomerId is not null || IsPactCustomer(customerKey);

    /// <summary>
    /// An EDSM summary field exactly as far as it is known: EDSM's own formatted
    /// string when it was read, or why there is none. Never "0.00" for a value
    /// that was not provided; a blank late-fines value is explained per business model.
    /// </summary>
    public static string EdsmField(CollectionsEdsmFieldDto field) => field switch
    {
        { Status: "Provided", Raw: { } raw } => raw.Trim(),
        { Meaning: "ZeroOrLess" } => "None above zero",
        { Meaning: "NotComputedForRented" } => "Not computed for rented companies",
        { Status: "Missing" } => "Not provided",
        { Status: "Empty" } => "Blank in EDSM",
        { Status: "FormatNotConfigured" } => "Not read (EDSM number format not configured)",
        _ => "Not readable"
    };

    public static string EdsmStatusText(string status) => status switch
    {
        "NotSupported" => "EDSM does not support this company",
        "BusinessRuleRejected" => "EDSM refused the request",
        "ValidationRejected" => "EDSM rejected the request parameters",
        "Unauthorized" => "EDSM rejected TigerCS's credentials",
        "InvalidResponse" => "EDSM's response could not be read",
        "Disabled" => "Not enabled",
        "NotMatchable" => "Cannot be matched",
        _ => "EDSM is unavailable"
    };
    public CollectionsAccountDto? SelectedAccount { get; init; }

    public CollectionsInstalmentsResponseDto? Instalments { get; init; }
    public CollectionsPaymentHistoryResponseDto? History { get; init; }
    public CollectionsReminderHistoryResponseDto? Reminders { get; init; }

    /// <summary>Eligible candidates for the selected account (one per open reminder window).</summary>
    public IReadOnlyList<CollectionsReminderCandidateDto> Candidates { get; init; } = [];

    /// <summary>False when the candidates route refused the viewer — the send action is then hidden.</summary>
    public bool CanSend { get; init; }

    /// <summary>A fresh key per rendered form, so a double submit of the same form queues once.</summary>
    public string SendIdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");

    public string? Notice { get; init; }
    public bool NoticeIsError { get; init; }

    public bool IsStale => SelectedAccount?.DataStatus == "Stale" || Outstanding?.DataStatus == "Stale";

    /// <summary>Principal figures arrived. Amount due now may still be null when the source did not report penalties, fees or credits.</summary>
    public bool HasFigures => SelectedAccount?.RemainingPrincipalAmount is not null;

    public bool IsSettled => SelectedAccount is { AmountDueNow: 0m, RemainingPrincipalAmount: 0m };

    /// <summary>Web sends on SMS/email only; the voice bot is dialled by Genesys.</summary>
    public static IReadOnlyList<string> WebChannels(CollectionsReminderCandidateDto candidate) =>
        candidate.AvailableChannels.Where(c => c is "Sms" or "Email").ToList();

    public IReadOnlyList<CollectionsReminderCandidateDto> SendableCandidates =>
        CanSend && !IsStale ? Candidates.Where(c => WebChannels(c).Count > 0).ToList() : [];

    public string AccountLabel(CollectionsAccountDto a) =>
        $"{a.TowerName ?? "—"} · {a.UnitNumber ?? "—"} · {a.AccountId}";

    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{currency} {value.ToString("N2", CultureInfo.InvariantCulture)}" : "Unavailable";

    /// <summary>For a figure on an account whose principal did arrive: null means the source did not report it — said so, never "0.00".</summary>
    public static string Reported(decimal? amount, string currency) =>
        amount is null ? "Not provided by source" : Money(amount, currency);

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Label(string value) => value switch
    {
        "OverdueMonthly" => "Overdue > 1 month",
        "CurrentMonth" => "Current month",
        "MonthEndFollowUp" => "Month-end follow-up",
        "PartiallyPaid" => "Partially paid",
        "DueToday" => "Due today",
        "NoAnswer" => "No answer",
        "VoiceBot" => "Voice bot",
        "Sms" => "SMS",
        "PromiseToPay" => "Promise to pay",
        "AlreadyPaid" => "Says already paid",
        "RequestedHuman" => "Asked for a person",
        "AiDisconnected" => "AI disconnected",
        "UnpaidPrincipalOlderThanOneCalendarMonth" => "unpaid principal older than one calendar month",
        "CurrentMonthUnpaidPrincipal" => "this month's unpaid principal",
        _ => value
    };

    /// <summary>Badge tone (<c>badge-pay-*</c>, built on the semantic palette tokens). Status is always also written as text.</summary>
    public static string StatusCss(string status) => status switch
    {
        "Paid" or "Posted" or "Delivered" or "Answered" or "Created" or "Reused" => "ok",
        "Overdue" or "Failed" or "NoAnswer" => "critical",
        "DueToday" or "Queued" or "Pending" => "pending",
        "Sent" or "PartiallyPaid" => "progress",
        _ => "neutral"
    };

    public static string ApiOutcomeMessage(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden => "You don't have permission to send payment reminders.",
        ApiOutcome.Conflict => "The amount or eligibility changed since this reminder was offered. Review the updated reminder and send again.",
        _ => $"The reminder was not queued: {detail ?? "the ticketing service could not be reached."}"
    };
}
