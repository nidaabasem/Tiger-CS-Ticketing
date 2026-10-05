using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>
/// The figures are sums of what the source reports as remaining, bucketed by
/// due date — never a re-allocation of payments, never reduced by an
/// unverified payment, never zero-filled when the source data is bad.
/// </summary>
public class AccountBalanceCalculatorTests
{
    private static readonly DateOnly BusinessDate = new(2026, 10, 2);

    private static FinancialAccountSnapshot Account(
        IReadOnlyList<FinancialInstalment> instalments,
        IReadOnlyList<FinancialCharge>? charges = null,
        IReadOnlyList<FinancialPayment>? payments = null,
        decimal? reported = null,
        decimal credit = 0m,
        string currency = "AED") =>
        new("ACC-45001", 12345, 45001, "Example Tower", "1205", currency, new DateTime(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc),
            reported, instalments, charges ?? [], payments ?? [], credit);

    /// <summary>The specification's §3 example account.</summary>
    private static FinancialAccountSnapshot SpecExample() => Account(
    [
        new("INST-JUN-2026", new DateOnly(2026, 6, 1), 5_000m, 5_000m),
        new("INST-JUL-2026", new DateOnly(2026, 7, 1), 5_000m, 5_000m),
        new("INST-AUG-2026", new DateOnly(2026, 8, 1), 10_000m, 10_000m),
        new("INST-SEP-2026", new DateOnly(2026, 9, 1), 10_000m, 10_000m),
        new("INST-OCT-2026", new DateOnly(2026, 10, 15), 10_000m, 10_000m),
        new("INST-NOV-2026", new DateOnly(2026, 11, 15), 5_000m, 5_000m),
    ], charges: [new FinancialCharge("PEN-1", FinancialChargeType.Penalty, 500m, 500m, null, true)]);

    [Fact]
    public void ReproducesTheSpecificationsOutstandingExample()
    {
        var b = AccountBalanceCalculator.Calculate(SpecExample(), BusinessDate);

        Assert.Equal(45_000m, b.RemainingPrincipalAmount);
        Assert.Equal(30_000m, b.OverduePrincipalAmount);
        Assert.Equal(0m, b.DueTodayPrincipalAmount);
        Assert.Equal(15_000m, b.FuturePrincipalAmount);
        Assert.Equal(500m, b.PayablePenaltyAmount);
        Assert.Equal(0m, b.PayableFeeAmount);
        Assert.Equal(30_500m, b.AmountDueNow);
        Assert.Equal(10_000m, b.CurrentMonthRemainingAmount);
        Assert.Equal(new DateOnly(2026, 6, 1), b.OldestUnpaidDueDate);
        Assert.Equal(new NextPayment("INST-OCT-2026", new DateOnly(2026, 10, 15), 10_000m), b.NextPayment);
    }

    [Fact]
    public void CurrentMonthRemaining_OverlapsTheBuckets_AndIsNeverAddedToThem()
    {
        var b = AccountBalanceCalculator.Calculate(Account(
        [
            new("A", new DateOnly(2026, 10, 1), 1_000m, 1_000m),   // overdue, this month
            new("B", BusinessDate, 2_000m, 2_000m),                // due today, this month
            new("C", new DateOnly(2026, 10, 31), 3_000m, 3_000m),  // future, this month
        ]), BusinessDate);

        Assert.Equal(6_000m, b.CurrentMonthRemainingAmount);
        Assert.Equal(6_000m, b.OverduePrincipalAmount + b.DueTodayPrincipalAmount + b.FuturePrincipalAmount);
        Assert.Equal(6_000m, b.RemainingPrincipalAmount);
    }

    [Fact]
    public void PartialAllocation_CountsOnlyTheRemainderTheSourceReports()
    {
        var b = AccountBalanceCalculator.Calculate(Account(
        [
            new("A", new DateOnly(2026, 9, 10), 10_000m, 4_000m),
            new("B", new DateOnly(2026, 11, 10), 10_000m, 7_500.25m),
        ]), BusinessDate);

        Assert.Equal(4_000m, b.OverduePrincipalAmount);
        Assert.Equal(7_500.25m, b.FuturePrincipalAmount);
        Assert.Equal(new NextPayment("B", new DateOnly(2026, 11, 10), 7_500.25m), b.NextPayment);
    }

    [Fact]
    public void Payments_OfAnyStatus_NeverReduceTheBalanceASecondTime()
    {
        IReadOnlyList<FinancialInstalment> schedule = [new("A", new DateOnly(2026, 9, 10), 10_000m, 10_000m)];
        var without = AccountBalanceCalculator.Calculate(Account(schedule), BusinessDate);
        var with = AccountBalanceCalculator.Calculate(Account(schedule, payments:
        [
            new FinancialPayment("P1", BusinessDate, 10_000m, "BankTransfer", FinancialPaymentStatus.PendingVerification, null, false, []),
            new FinancialPayment("P2", BusinessDate, 10_000m, "Cheque", FinancialPaymentStatus.Reversed, null, false, []),
            // Even a posted payment is not subtracted: the source already allocated it.
            new FinancialPayment("P3", BusinessDate, 10_000m, "Cheque", FinancialPaymentStatus.Posted, "RCT", true, [new("A", 10_000m)]),
        ]), BusinessDate);

        Assert.Equal(without, with);
    }

    [Fact]
    public void PenaltiesAndFees_AreSeparate_OnlyPayableAndDueOnesCount_AndACreditIsSubtractedOnce()
    {
        var b = AccountBalanceCalculator.Calculate(Account(
            [new("A", new DateOnly(2026, 9, 10), 1_000m, 1_000m)],
            charges:
            [
                new FinancialCharge("P1", FinancialChargeType.Penalty, 500m, 500m, new DateOnly(2026, 9, 1), true),
                new FinancialCharge("F1", FinancialChargeType.Fee, 250m, 100m, null, true),          // partially paid
                new FinancialCharge("F2", FinancialChargeType.Fee, 300m, 300m, null, false),         // on hold
                new FinancialCharge("P2", FinancialChargeType.Penalty, 700m, 700m, new DateOnly(2026, 11, 1), true), // not due
            ],
            credit: 200m), BusinessDate);

        Assert.Equal(500m, b.PayablePenaltyAmount);
        Assert.Equal(100m, b.PayableFeeAmount);
        Assert.Equal(200m, b.AppliedCreditAmount);
        Assert.Equal(1_400m, b.AmountDueNow);           // 1,000 + 500 + 100 − 200
        Assert.Equal(1_000m, b.OverduePrincipalAmount); // the credit never touches principal buckets
    }

    [Fact]
    public void ACreditLargerThanWhatIsDue_NeverMakesAmountDueNowNegative()
    {
        var b = AccountBalanceCalculator.Calculate(Account([new("A", new DateOnly(2026, 9, 10), 1_000m, 1_000m)], credit: 5_000m), BusinessDate);
        Assert.Equal(0m, b.AmountDueNow);
    }

    [Fact]
    public void DecimalArithmetic_IsExact()
    {
        var b = AccountBalanceCalculator.Calculate(Account(
        [
            new("A", new DateOnly(2026, 9, 1), 3_333.33m, 3_333.33m),
            new("B", new DateOnly(2026, 9, 2), 3_333.33m, 3_333.33m),
            new("C", new DateOnly(2026, 9, 3), 3_333.34m, 3_333.34m),
            new("D", new DateOnly(2026, 9, 4), 0.1m, 0.1m),
            new("E", new DateOnly(2026, 9, 5), 0.2m, 0.2m),
        ], reported: 10_000.30m), BusinessDate);

        Assert.Equal(10_000.30m, b.OverduePrincipalAmount);
        Assert.Equal(BalanceConsistency.Consistent, b.Consistency);
    }

    [Fact]
    public void ASourceTotalThatDisagreesWithTheSchedule_IsFlagged_AndTheSourceTotalIsShown()
    {
        var b = AccountBalanceCalculator.Calculate(Account([new("A", new DateOnly(2026, 9, 10), 8_000m, 8_000m)], reported: 12_000m), BusinessDate);

        Assert.Equal(BalanceConsistency.Mismatch, b.Consistency);
        Assert.Equal(12_000m, b.RemainingPrincipalAmount);
        Assert.Single(b.Problems);
    }

    [Theory]
    [InlineData(10_000, 12_000, "AED")]   // more remaining than scheduled
    [InlineData(10_000, -1, "AED")]       // negative
    [InlineData(10_000, 5_000, "aed")]    // not an ISO code
    [InlineData(10_000, 5_000, "")]
    public void InvalidSourceData_YieldsNoFigures_NeverZero(int scheduled, int remaining, string currency)
    {
        var b = AccountBalanceCalculator.Calculate(Account([new("A", new DateOnly(2026, 9, 10), scheduled, remaining)], currency: currency), BusinessDate);

        Assert.Equal(BalanceConsistency.InvalidSourceData, b.Consistency);
        Assert.False(b.HasFigures);
        Assert.Null(b.AmountDueNow);
        Assert.Null(b.RemainingPrincipalAmount);
        Assert.Null(b.NextPayment);
    }
}
