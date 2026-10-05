namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Buckets the authoritative source's outstanding amounts by due date. This
/// is <b>not</b> a second balance calculation: it never allocates a payment,
/// never subtracts the payment history and never invents an amount. Every
/// figure is a sum of <see cref="FinancialInstalment.PrincipalOutstanding"/>
/// or <see cref="FinancialCharge.Outstanding"/> values the source reported,
/// grouped by where their due date falls relative to <c>today</c> (a date in
/// the business time zone).
///
/// <para>
/// When the source also reports its own unpaid-principal total, the schedule
/// is cross-checked against it. A disagreement is reported as
/// <see cref="BalanceConsistency.Mismatch"/> rather than resolved in either
/// direction — TigerCS cannot know which of the two is wrong, so it shows the
/// source's total and refuses to send reminders on that account until the
/// source is consistent again.
/// </para>
/// </summary>
public static class AccountBalanceCalculator
{
    public static AccountBalance Calculate(FinancialAccountSnapshot account, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(account);

        var problems = Validate(account);
        if (problems.Count > 0)
        {
            return AccountBalance.Invalid(account.Currency, problems);
        }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        decimal overdue = 0m, dueToday = 0m, future = 0m, currentMonth = 0m, throughMonthEnd = 0m;
        FinancialInstalment? next = null;

        foreach (var instalment in account.Instalments)
        {
            var outstanding = instalment.PrincipalOutstanding;
            if (outstanding == 0m)
            {
                continue;
            }

            if (instalment.DueDate < today)
            {
                overdue += outstanding;
            }
            else if (instalment.DueDate == today)
            {
                dueToday += outstanding;
            }
            else
            {
                future += outstanding;
            }

            if (instalment.DueDate >= monthStart && instalment.DueDate <= monthEnd)
            {
                currentMonth += outstanding;
            }

            if (instalment.DueDate <= monthEnd)
            {
                throughMonthEnd += outstanding;
            }

            if (instalment.DueDate >= today
                && (next is null
                    || instalment.DueDate < next.DueDate
                    || (instalment.DueDate == next.DueDate && instalment.Sequence < next.Sequence)))
            {
                next = instalment;
            }
        }

        var payableCharges = account.Charges
            .Where(c => c.IsPayable && c.Outstanding > 0m && (c.DueDate is null || c.DueDate <= today))
            .Sum(c => c.Outstanding);

        var remaining = overdue + dueToday + future;

        var consistency = account.ReportedOutstandingPrincipal is { } reported && reported != remaining
            ? BalanceConsistency.Mismatch
            : BalanceConsistency.Consistent;

        return new AccountBalance(
            Currency: account.Currency,
            // The source's own total wins when it reports one; on a mismatch
            // it is still the figure shown, flagged as inconsistent.
            RemainingUnpaidPrincipal: account.ReportedOutstandingPrincipal ?? remaining,
            OverduePrincipal: overdue,
            PrincipalDueToday: dueToday,
            FuturePrincipal: future,
            PayableFinesAndFees: payableCharges,
            AmountDueNow: overdue + dueToday + payableCharges,
            CurrentMonthRemaining: currentMonth,
            PrincipalDueThroughMonthEnd: throughMonthEnd,
            NextPayment: next is null ? null : new NextPayment(next.InstalmentId, next.DueDate, next.PrincipalOutstanding),
            Consistency: consistency,
            Problems: consistency == BalanceConsistency.Mismatch
                ? [$"The source reports {account.ReportedOutstandingPrincipal} {account.Currency} unpaid principal but its instalment schedule sums to {remaining} {account.Currency}."]
                : []);
    }

    /// <summary>
    /// Unpaid principal on instalments that fell due strictly before
    /// <paramref name="cutoff"/> — the "overdue for more than N" test the
    /// reminder windows use. Same source values, same no-allocation rule.
    /// </summary>
    public static decimal OutstandingDueBefore(FinancialAccountSnapshot account, DateOnly cutoff) =>
        account.Instalments.Where(i => i.DueDate < cutoff).Sum(i => i.PrincipalOutstanding);

    private static List<string> Validate(FinancialAccountSnapshot account)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(account.Currency) || account.Currency.Length != 3 || !account.Currency.All(char.IsAsciiLetterUpper))
        {
            problems.Add($"Currency '{account.Currency}' is not an ISO 4217 code.");
        }

        foreach (var instalment in account.Instalments)
        {
            if (instalment.PrincipalAmount < 0m || instalment.PrincipalOutstanding < 0m || instalment.PrincipalOutstanding > instalment.PrincipalAmount)
            {
                problems.Add($"Instalment {instalment.InstalmentId} reports {instalment.PrincipalOutstanding} outstanding of {instalment.PrincipalAmount}.");
            }
        }

        foreach (var charge in account.Charges)
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

        return problems;
    }
}

public enum BalanceConsistency
{
    /// <summary>The schedule agrees with the source's own total (or the source reports no separate total).</summary>
    Consistent = 1,

    /// <summary>The schedule and the source's total disagree. Shown, flagged, and never reminded on.</summary>
    Mismatch = 2,

    /// <summary>The source returned values that cannot be right (negative, more outstanding than charged, no currency). Nothing is shown as a figure.</summary>
    InvalidSourceData = 3
}

/// <summary>The next instalment that still has principal outstanding, due today or later.</summary>
public sealed record NextPayment(string InstalmentId, DateOnly DueDate, decimal Amount);

/// <summary>An account's figures, all in <see cref="Currency"/>, all derived only from what the source reported.</summary>
public sealed record AccountBalance(
    string Currency,
    decimal? RemainingUnpaidPrincipal,
    decimal? OverduePrincipal,
    decimal? PrincipalDueToday,
    decimal? FuturePrincipal,
    decimal? PayableFinesAndFees,
    decimal? AmountDueNow,
    decimal? CurrentMonthRemaining,
    decimal? PrincipalDueThroughMonthEnd,
    NextPayment? NextPayment,
    BalanceConsistency Consistency,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Bad source data yields no figures at all — null, never zero. A zero
    /// would read as "nothing owed", which is the one wrong answer that
    /// could stop a legitimate collection or misinform a customer.
    /// </summary>
    public static AccountBalance Invalid(string currency, IReadOnlyList<string> problems) =>
        new(currency, null, null, null, null, null, null, null, null, null, BalanceConsistency.InvalidSourceData, problems);
}
