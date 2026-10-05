using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Models;

/// <summary>What the Payment tab can show, decided from the Api's answer — never inferred from roles in the browser.</summary>
public enum PaymentPanelState
{
    /// <summary>Not loaded yet: the tab fetches its content when opened (the loading state).</summary>
    Deferred,

    /// <summary>The customer is not identified in Tiger CRM, so there is no account to look up.</summary>
    NotCrmCustomer,

    Loaded,

    /// <summary>The viewer lacks the Collections financial-read permission.</summary>
    Forbidden,

    /// <summary>Collections is switched off in this environment.</summary>
    Disabled,

    /// <summary>The financial source is unavailable. The balance is shown as unavailable — never as zero.</summary>
    SourceUnavailable,

    /// <summary>The financial source has no record of this CRM customer.</summary>
    NoAccounts,

    /// <summary>The Api could not be reached, or answered unexpectedly.</summary>
    Error
}

public sealed class CustomerPaymentPanel
{
    public const string CollectionsDisabledProblem = "https://tigercs.internal/problems/collections-disabled";

    public required string CustomerKey { get; init; }
    public string? CrmCustomerId { get; init; }
    public PaymentPanelState State { get; init; }
    public string? StateDetail { get; init; }

    public CollectionsOutstandingResponseDto? Outstanding { get; init; }
    public CollectionsAccountDto? SelectedAccount { get; init; }

    public CollectionsPaymentsResponseDto? Payments { get; init; }
    public ApiOutcome PaymentsOutcome { get; init; }

    public CollectionsReminderListResultDto? Reminders { get; init; }
    public ApiOutcome RemindersOutcome { get; init; }

    public string? Notice { get; init; }
    public bool NoticeIsError { get; init; }

    public bool IsStale => SelectedAccount?.IsStale ?? Outstanding?.IsStale ?? false;

    /// <summary>Channels Web may send on. The voice bot is dialled by Genesys, not from here.</summary>
    public IReadOnlyList<string> SendChannels =>
        Outstanding?.Viewer.EnabledChannels.Where(c => c != "VoiceBot").ToList() ?? [];

    /// <summary>The button shows only to a permitted viewer, for an eligible account, with a channel to send on.</summary>
    public bool CanSendReminder =>
        Outstanding?.Viewer.CanSendReminder == true
        && SelectedAccount?.ReminderEligibility.Eligible == true
        && SendChannels.Count > 0;

    public bool DocumentsAvailable =>
        Outstanding?.Documents is { ReceiptDownloadAvailable: true } or { StatementDownloadAvailable: true };

    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{currency} {value.ToString("N2", CultureInfo.InvariantCulture)}" : "Unavailable";

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Label(string value) => value switch
    {
        "OverdueMoreThanOneMonth" => "Overdue > 1 month",
        "CurrentMonthDue" => "Current month due",
        "MonthEndFollowUp" => "Month-end follow-up",
        "PendingVerification" => "Pending verification",
        "PartiallyPaid" => "Partially paid",
        "DueToday" => "Due today",
        "VoiceBot" => "Voice bot",
        "Sms" => "SMS",
        "CustomerResponded" => "Customer responded",
        "PromiseToPay" => "Promise to pay",
        "AlreadyPaid" => "Says already paid",
        "RequestedHuman" => "Asked for a person",
        "AiDisconnected" => "AI disconnected",
        _ => value
    };

    /// <summary>Badge tone for a status (<c>badge-pay-*</c>, built on the semantic palette tokens).</summary>
    public static string StatusCss(string status) => status switch
    {
        "Paid" or "Posted" or "Delivered" or "Linked" => "ok",
        "Overdue" or "Failed" or "Reversed" or "Rejected" => "critical",
        "DueToday" or "PendingVerification" or "Queued" or "Pending" => "pending",
        "Sent" or "PartiallyPaid" => "progress",
        _ => "neutral"
    };
}
