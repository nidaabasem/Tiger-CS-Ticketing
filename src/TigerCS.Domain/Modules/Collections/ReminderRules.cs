using System.Globalization;

namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The three ordinary payment reminders the Collections FAQ describes. Legal
/// notices and legal case referrals are deliberately <b>not</b> members: they
/// need a separately approved workflow (docs/Collections/Collections-Legal-Requirements.md)
/// and can never be produced by reminder dispatch.
/// </summary>
public enum ReminderType
{
    /// <summary>1st–4th: unpaid principal overdue more than one month.</summary>
    OverdueMonthly = 1,

    /// <summary>15th: the current month's unpaid payment.</summary>
    CurrentMonth = 2,

    /// <summary>Three days before month end: the current month's payment is still unpaid.</summary>
    MonthEndFollowUp = 3
}

public enum ReminderChannel
{
    /// <summary>A Genesys outbound voice-bot call. Genesys places the call and reports each event back; TigerCS never dials.</summary>
    VoiceBot = 1,

    Sms = 2,

    Email = 3
}

/// <summary>
/// One channel's delivery state. Each channel has its own vocabulary — voice
/// is Answered / NoAnswer / Failed, SMS and email are Sent / Delivered /
/// Failed — and <b>a customer response is not a status</b>: an answered call
/// does not mean the customer responded.
/// </summary>
public enum ChannelStatus
{
    Queued = 1,
    Sent = 2,
    Delivered = 3,
    Answered = 4,
    NoAnswer = 5,
    Failed = 6,

    /// <summary>Not sent: revalidation before dispatch found the account settled, ineligible or inconsistent.</summary>
    Suppressed = 7
}

/// <summary>What the customer said.</summary>
public enum CustomerIntent
{
    PromiseToPay = 1,

    /// <summary>"I already paid." Opens a verification follow-up. Never posts a payment.</summary>
    AlreadyPaid = 2,

    RequestedHuman = 3,

    /// <summary>The AI lost the call or could not continue.</summary>
    AiDisconnected = 4,

    Disputed = 5,

    Other = 6
}

/// <summary>What happened to the ticket a reminder event may owe.</summary>
public enum TicketResult
{
    /// <summary>No customer response in a conversation, so no ticket is owed.</summary>
    NotRequired = 1,

    /// <summary>A ticket is owed and not yet linked — retried durably.</summary>
    Pending = 2,

    /// <summary>A new ticket was created for the conversation.</summary>
    Created = 3,

    /// <summary>The conversation already had a ticket; it was reused.</summary>
    Reused = 4
}

public enum OverdueAgeRule
{
    /// <summary>Due strictly before businessDate.AddMonths(-1) (month-end clamped: 31 Mar → 28/29 Feb).</summary>
    CalendarMonth = 1,

    /// <summary>Due strictly before businessDate minus <see cref="ReminderRuleSettings.OverdueFixedDays"/>.</summary>
    FixedDays = 2
}

public enum ReminderSendFrequency
{
    /// <summary>At most one reminder per account, type and channel in each monthly window (failed deliveries may be retried).</summary>
    OncePerWindow = 1,

    /// <summary>At most one per account, type and channel per day while the window is open.</summary>
    Daily = 2
}

/// <summary>
/// The open FAQ points as explicit settings. The defaults are the
/// specification's proposed drafts; automatic scheduling stays off until
/// Collections confirms them.
/// </summary>
public sealed record ReminderRuleSettings
{
    public OverdueAgeRule OverdueAgeRule { get; init; } = OverdueAgeRule.CalendarMonth;
    public int OverdueFixedDays { get; init; } = 30;
    public int OverdueWindowFirstDay { get; init; } = 1;
    public int OverdueWindowLastDay { get; init; } = 4;
    public int CurrentMonthDay { get; init; } = 15;

    /// <summary>The follow-up runs on (last day of month − this): 28 Oct, 27 Nov, 25 Feb, 26 Feb in a leap year.</summary>
    public int MonthEndOffsetDays { get; init; } = 3;

    /// <summary>Draft: reminders quote qualifying principal only; penalties and fees are shown separately in balance enquiries.</summary>
    public bool IncludePenaltiesAndFees { get; init; }

    public ReminderSendFrequency SendFrequency { get; init; } = ReminderSendFrequency.OncePerWindow;
}

/// <summary>A reminder window open on a business date, with the cycle key that de-duplicates sends inside it.</summary>
public sealed record ReminderWindow(ReminderType Type, string CycleKey);

/// <summary>Whether an account qualifies for a reminder, for how much, why, and from which instalments.</summary>
public sealed record ReminderEligibility(
    bool IsEligible,
    decimal Amount,
    string Currency,
    string AmountBasis,
    IReadOnlyList<string> InstalmentIds,
    DateOnly? OldestUnpaidDueDate,
    string? Reason)
{
    public static ReminderEligibility NotEligible(string currency, string reason) =>
        new(false, 0m, currency, string.Empty, [], null, reason);
}

/// <summary>
/// The FAQ's reminder calendar and eligibility, as pure functions of the
/// business date and the source's snapshot. Used identically by the
/// candidates list, the queue's revalidation, dispatch and the scheduler.
/// </summary>
public static class ReminderPolicy
{
    public const string SettledReason = "Settled";
    public const string InconsistentReason = "SourceInconsistent";
    public const string NothingOldEnoughReason = "NoUnpaidPrincipalOlderThanOneMonth";
    public const string ChargesNotReportedReason = "PenaltiesAndFeesNotReported";

    /// <summary>The windows open on <paramref name="businessDate"/>.</summary>
    public static IReadOnlyList<ReminderWindow> OpenWindows(DateOnly businessDate, ReminderRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var windows = new List<ReminderWindow>();
        var lastDay = DateTime.DaysInMonth(businessDate.Year, businessDate.Month);

        if (businessDate.Day >= settings.OverdueWindowFirstDay && businessDate.Day <= settings.OverdueWindowLastDay)
        {
            windows.Add(new ReminderWindow(ReminderType.OverdueMonthly, CycleKey(ReminderType.OverdueMonthly, businessDate, settings)));
        }

        if (businessDate.Day == settings.CurrentMonthDay)
        {
            windows.Add(new ReminderWindow(ReminderType.CurrentMonth, CycleKey(ReminderType.CurrentMonth, businessDate, settings)));
        }

        if (businessDate.Day == lastDay - settings.MonthEndOffsetDays)
        {
            windows.Add(new ReminderWindow(ReminderType.MonthEndFollowUp, CycleKey(ReminderType.MonthEndFollowUp, businessDate, settings)));
        }

        return windows;
    }

    /// <summary>"2026-10:OverdueMonthly" (once per window) or "2026-10-02:OverdueMonthly" (daily), in the Dubai calendar.</summary>
    public static string CycleKey(ReminderType type, DateOnly businessDate, ReminderRuleSettings settings) =>
        (settings.SendFrequency == ReminderSendFrequency.Daily
            ? businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : businessDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)) + ":" + type;

    /// <summary>An instalment due strictly before this date is "overdue more than one month".</summary>
    public static DateOnly OverdueCutoff(DateOnly businessDate, ReminderRuleSettings settings) =>
        settings.OverdueAgeRule == OverdueAgeRule.FixedDays
            ? businessDate.AddDays(-settings.OverdueFixedDays)
            : businessDate.AddMonths(-1);

    public static ReminderEligibility Evaluate(
        FinancialAccountSnapshot account, ReminderType type, DateOnly businessDate, ReminderRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settings);

        var balance = AccountBalanceCalculator.Calculate(account, businessDate);
        if (balance.Consistency != BalanceConsistency.Consistent)
        {
            return ReminderEligibility.NotEligible(account.Currency, InconsistentReason);
        }

        IReadOnlyList<FinancialInstalment> qualifying;
        string basis;
        if (type == ReminderType.OverdueMonthly)
        {
            qualifying = AccountBalanceCalculator.UnpaidDueBefore(account, OverdueCutoff(businessDate, settings));
            basis = settings.OverdueAgeRule == OverdueAgeRule.FixedDays
                ? $"UnpaidPrincipalOlderThan{settings.OverdueFixedDays}Days"
                : "UnpaidPrincipalOlderThanOneCalendarMonth";
            if (qualifying.Count == 0)
            {
                return ReminderEligibility.NotEligible(account.Currency,
                    balance.RemainingPrincipalAmount == 0m ? SettledReason : NothingOldEnoughReason);
            }
        }
        else
        {
            qualifying = AccountBalanceCalculator.UnpaidInMonth(account, businessDate);
            basis = "CurrentMonthUnpaidPrincipal";
            if (qualifying.Count == 0)
            {
                // Paid for this month: suppressed. Penalties alone never trigger a payment reminder.
                return ReminderEligibility.NotEligible(account.Currency, SettledReason);
            }
        }

        var amount = qualifying.Sum(i => i.RemainingAmount);
        if (settings.IncludePenaltiesAndFees)
        {
            if (balance.PayablePenaltyAmount is not { } penalties || balance.PayableFeeAmount is not { } fees)
            {
                // The configured amount includes charges the source did not report: no amount can be stated.
                return ReminderEligibility.NotEligible(account.Currency, ChargesNotReportedReason);
            }

            amount += penalties + fees;
            basis += "PlusPayablePenaltiesAndFees";
        }

        return new ReminderEligibility(true, amount, account.Currency, basis,
            qualifying.Select(i => i.InstalmentId).ToList(), balance.OldestUnpaidDueDate, null);
    }

    /// <summary>The delivery statuses a channel may report.</summary>
    public static bool IsValidDeliveryStatus(ReminderChannel channel, ChannelStatus status) => channel switch
    {
        ReminderChannel.VoiceBot => status is ChannelStatus.Answered or ChannelStatus.NoAnswer or ChannelStatus.Failed,
        _ => status is ChannelStatus.Sent or ChannelStatus.Delivered or ChannelStatus.Failed
    };

    /// <summary>A delivery that reached the customer — final; nothing later moves it.</summary>
    public static bool IsSuccessTerminal(ChannelStatus status) => status is ChannelStatus.Delivered or ChannelStatus.Answered;
}
