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

    public bool HasFigures => SelectedAccount?.AmountDueNow is not null;

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
