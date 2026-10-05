namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The ordinary payment reminders the Collections FAQ describes. Legal
/// notices and legal referrals are deliberately <b>not</b> members: they are a
/// separate, human-approved process (docs/Collections/Collections-Legal-Requirements.md)
/// and can never be produced by reminder dispatch.
/// </summary>
public enum ReminderType
{
    /// <summary>Days 1–4 of the month: principal overdue for more than one month.</summary>
    OverdueMoreThanOneMonth = 1,

    /// <summary>Day 15: the current month's unpaid payment.</summary>
    CurrentMonthDue = 2,

    /// <summary>Three days before month end: follow-up while the current month's payment is still unsettled.</summary>
    MonthEndFollowUp = 3,

    /// <summary>Sent by an authorized user from the Payment tab, outside the scheduled windows.</summary>
    Manual = 4
}

public enum ReminderChannel
{
    /// <summary>A Genesys outbound voice-bot call. Genesys places the call and reports each delivery event back.</summary>
    VoiceBot = 1,

    Sms = 2,

    Email = 3
}

/// <summary>
/// Reminder delivery state. <b>Customer responses are not a status</b>: a
/// response is an event recorded against the reminder
/// (<see cref="ReminderEventType.CustomerResponded"/>), so "the call was
/// delivered" and "the customer said they already paid" stay separate facts.
/// </summary>
public enum ReminderStatus
{
    /// <summary>Recorded with its amount, waiting for the channel to send it.</summary>
    Queued = 1,

    /// <summary>Handed to the channel (call placed, SMS/email accepted by the provider).</summary>
    Sent = 2,

    /// <summary>The channel confirmed the customer received it (call answered, SMS/email delivered).</summary>
    Delivered = 3,

    /// <summary>The channel could not deliver it.</summary>
    Failed = 4,

    /// <summary>Not sent: revalidation before dispatch found nothing left to remind about (settled), or the source became inconsistent.</summary>
    Suppressed = 5
}

public enum ReminderEventType
{
    Queued = 1,
    Sent = 2,
    Delivered = 3,
    Failed = 4,
    Suppressed = 5,
    CustomerResponded = 6
}

/// <summary>What the customer said in response to a reminder.</summary>
public enum CustomerResponseKind
{
    /// <summary>The customer promised to pay. Recorded; no balance changes.</summary>
    PromiseToPay = 1,

    /// <summary>"I already paid." Opens a verification follow-up. Never posts a payment.</summary>
    AlreadyPaid = 2,

    /// <summary>The customer asked for a human.</summary>
    RequestedHuman = 3,

    /// <summary>The AI lost the call or could not continue.</summary>
    AiDisconnected = 4,

    /// <summary>The customer disputes the amount.</summary>
    Disputed = 5,

    Other = 6
}

/// <summary>Whether a customer response still owes a ticket.</summary>
public enum ResponseTicketStatus
{
    /// <summary>The response did not happen in a conversation (e.g. an SMS reply), so there is no conversation id to create a ticket from.</summary>
    NotApplicable = 1,

    /// <summary>A ticket is owed and not yet linked — retried durably through the outbox.</summary>
    Pending = 2,

    /// <summary>The ticket was created or reused, and linked.</summary>
    Linked = 3
}

public enum OverdueAgeRule
{
    /// <summary>Overdue for more than one calendar month: due before the same day of the previous month (month-end clamped, so 31 March → 28/29 February).</summary>
    CalendarMonth = 1,

    /// <summary>Overdue for more than <see cref="ReminderRuleSettings.OverdueFixedDays"/> days.</summary>
    FixedDays = 2
}

public enum ReminderSendFrequency
{
    /// <summary>At most one reminder per account, type and channel in each monthly window.</summary>
    OncePerWindow = 1,

    /// <summary>At most one per account, type and channel per day while the window is open.</summary>
    Daily = 2
}

/// <summary>
/// The four FAQ points Collections has not confirmed, as explicit settings.
/// The defaults are the conservative reading, and automatic scheduling stays
/// off until Collections confirms them (see CollectionsOptions).
/// </summary>
public sealed record ReminderRuleSettings
{
    public OverdueAgeRule OverdueAgeRule { get; init; } = OverdueAgeRule.CalendarMonth;
    public int OverdueFixedDays { get; init; } = 30;
    public int OverdueWindowFirstDay { get; init; } = 1;
    public int OverdueWindowLastDay { get; init; } = 4;
    public int CurrentMonthDueDay { get; init; } = 15;

    /// <summary>The follow-up runs on (last day of month − this). 3 → 28 Oct, 25 Feb, 26 Feb in a leap year.</summary>
    public int MonthEndOffsetDays { get; init; } = 3;

    public bool IncludeFinesInReminderAmount { get; init; }
    public ReminderSendFrequency SendFrequency { get; init; } = ReminderSendFrequency.OncePerWindow;
}

/// <summary>A window that is open on a given date, with the cycle key that de-duplicates sends inside it.</summary>
public sealed record ReminderWindow(ReminderType Type, string CycleKey);

public sealed record ReminderEligibility(bool IsEligible, decimal? Amount, string Currency, string? Reason)
{
    public static ReminderEligibility Eligible(decimal amount, string currency) => new(true, amount, currency, null);

    public static ReminderEligibility NotEligible(string currency, string reason, decimal? amount = null) => new(false, amount, currency, reason);
}

/// <summary>
/// The FAQ's reminder calendar and eligibility, as pure functions of a date
/// and the source's snapshot. Used identically by the scheduler, the
/// candidates list, manual sends and the revalidation before dispatch.
/// </summary>
public static class ReminderPolicy
{
    public const string SettledReason = "Settled";
    public const string InconsistentReason = "SourceInconsistent";
    public const string NotInWindowReason = "NoOverdueOlderThanOneMonth";

    /// <summary>The scheduled windows open on <paramref name="today"/>. Manual sends are not windowed.</summary>
    public static IReadOnlyList<ReminderWindow> OpenWindows(DateOnly today, ReminderRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var windows = new List<ReminderWindow>();
        var lastDay = DateTime.DaysInMonth(today.Year, today.Month);

        if (today.Day >= settings.OverdueWindowFirstDay && today.Day <= settings.OverdueWindowLastDay)
        {
            windows.Add(new ReminderWindow(ReminderType.OverdueMoreThanOneMonth, CycleKey(ReminderType.OverdueMoreThanOneMonth, today, settings)));
        }

        if (today.Day == settings.CurrentMonthDueDay)
        {
            windows.Add(new ReminderWindow(ReminderType.CurrentMonthDue, CycleKey(ReminderType.CurrentMonthDue, today, settings)));
        }

        if (today.Day == lastDay - settings.MonthEndOffsetDays)
        {
            windows.Add(new ReminderWindow(ReminderType.MonthEndFollowUp, CycleKey(ReminderType.MonthEndFollowUp, today, settings)));
        }

        return windows;
    }

    /// <summary>
    /// The de-duplication cycle: the month for a once-per-window send, the
    /// day for daily sends and for manual sends (one manual reminder per
    /// account and channel per day).
    /// </summary>
    public static string CycleKey(ReminderType type, DateOnly today, ReminderRuleSettings settings) =>
        type == ReminderType.Manual || settings.SendFrequency == ReminderSendFrequency.Daily
            ? today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The cutoff for "overdue for more than one month": an instalment due strictly before it qualifies.</summary>
    public static DateOnly OverdueCutoff(DateOnly today, ReminderRuleSettings settings) =>
        settings.OverdueAgeRule == OverdueAgeRule.FixedDays
            ? today.AddDays(-settings.OverdueFixedDays)
            : today.AddMonths(-1);

    /// <summary>Whether this account should get this reminder today, and for how much.</summary>
    public static ReminderEligibility Evaluate(
        FinancialAccountSnapshot account, ReminderType type, DateOnly today, ReminderRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settings);

        var balance = AccountBalanceCalculator.Calculate(account, today);
        if (balance.Consistency != BalanceConsistency.Consistent)
        {
            return ReminderEligibility.NotEligible(account.Currency, InconsistentReason);
        }

        var fines = settings.IncludeFinesInReminderAmount ? balance.PayableFinesAndFees!.Value : 0m;

        decimal principal;
        switch (type)
        {
            case ReminderType.OverdueMoreThanOneMonth:
                if (AccountBalanceCalculator.OutstandingDueBefore(account, OverdueCutoff(today, settings)) <= 0m)
                {
                    return ReminderEligibility.NotEligible(account.Currency, NotInWindowReason);
                }

                // The customer is asked for everything overdue, not only the
                // part older than a month — the older part is the trigger.
                principal = balance.OverduePrincipal!.Value;
                break;

            case ReminderType.CurrentMonthDue:
            case ReminderType.MonthEndFollowUp:
                principal = balance.CurrentMonthRemaining!.Value;
                break;

            default:
                principal = balance.PrincipalDueThroughMonthEnd!.Value;
                break;
        }

        // Settled: the principal this reminder is about is fully paid. Fines
        // alone never trigger a payment reminder.
        if (principal <= 0m)
        {
            return ReminderEligibility.NotEligible(account.Currency, SettledReason, 0m);
        }

        return ReminderEligibility.Eligible(principal + fines, account.Currency);
    }
}
