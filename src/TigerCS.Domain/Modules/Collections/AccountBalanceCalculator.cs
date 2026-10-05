namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Buckets the authoritative source's outstanding amounts by due date. This
/// is <b>not</b> a second balance calculation: it never allocates a payment,
/// never subtracts the payment history and never invents an amount. Every
/// figure is a sum of <see cref="FinancialInstalment.RemainingAmount"/> or
/// <see cref="FinancialCharge.Outstanding"/> values the source reported,
/// grouped by where their due date falls relative to <c>businessDate</c>
/// (a date in Asia/Dubai).
///
/// <para>
/// When the source also reports its own unpaid-principal total, the schedule
/// is cross-checked against it. A disagreement is reported as
/// <see cref="BalanceConsistency.Mismatch"/> rather than resolved in either
/// direction: TigerCS shows the source's total and refuses reminders on that
/// account until the source is consistent again.
/// </para>
/// </summary>
public static class AccountBalanceCalculator
{
    public static AccountBalance Calculate(FinancialAccountSnapshot account, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(account);

        var problems = Validate(account);
        if (problems.Count > 0)
        {
            return AccountBalance.Invalid(account.Currency, problems);
        }

        var monthStart = new DateOnly(businessDate.Year, businessDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        decimal overdue = 0m, dueToday = 0m, future = 0m, currentMonth = 0m;
        FinancialInstalment? next = null;
        DateOnly? oldestUnpaid = null;

        foreach (var instalment in account.Instalments)
        {
            var remaining = instalment.RemainingAmount;
            if (remaining == 0m)
            {
                continue;
            }

            if (instalment.DueDate < businessDate)
            {
                overdue += remaining;
            }
            else if (instalment.DueDate == businessDate)
            {
                dueToday += remaining;
            }
            else
            {
                future += remaining;
            }

            // Overlaps the three buckets above by design and is never added to them.
            if (instalment.DueDate >= monthStart && instalment.DueDate <= monthEnd)
            {
                currentMonth += remaining;
            }

            if (oldestUnpaid is null || instalment.DueDate < oldestUnpaid)
            {
                oldestUnpaid = instalment.DueDate;
            }

            if (instalment.DueDate >= businessDate && (next is null || instalment.DueDate < next.DueDate))
            {
                next = instalment;
            }
        }

        // Charges the source did not report stay unknown (null): never "no penalties".
        var payable = account.Charges?
            .Where(c => c.IsPayable && c.Outstanding > 0m && (c.DueDate is null || c.DueDate <= businessDate))
            .ToList();
        decimal? penalties = payable?.Where(c => c.Type == FinancialChargeType.Penalty).Sum(c => c.Outstanding);
        decimal? fees = payable?.Where(c => c.Type == FinancialChargeType.Fee).Sum(c => c.Outstanding);

        var remainingPrincipal = overdue + dueToday + future;
        var consistency = account.ReportedOutstandingPrincipal is { } reported && reported != remainingPrincipal
            ? BalanceConsistency.Mismatch
            : BalanceConsistency.Consistent;

        // A source-applied credit reduces what is due now, once; it never
        // goes below zero and never touches the principal buckets, which are
        // the source's own allocated figures. If any component was not
        // reported, amount due now is unknown — not a smaller number.
        decimal? dueNow = penalties is { } p && fees is { } f && account.AppliedCreditAmount is { } credit
            ? Math.Max(0m, overdue + dueToday + p + f - credit)
            : null;

        return new AccountBalance(
            Currency: account.Currency,
            RemainingPrincipalAmount: account.ReportedOutstandingPrincipal ?? remainingPrincipal,
            OverduePrincipalAmount: overdue,
            DueTodayPrincipalAmount: dueToday,
            FuturePrincipalAmount: future,
            PayablePenaltyAmount: penalties,
            PayableFeeAmount: fees,
            AppliedCreditAmount: account.AppliedCreditAmount,
            AmountDueNow: dueNow,
            CurrentMonthRemainingAmount: currentMonth,
            OldestUnpaidDueDate: oldestUnpaid,
            NextPayment: next is null ? null : new NextPayment(next.InstalmentId, next.DueDate, next.RemainingAmount),
            Consistency: consistency,
            Problems: consistency == BalanceConsistency.Mismatch
                ? [$"The source reports {account.ReportedOutstandingPrincipal} {account.Currency} unpaid principal but its instalment schedule sums to {remainingPrincipal} {account.Currency}."]
                : []);
    }

    /// <summary>
    /// The unpaid instalments that fell due strictly before <paramref name="cutoff"/>
    /// — the reminder age test. Examined per instalment, never inferred from
    /// the oldest unpaid date alone.
    /// </summary>
    public static IReadOnlyList<FinancialInstalment> UnpaidDueBefore(FinancialAccountSnapshot account, DateOnly cutoff) =>
        account.Instalments.Where(i => i.RemainingAmount > 0m && i.DueDate < cutoff).OrderBy(i => i.DueDate).ToList();

    /// <summary>The unpaid instalments due in <paramref name="businessDate"/>'s calendar month, whether already due or upcoming.</summary>
    public static IReadOnlyList<FinancialInstalment> UnpaidInMonth(FinancialAccountSnapshot account, DateOnly businessDate) =>
        account.Instalments
            .Where(i => i.RemainingAmount > 0m && i.DueDate.Year == businessDate.Year && i.DueDate.Month == businessDate.Month)
            .OrderBy(i => i.DueDate)
            .ToList();

    private static List<string> Validate(FinancialAccountSnapshot account)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(account.Currency) || account.Currency.Length != 3 || !account.Currency.All(char.IsAsciiLetterUpper))
        {
            problems.Add($"Currency '{account.Currency}' is not an ISO 4217 code.");
        }

        foreach (var instalment in account.Instalments)
        {
            if (instalment.ScheduledAmount < 0m || instalment.RemainingAmount < 0m || instalment.RemainingAmount > instalment.ScheduledAmount)
            {
                problems.Add($"Instalment {instalment.InstalmentId} reports {instalment.RemainingAmount} remaining of {instalment.ScheduledAmount}.");
            }
        }

        foreach (var charge in account.Charges ?? [])
        {
            if (charge.Amount < 0m || charge.Outstanding < 0m || charge.Outstanding > charge.Amount)
            {
                problems.Add($"Charge {charge.ChargeId} reports {charge.Outstanding} outstanding of {charge.Amount}.");
            }
        }

        if (account.ReportedOutstandingPrincipal is < 0m)
        {
            problems.Add("The reported outstanding principal is negative.");
        }

        if (account.AppliedCreditAmount < 0m)
        {
            problems.Add("The applied credit is negative.");
        }

        return problems;
    }
}

public enum BalanceConsistency
{
    /// <summary>The schedule agrees with the source's own total (or the source reports no separate total).</summary>
    Consistent = 1,

    /// <summary>The schedule and the source's total disagree. Shown, flagged, and never reminded on.</summary>
    Mismatch = 2,

    /// <summary>The source returned values that cannot be right. No figure is shown.</summary>
    InvalidSourceData = 3
}

/// <summary>The next instalment that still has principal remaining, due on or after the business date.</summary>
public sealed record NextPayment(string InstalmentId, DateOnly DueDate, decimal RemainingAmount);

/// <summary>An account's figures, all in <see cref="Currency"/>, all derived only from what the source reported. Null — never zero — when the source data is invalid.</summary>
public sealed record AccountBalance(
    string Currency,
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
    NextPayment? NextPayment,
    BalanceConsistency Consistency,
    IReadOnlyList<string> Problems)
{
    public bool HasFigures => Consistency != BalanceConsistency.InvalidSourceData;

    public static AccountBalance Invalid(string currency, IReadOnlyList<string> problems) =>
        new(currency, null, null, null, null, null, null, null, null, null, null, null, BalanceConsistency.InvalidSourceData, problems);
}
