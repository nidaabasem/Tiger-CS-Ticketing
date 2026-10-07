using System.Runtime.CompilerServices;
using System.Text.Json;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// <b>Fixture tests, not EDSM verification.</b> They prove TigerCS's own rules — the semantics gate,
/// the forward windowed search, the unpaid-status filter, overdue exclusion and the explicit
/// statuses — against rows in EDSM's wire shape that a fake gateway returns. They do not show what any
/// real due-installments row means; that is the unresolved dependency in
/// docs/Collections/EDSM-Instalment-Semantics.md, and a UAT run with known figures is still owed.
/// </summary>
public class CollectionsNextPaymentServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 8);
    private const long Tenant = 3001;

    private sealed class FakeDue : IEdsmCollectionsGateway
    {
        public List<(int Company, DateOnly From, DateOnly To)> Calls { get; } = [];
        public List<EdsmDueInstallment> Rows { get; } = [];
        public EdsmOutcome Outcome { get; set; } = EdsmOutcome.Success;
        public Action? OnCall { get; set; }
        public string SourceName => "Fake";

        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
            int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
        {
            Calls.Add((companyId, fromDate, toDate));
            OnCall?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Outcome != EdsmOutcome.Success)
            {
                return Task.FromResult(EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Fail(Outcome, "down"));
            }

            // A real EDSM answers company-wide for the range; the fake honours the range, inclusive both ends.
            IReadOnlyList<EdsmDueInstallment> inRange = Rows
                .Where(r => r.CompanyId == companyId && r.ChequeDueDate >= fromDate && r.ChequeDueDate <= toDate).ToList();
            return Task.FromResult(EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Ok(inRange));
        }

        public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int c, string t, CancellationToken ct = default) =>
            throw new InvalidOperationException("The next-payment path must not read the summary.");

        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
            int c, string t, string m, EdsmTransactionType type, CancellationToken ct = default) =>
            throw new InvalidOperationException("The next-payment path must not read transactions.");
    }

    private static NextPaymentSemanticsAttestation Confirmed() => new()
    {
        UnpaidStatusValues = ["Pending"],
        AmountRepresents = NextPaymentSemanticsAttestation.RemainingUnpaid,
        ChequeDueDateIsInstalmentDueDate = true,
        ListsEveryUnpaidInstalment = true,
        CurrencyConfirmed = true,
        ConfirmedBy = "EDSM owner (fixture)",
        ConfirmedOn = new DateOnly(2026, 10, 1),
        EvidenceReference = "fixture-only"
    };

    private static EdsmDueInstallment Row(int company, int daysFromToday, decimal amount, string status = "Pending",
        long tenant = Tenant, int? unit = 678, string voucher = "V1") =>
        new(company, tenant, unit, voucher, "000123", Today.AddDays(daysFromToday), amount, status);

    private readonly FakeDue _edsm = new();
    private readonly CollectionsEdsmOptions _options = new() { Currency = "AED" };

    private CollectionsNextPaymentService Service() =>
        new(_options, new CollectionsClock(new CollectionsOptions(), new FakeTimeProvider(Now)), _edsm);

    private Task<CollectionsEdsmNextPaymentDto> Resolve(params int[] companies) =>
        Service().ResolveAsync(companies, Tenant, CancellationToken.None, () => false);

    private void Enable(params int[] companies)
    {
        _options.NextPayment.Enabled = true;
        foreach (var c in companies)
        {
            _options.NextPayment.Companies[c] = Confirmed();
        }
    }

    // ---- the gate: no inference, no EDSM call ----

    [Fact]
    public async Task ByDefault_NextPaymentIsExplicitlyUnavailable_AndEdsmIsNotCalled()
    {
        var result = await Resolve(4);

        Assert.Equal(CollectionsNextPaymentStatus.Unavailable, result.Status);
        Assert.Equal([CollectionsNextPaymentReasons.FeatureDisabled], result.Reasons);
        Assert.False(result.IsComplete);
        Assert.Null(result.Earliest);
        Assert.Empty(_edsm.Calls);
        Assert.False(_options.DueInstallmentsEnabled);        // unrelated switch untouched
    }

    [Fact]
    public async Task EnabledButSemanticsUnconfirmed_IsUnavailable_NamingEveryMissingConfirmation_AndEdsmIsNotCalled()
    {
        _options.NextPayment.Enabled = true;

        var result = await Resolve(4);

        var company = Assert.Single(result.Companies);
        Assert.Equal([CollectionsNextPaymentReasons.SemanticsNotConfirmed], company.Reasons);
        Assert.Contains("UnpaidStatusValues", company.MissingSemantics);
        Assert.Contains("AmountRepresents=RemainingUnpaid", company.MissingSemantics);
        Assert.Contains("ChequeDueDateIsInstalmentDueDate", company.MissingSemantics);
        Assert.Contains("ListsEveryUnpaidInstalment", company.MissingSemantics);
        Assert.Contains("CurrencyConfirmed", company.MissingSemantics);
        Assert.Contains("ConfirmedBy/ConfirmedOn/EvidenceReference", company.MissingSemantics);
        Assert.Empty(_edsm.Calls);
    }

    [Theory]
    [InlineData("amount-original", "AmountRepresents=RemainingUnpaid")]
    [InlineData("date", "ChequeDueDateIsInstalmentDueDate")]
    [InlineData("complete", "ListsEveryUnpaidInstalment")]
    [InlineData("currency", "CurrencyConfirmed")]
    [InlineData("statuses", "UnpaidStatusValues")]
    [InlineData("evidence", "ConfirmedBy/ConfirmedOn/EvidenceReference")]
    public async Task AnySingleUnconfirmedSemantic_BlocksTheCompany(string gap, string expectedMissing)
    {
        Enable(4);
        var a = _options.NextPayment.Companies[4];
        switch (gap)
        {
            case "amount-original": a.AmountRepresents = "OriginalScheduled"; break;   // partial payments could not be netted
            case "date": a.ChequeDueDateIsInstalmentDueDate = false; break;
            case "complete": a.ListsEveryUnpaidInstalment = false; break;
            case "currency": a.CurrencyConfirmed = false; break;
            case "statuses": a.UnpaidStatusValues = [" "]; break;
            case "evidence": a.EvidenceReference = null; break;
        }

        var company = Assert.Single((await Resolve(4)).Companies);

        Assert.Equal(CollectionsNextPaymentStatus.Unavailable, company.Status);
        Assert.Equal([expectedMissing], company.MissingSemantics);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task OnlyConfirmedCompaniesAreRead_AnUnconfirmedOneStaysUnavailableAndTheAnswerIsIncomplete()
    {
        Enable(4);                      // company 25 mapped but never confirmed
        _edsm.Rows.Add(Row(4, 20, 1_000m));

        var result = await Resolve(4, 25);

        Assert.Equal(CollectionsNextPaymentStatus.Available, result.Status);
        Assert.False(result.IsComplete);
        Assert.Contains(CollectionsNextPaymentReasons.SemanticsNotConfirmed, result.Reasons);
        Assert.Contains(CollectionsNextPaymentReasons.SearchIncomplete, result.Reasons);
        Assert.All(_edsm.Calls, c => Assert.Equal(4, c.Company));
    }

    [Fact]
    public async Task Company20_IsNotSupportedByDueInstallments_AndIsNeverCalled()
    {
        Enable(20);

        var company = Assert.Single((await Resolve(20)).Companies);

        Assert.Equal([CollectionsNextPaymentReasons.CompanyNotSupported], company.Reasons);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task ATenantIdBeyondEdsms32BitField_IsNotMatchable_NotGuessed()
    {
        Enable(4);

        var result = await Service().ResolveAsync([4], (long)int.MaxValue + 1, CancellationToken.None, () => false);

        Assert.Equal([CollectionsNextPaymentReasons.TenantIdNotMatchable], Assert.Single(result.Companies).Reasons);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public void ACrmCustomerWithoutAVerifiedMapping_IsExplicitlyUnavailable_WithNoCompanyOrTenant()
    {
        var result = CollectionsNextPaymentService.ForUnmapped(null);

        Assert.Equal(CollectionsNextPaymentStatus.Unavailable, result.Status);
        Assert.Equal([CollectionsNextPaymentReasons.MappingNotAvailable], result.Reasons);
        Assert.Empty(result.Companies);
    }

    // ---- selection ----

    [Fact]
    public async Task APartialPayment_UsesTheAttestedRemainingAmount_AsReceived()
    {
        Enable(4);                                   // attested: amount = remaining unpaid
        _edsm.Rows.Add(Row(4, 21, 7_500.25m));       // e.g. a 10,000.00 instalment with 2,499.75 paid

        var next = (await Resolve(4)).Earliest!;

        Assert.Equal(7_500.25m, next.Amount);
        Assert.Equal("7500.25", next.AmountRaw);
        Assert.Equal("AED", next.Currency);
    }

    [Fact]
    public async Task ANextInstalmentBeyond31Days_IsFound_ByWalkingForwardThroughOverlappingWindows()
    {
        Enable(4);
        _edsm.Rows.Add(Row(4, 120, 10_000m));        // 120 days out; a 31-day look-ahead would never see it

        var result = await Resolve(4);

        Assert.Equal(CollectionsNextPaymentStatus.Available, result.Status);
        Assert.Equal(Today.AddDays(120), result.Earliest!.DueDate);
        Assert.Equal(
            [(Today, Today.AddDays(92)), (Today.AddDays(92), Today.AddDays(184))],
            _edsm.Calls.Select(c => (c.From, c.To)));  // stops at the first window with a hit; windows share a boundary day
    }

    [Fact]
    public async Task ARowExactlyOnAWindowBoundary_IsNotMissed_AndNotDoubled()
    {
        Enable(4);
        _edsm.Rows.Add(Row(4, 92, 600m));            // lands on the shared boundary day of windows 1 and 2

        var next = (await Resolve(4)).Earliest!;

        Assert.Equal(Today.AddDays(92), next.DueDate);
        Assert.Equal(600m, next.Amount);
    }

    [Fact]
    public async Task OverdueIsNeverTheNextPayment_AndPaidRowsAndOtherTenantsAreIgnored()
    {
        Enable(4);
        _edsm.Rows.AddRange(
        [
            Row(4, 0, 2_000m),                       // due today: belongs to EDSM's dueAmount
            Row(4, 5, 900m, status: "Paid"),         // status not in the attested unpaid set
            Row(4, 6, 800m, tenant: 9999),           // another tenant's row (the route is company-wide)
            Row(4, 40, 3_000m),
        ]);

        var result = await Resolve(4);

        Assert.Equal(Today.AddDays(40), result.Earliest!.DueDate);
        Assert.Equal(3_000m, result.Earliest.Amount);
        Assert.Contains(CollectionsEdsmNextPaymentDto.OverdueNote, result.OverdueTreatment);
    }

    [Fact]
    public async Task StatusMatching_IsTrimmedAndCaseInsensitive_NothingElseCounts()
    {
        Enable(4);
        _edsm.Rows.AddRange([Row(4, 10, 100m, status: " pending "), Row(4, 3, 50m, status: "PENDING-REVIEW")]);

        Assert.Equal(Today.AddDays(10), (await Resolve(4)).Earliest!.DueDate);
    }

    [Fact]
    public async Task SeveralUnitsOnTheEarliestDate_AreListedAsLines_AndOtherUnitsAreNotSilentlyPicked()
    {
        Enable(4);
        _edsm.Rows.AddRange([Row(4, 30, 1_000m, unit: 11, voucher: "A"), Row(4, 30, 2_500m, unit: 12, voucher: "B"), Row(4, 55, 700m, unit: 13, voucher: "C")]);

        var next = (await Resolve(4)).Earliest!;

        Assert.Equal(3_500m, next.Amount);
        Assert.Equal([11L, 12L], next.Units.Select(u => u.UnitId!.Value));
    }

    [Fact]
    public async Task DuplicateRowsFromOverlappingWindows_AreCountedOnce()
    {
        Enable(4);
        var row = Row(4, 92, 600m);
        _edsm.Rows.AddRange([row, row with { }]);

        Assert.Equal(600m, (await Resolve(4)).Earliest!.Amount);
    }

    [Fact]
    public async Task EarliestAcrossCompanies_TiesBreakOnLowestCompanyId()
    {
        Enable(4, 25);
        _edsm.Rows.AddRange([Row(25, 14, 100m), Row(4, 14, 300m), Row(32, 2, 1m)]);

        var result = await Resolve(4, 25);

        Assert.Equal(4, result.Earliest!.CompanyId);
        Assert.True(result.IsComplete);
        Assert.Equal([4, 25], result.Companies.Select(c => c.CompanyId));
    }

    // ---- explicit statuses ----

    [Fact]
    public async Task NothingThroughTheHorizon_IsNoneWithinHorizon_NeverNoPayment_AndSaysHowFarItLooked()
    {
        Enable(4);
        _options.NextPayment.SearchHorizonDays = 200;

        var result = await Resolve(4);

        Assert.Equal(CollectionsNextPaymentStatus.NoneWithinHorizon, result.Status);
        Assert.Equal(Today.AddDays(200), result.SearchedThrough);
        Assert.True(result.IsComplete);
        Assert.Contains("does not say none exist later", result.Detail);
        Assert.Equal(Today.AddDays(200), _edsm.Calls.Last().To);
    }

    [Fact]
    public async Task EdsmFailure_IsUnavailable_NotAnEmptyAnswer()
    {
        Enable(4);
        _edsm.Outcome = EdsmOutcome.Unavailable;

        var result = await Resolve(4);

        Assert.Equal(CollectionsNextPaymentStatus.Unavailable, result.Status);
        Assert.Contains(CollectionsNextPaymentReasons.SourceUnavailable, result.Reasons);
    }

    [Fact]
    public async Task ADeadlinePassingMidSearch_IsReportedAsDeadlineExceeded_NotAsNone()
    {
        Enable(4);
        using var cts = new CancellationTokenSource();
        _edsm.OnCall = () => cts.Cancel();

        var result = await Service().ResolveAsync([4], Tenant, cts.Token, () => true);

        Assert.Equal(CollectionsNextPaymentStatus.Unavailable, result.Status);
        Assert.Contains(CollectionsNextPaymentReasons.DeadlineExceeded, result.Reasons);
    }

    [Fact]
    public async Task ACallersOwnCancellation_Propagates_NotSwallowedAsADeadline()
    {
        Enable(4);
        using var cts = new CancellationTokenSource();
        _edsm.OnCall = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().ResolveAsync([4], Tenant, cts.Token, () => false));
    }

    [Theory]
    [InlineData(1, 31)]        // horizon clamps up to 31
    [InlineData(99_999, 1830)] // and down to 1830
    public void HorizonAndWindowAreClamped(int configured, int effective)
    {
        var o = new CollectionsNextPaymentOptions { SearchHorizonDays = configured, WindowDays = 1 };

        Assert.Equal(effective, o.EffectiveHorizonDays);
        Assert.Equal(7, o.EffectiveWindowDays);
    }

    // ---- the committed configuration ----

    [Fact]
    public void CommittedApiSettings_ShipNextPaymentOff_WithNoConfirmedCompany_AndDueInstallmentsStillOff()
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFile())!, "..", "..", "..", "TigerCS.Api", "appsettings.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var source = doc.RootElement.GetProperty("CollectionsSource");
        var next = source.GetProperty("NextPayment");

        Assert.False(next.GetProperty("Enabled").GetBoolean());
        Assert.Empty(next.GetProperty("Companies").EnumerateObject());
        Assert.False(source.GetProperty("DueInstallmentsEnabled").GetBoolean());
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
