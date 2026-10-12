using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Tests.Collections.Statement;

/// <summary>
/// The per-unit Payment Summary (Due | Outstanding | Paid | Fines | Total + Payment Details). SYNTHETIC data only: it proves the rules (unit isolation, nothing invented, missing is never zero,
/// cancelled units never shown), not that production PACT returns what a given customer expects.
/// </summary>
public sealed class UnitPaymentStatementTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    private static PactContractDto Contract(string code, int company = 4, DateOnly? ends = null, string? type = null) =>
        new(code, "C-" + code, code[(code.IndexOf('-') + 1)..], "Tower", type, company, ends, code);

    private static PactContractDiscoveryResult Found(string tenant, string phone, int failed = 0, params PactContractDto[] contracts) =>
        new([(phone, new PactCustomerMatchDto(tenant, "Customer", phone, null, null, contracts))], 1, failed);

    private static UnitStatementRequest Request(string key, string? tenant = null) => new(4, key, tenant, ["+971500000001"]);

    [Fact]
    public void AnAccountThatHoldsOnlyTheSelectedUnit_IsProven_AndAskedForThatTenant()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909")), Request("TP140-909", "100"), Today);
        Assert.Null(scope.Withheld);
        Assert.Equal((4, "100", "971500000001"), (scope.Account!.CompanyId, scope.Account.TenantId, scope.Account.Mobile));
    }

    [Fact]
    public void WhenTheCustomerOwnsSeveralUnits_NoStatementIsShownForOne_AndTheOtherUnitsAreNamed()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909"), Contract("TP140-407"), Contract("TP115-PK1", type: "Parking")), Request("TP140-909", "100"), Today);
        Assert.Null(scope.Account);
        Assert.Equal("NotExclusive", scope.Withheld!.Status);
        Assert.Contains("TP140-407", scope.Withheld.OtherUnits);
        Assert.Null(scope.Withheld.Total);                       // no amount of another unit can reach the screen
        Assert.Null(scope.Withheld.Paid);
    }

    [Fact]
    public void ACancelledSibling_WithAnAsterisk_StillWithholds_BecauseTheAccountTotalMayIncludeIt()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909"), Contract("TP140-407*")), Request("TP140-909"), Today);
        Assert.Equal("NotExclusive", scope.Withheld!.Status);
    }

    [Fact]
    public void AnIncompleteContractAnswer_CannotProveExclusivity_SoNothingIsShown()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 1, Contract("TP140-909")), Request("TP140-909"), Today);
        Assert.Equal("Unverified", scope.Withheld!.Status);
    }

    [Fact]
    public void ATenantThatDiffersFromTheSnapshot_IsAnIdentityMismatch()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909")), Request("TP140-909", "999"), Today);
        Assert.Equal("IdentityMismatch", scope.Withheld!.Status);
    }

    [Fact]
    public void AnEndedContract_BelongsToAFormerTenant_AndShowsNothing()
    {
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909", ends: new(2026, 1, 1))), Request("TP140-909"), Today);
        Assert.Equal("ContractEnded", scope.Withheld!.Status);
    }

    [Fact]
    public void TwoAccountsClaimingOneUnit_AreAmbiguous_NoneIsChosen()
    {
        var two = new PactContractDiscoveryResult([("+971500000001", new PactCustomerMatchDto("100", "A", null, null, null, [Contract("TP140-909")])),
                                                   ("+971500000002", new PactCustomerMatchDto("200", "B", null, null, null, [Contract("TP140-909")]))], 2, 0);
        Assert.Equal("Ambiguous", CollectionsUnitStatementService.Resolve(two, Request("TP140-909"), Today).Withheld!.Status);
    }

    [Theory]
    [InlineData("TP140-909*")]
    [InlineData("TP140-9*09")]
    [InlineData("TP140-**909")]
    [InlineData("*TP140-909")]
    public void AUnitCodeWithOneOrMoreAsterisks_IsCancelled_NoStatement_AndTheMarkIsNeverStrippedToReachAnActiveUnit(string code)
    {
        // The active unit TP140-909 exists, but a starred request must NOT be matched to it.
        var scope = CollectionsUnitStatementService.Resolve(Found("100", "+971500000001", 0, Contract("TP140-909")), Request(code), Today);
        Assert.Null(scope.Account);
        Assert.Equal("UnitNotResolved", scope.Withheld!.Status);
    }

    private static CollectionsEdsmFieldDto Field(string key, string status, decimal? value, string? raw = null) => new(key, key, "def", status, value, raw ?? value?.ToString("N2"), null);

    private static CollectionsCompanyPaymentSummaryDto Read(string model, IReadOnlyList<CollectionsEdsmFieldDto> fields, params CollectionsEdsmTransactionListDto[] lists) =>
        new(4, "Company", model, "Available", null, [], fields, "Consistent", false, [], lists, null);

    private static CollectionsEdsmTransactionListDto List(string type, params CollectionsEdsmTransactionDto[] items) => new(type, "Available", null, null, items);
    private static CollectionsEdsmTransactionDto Tx(decimal amount, int y, int m, int d, string? cheque = null) => new(amount, "Provided", null, new(y, m, d), null, cheque, null);

    private static CollectionsUnitStatementService.ProvenAccount Account() => new(4, "100", "971500000001", []);

    [Fact]
    public void TheStatement_CarriesTheSourceFiguresAsStated_AndPaidUnpaidAndFutureRowsWithPartialSplits()
    {
        var read = Read("Owned",
        [
            Field("dueAmount", "Provided", 0m), Field("outstandingAmount", "Provided", 889393.44m), Field("paidAmount", "Provided", 594467.56m),
            Field("lateFines", "Empty", null), Field("totalAmount", "Provided", 1483861.00m),
        ],
        List("Paid", Tx(100000m, 2026, 1, 5, "CHQ-1"), Tx(50000m, 2026, 2, 5, "CHQ-2"), Tx(25000m, 2026, 2, 5, "CHQ-3")),    // a split on one date stays two rows
        List("Due"),
        List("Outstanding", Tx(300000m, 2027, 1, 1, "CHQ-9")));                                                              // a future row

        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);

        Assert.True(s.IsAvailable);
        Assert.Equal(889393.44m, s.Outstanding!.Value);
        Assert.Equal(594467.56m, s.Paid!.Value);
        Assert.Equal(0m, s.Due!.Value);
        Assert.Equal(1483861.00m, s.Total!.Value);
        Assert.Equal(("ZeroOrLess", 0m), (s.Fines!.Status, s.Fines.Value));
        Assert.Equal(4, s.Rows.Count);
        Assert.Equal(["Paid", "Paid", "Paid", "Outstanding"], s.Rows.Select(r => r.Status));
        Assert.Equal(2, s.Rows.Count(r => r.Date == new DateOnly(2026, 2, 5)));
        Assert.Contains(s.Rows, r => r.Status == "Outstanding" && r.Date == new DateOnly(2027, 1, 1) && r.ChequeNumber == "CHQ-9");
        Assert.True(s.RowsComplete);
    }

    [Fact]
    public void AMissingFigure_IsUnavailable_NeverZero_AndNothingIsDerived()
    {
        var read = Read("Owned", [Field("paidAmount", "Provided", 10m), Field("dueAmount", "Missing", null), Field("totalAmount", "Unreadable", null, "garbage")]);
        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);
        Assert.Equal(10m, s.Paid!.Value);
        Assert.Null(s.Due!.Value);
        Assert.Null(s.Total!.Value);                             // not Paid + Due + Outstanding
        Assert.Null(s.Outstanding);
    }

    [Fact]
    public void WhenNoFigureCanBeRead_TheStatementSaysSo_InsteadOfShowingZeros()
    {
        var read = Read("Owned", [Field("paidAmount", "FormatNotConfigured", null, "1.234,50"), Field("dueAmount", "FormatNotConfigured", null)]);
        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);
        Assert.Equal("AmountsUnreadable", s.Status);
        Assert.False(s.IsAvailable);
    }

    [Fact]
    public void ARentedCompany_NeverComputesFines_AndLabelsOutstandingAsPostDatedCheques()
    {
        var read = Read("Rented", [Field("paidAmount", "Provided", 1m), Field("lateFines", "Empty", null)]);
        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);
        Assert.Equal(("NotComputed", null), (s.Fines!.Status, s.Fines.Value));
        Assert.Equal("Post-dated cheques", s.OutstandingLabel);
    }

    [Fact]
    public void ALoadFailureOfOneList_IsReported_AndTheRowsAreMarkedIncomplete()
    {
        var read = Read("Owned", [Field("paidAmount", "Provided", 1m)], List("Paid", Tx(1m, 2026, 1, 1)), new CollectionsEdsmTransactionListDto("Due", "Unavailable", "down", null, []));
        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);
        Assert.False(s.RowsComplete);
        Assert.Contains(s.Notes, n => n.Contains("Due"));
    }

    [Fact]
    public void ASourceFailure_WithholdsTheWholeStatement()
    {
        var read = Read("Owned", []) with { Status = "Unavailable", StatusDetail = "timeout" };
        var s = CollectionsUnitStatementService.Compose("TP140-909", Account(), read, "Edsm", "AED", DateTime.UtcNow);
        Assert.Equal("SourceUnavailable", s.Status);
        Assert.Null(s.Total);
    }
}
