using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>
/// The balance figures are sums of what the source reports as outstanding,
/// bucketed by due date — never a re-allocation of payments, never reduced by
/// an unverified payment, never zero-filled when the source data is bad.
/// </summary>
public class AccountBalanceCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static FinancialAccountSnapshot Account(
        IReadOnlyList<FinancialInstalment> instalments,
        IReadOnlyList<FinancialCharge>? charges = null,
        IReadOnlyList<FinancialPayment>? payments = null,
        decimal? reported = null,
        string currency = "AED") =>
        new("ACC-1", "9001", "9200", "1204", "Tiger Tower A", currency, new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc),
            reported, instalments, charges ?? [], payments ?? []);

    private static FinancialInstalment Ins(string id, int seq, DateOnly due, decimal amount, decimal outstanding) =>
        new(id, seq, due, amount, outstanding);

    [Fact]
    public void BucketsOutstandingPrincipal_ByDueDateRelativeToToday()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
        [
            Ins("A", 1, new DateOnly(2026, 8, 10), 10_000m, 0m),          // paid
            Ins("B", 2, new DateOnly(2026, 9, 10), 10_000m, 10_000m),     // overdue
            Ins("C", 3, new DateOnly(2026, 10, 10), 10_000m, 10_000m),    // overdue, this month
            Ins("D", 4, Today, 5_000m, 5_000m),                           // due today, this month
            Ins("E", 5, new DateOnly(2026, 10, 31), 2_000m, 2_000m),      // future, this month
            Ins("F", 6, new DateOnly(2026, 11, 10), 10_000m, 10_000m),    // future
        ]), Today);

        Assert.Equal(BalanceConsistency.Consistent, balance.Consistency);
        Assert.Equal(37_000m, balance.RemainingUnpaidPrincipal);
        Assert.Equal(20_000m, balance.OverduePrincipal);
        Assert.Equal(5_000m, balance.PrincipalDueToday);
        Assert.Equal(12_000m, balance.FuturePrincipal);
        Assert.Equal(17_000m, balance.CurrentMonthRemaining);
        Assert.Equal(27_000m, balance.PrincipalDueThroughMonthEnd);
        Assert.Equal(25_000m, balance.AmountDueNow);
        Assert.Equal(new NextPayment("D", Today, 5_000m), balance.NextPayment);
    }

    [Fact]
    public void PartialPayment_CountsOnlyTheRemainderTheSourceReports()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
        [
            Ins("A", 1, new DateOnly(2026, 9, 10), 10_000m, 4_000m),
            Ins("B", 2, new DateOnly(2026, 11, 10), 10_000m, 7_500.25m),
        ]), Today);

        Assert.Equal(4_000m, balance.OverduePrincipal);
        Assert.Equal(7_500.25m, balance.FuturePrincipal);
        Assert.Equal(11_500.25m, balance.RemainingUnpaidPrincipal);
        Assert.Equal(new NextPayment("B", new DateOnly(2026, 11, 10), 7_500.25m), balance.NextPayment);
    }

    [Fact]
    public void UnverifiedRejectedAndReversedPayments_NeverReduceTheBalance()
    {
        IReadOnlyList<FinancialInstalment> schedule = [Ins("A", 1, new DateOnly(2026, 9, 10), 10_000m, 10_000m)];
        var without = AccountBalanceCalculator.Calculate(Account(schedule), Today);
        var with = AccountBalanceCalculator.Calculate(Account(schedule, payments:
        [
            new FinancialPayment("P1", Today, null, 10_000m, "BankTransfer", "upload", FinancialPaymentStatus.PendingVerification),
            new FinancialPayment("P2", Today, null, 10_000m, "Cheque", "CHQ", FinancialPaymentStatus.Reversed),
            new FinancialPayment("P3", Today, null, 10_000m, "Cheque", "CHQ", FinancialPaymentStatus.Rejected),
            // Even a Posted payment is not subtracted here: the source has
            // already applied it to the instalment it reports.
            new FinancialPayment("P4", Today, Today, 10_000m, "Cheque", "CHQ", FinancialPaymentStatus.Posted),
        ]), Today);

        Assert.Equal(without, with);
        Assert.Equal(10_000m, with.AmountDueNow);
    }

    [Fact]
    public void FinesAndFees_OnlyPayableAndAlreadyDueOnesAreDueNow()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
            [Ins("A", 1, new DateOnly(2026, 11, 10), 10_000m, 10_000m)],
            charges:
            [
                new FinancialCharge("F1", "Fine", null, 500m, 500m, new DateOnly(2026, 10, 1), IsPayable: true),
                new FinancialCharge("F2", "Fee", null, 250m, 100m, null, IsPayable: true),       // partially paid fee
                new FinancialCharge("F3", "Fee", null, 300m, 300m, null, IsPayable: false),      // on hold
                new FinancialCharge("F4", "Fine", null, 700m, 700m, new DateOnly(2026, 11, 1), IsPayable: true), // not due yet
                new FinancialCharge("F5", "Fine", null, 900m, 0m, null, IsPayable: true),        // paid
            ]), Today);

        Assert.Equal(600m, balance.PayableFinesAndFees);
        Assert.Equal(600m, balance.AmountDueNow);
        Assert.Equal(0m, balance.OverduePrincipal);
    }

    [Fact]
    public void DecimalArithmetic_IsExact()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
        [
            Ins("A", 1, new DateOnly(2026, 9, 1), 3_333.33m, 3_333.33m),
            Ins("B", 2, new DateOnly(2026, 9, 2), 3_333.33m, 3_333.33m),
            Ins("C", 3, new DateOnly(2026, 9, 3), 3_333.34m, 3_333.34m),
            Ins("D", 4, new DateOnly(2026, 9, 4), 0.1m, 0.1m),
            Ins("E", 5, new DateOnly(2026, 9, 5), 0.2m, 0.2m),
        ], reported: 10_000.30m), Today);

        Assert.Equal(10_000.30m, balance.OverduePrincipal);
        Assert.Equal(BalanceConsistency.Consistent, balance.Consistency);
    }

    [Fact]
    public void SourceTotalThatDisagreesWithTheSchedule_IsFlaggedAndTheSourceTotalIsShown()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
            [Ins("A", 1, new DateOnly(2026, 9, 10), 8_000m, 8_000m), Ins("B", 2, new DateOnly(2026, 11, 10), 8_000m, 8_000m)],
            reported: 12_000m), Today);

        Assert.Equal(BalanceConsistency.Mismatch, balance.Consistency);
        Assert.Equal(12_000m, balance.RemainingUnpaidPrincipal);
        Assert.Single(balance.Problems);
    }

    [Theory]
    [InlineData(10_000, 12_000, "AED")]   // more outstanding than scheduled
    [InlineData(10_000, -1, "AED")]       // negative
    [InlineData(10_000, 5_000, "aed")]    // not an ISO code
    [InlineData(10_000, 5_000, "")]
    public void InvalidSourceData_YieldsNoFigures_NeverZero(int amount, int outstanding, string currency)
    {
        var balance = AccountBalanceCalculator.Calculate(
            Account([Ins("A", 1, new DateOnly(2026, 9, 10), amount, outstanding)], currency: currency), Today);

        Assert.Equal(BalanceConsistency.InvalidSourceData, balance.Consistency);
        Assert.Null(balance.AmountDueNow);
        Assert.Null(balance.RemainingUnpaidPrincipal);
        Assert.Null(balance.OverduePrincipal);
        Assert.Null(balance.NextPayment);
        Assert.NotEmpty(balance.Problems);
    }

    [Fact]
    public void FullySettledAccount_HasZeroDueAndNoNextPayment()
    {
        var balance = AccountBalanceCalculator.Calculate(Account(
            [Ins("A", 1, new DateOnly(2026, 9, 10), 10_000m, 0m), Ins("B", 2, new DateOnly(2026, 11, 10), 10_000m, 0m)],
            reported: 0m), Today);

        Assert.Equal(0m, balance.RemainingUnpaidPrincipal);
        Assert.Equal(0m, balance.AmountDueNow);
        Assert.Null(balance.NextPayment);
    }
}
