using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Services;

public sealed class PactReceivableCustomersTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc); // Oct 7 in Dubai
    private sealed class Source : IPactReceivablesSource
    {
        public List<PactReceivableInstalment> Items { get; } = [];
        public int Reads { get; private set; }
        public Exception? Failure { get; set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly businessDate, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new PactReceivablesSnapshot(Items, Now, true));
        }
    }

    private sealed class Harness
    {
        public Source Source { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public PactReceivablesOptions SqlOptions { get; } = new() { Enabled = true };
        public PactReceivableCustomersAppService Service { get; }
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
        public Harness()
        {
            Service = new(Options, SqlOptions,
                new CollectionsAuthorizationService(Options, new FakeDepartmentRepository()),
                new CollectionsClock(Options, new FakeTimeProvider(Now)), Source,
                NullLogger<PactReceivableCustomersAppService>.Instance);
        }
    }

    internal static PactReceivableInstalment Row(int day, decimal amount = 100m,
        string tenant = "3001", int company = 4, string unit = "TP140-101", string voucher = "INV-1") =>
        new(company, tenant, "Example Customer", "+971 50 000 3001", "example@example.test", 101,
            unit, "", voucher, "", new DateTime(2026, 10, day), amount, "Installment");

    [Fact]
    public async Task ListsCustomersWithoutTickets_UsesDubaiDate_ExcludesFutureAndSettled()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6), Row(7, 0.25m), Row(8, 900m), Row(5, 0), Row(4, -20)]);
        var result = await h.Service.ListAsync(h.Agent);
        var report = result.Value!;
        var customer = Assert.Single(report.Items);
        Assert.Equal(new DateOnly(2026, 10, 7), report.BusinessDate);
        Assert.True(customer.HasDue);
        Assert.True(customer.HasOverdue);
        Assert.Equal(0.25m, customer.DueAmount);
        Assert.Equal(100m, customer.OverdueAmount);
        Assert.Equal(1, customer.OverdueDays);
        Assert.Equal(2, customer.Instalments.Count);
    }

    [Fact]
    public async Task EachApartmentIsItsOwnRow_WithSeparateDueOverdueAndTotal()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(7, 40m), Row(6, 60m), Row(5, 25m, unit: "TP140-202"), Row(8, 900m, unit: "TP140-303"), Row(6, 0m, unit: "TP140-404")]);
        var items = (await h.Service.ListAsync(h.Agent)).Value!.Items;
        Assert.Equal(2, items.Count);
        var a = items.Single(c => c.UnitCode == "TP140-101");
        Assert.Equal((40m, 60m, 100m), (a.DueAmount, a.OverdueAmount, a.TotalAmount));
        var b = items.Single(c => c.UnitCode == "TP140-202");
        Assert.Equal((0m, 25m, 25m), (b.DueAmount, b.OverdueAmount, b.TotalAmount));
    }

    [Fact]
    public async Task AmbiguousBucketHasNoTotal_ButApartmentStaysVisible()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6), Row(6, voucher: "INV-2")]);
        var c = Assert.Single((await h.Service.ListAsync(h.Agent)).Value!.Items);
        Assert.Null(c.OverdueAmount);
        Assert.Null(c.TotalAmount);
    }

    [Fact]
    public void LegacyExclusionsAreOffByDefault() => Assert.False(new PactReceivablesOptions().ApplyLegacyExclusions);

    [Fact]
    public async Task CompanyAndTenantFormTheIdentity_AndPagingIsAfterCustomerGrouping()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(1), Row(2), Row(3, company: 32), Row(4, tenant: "4001")]);
        var first = (await h.Service.ListAsync(h.Agent, pageSize: 2)).Value!;
        var second = (await h.Service.ListAsync(h.Agent, page: 2, pageSize: 2)).Value!;
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Equal(2, first.Items.Single(c => c.CompanyId == 4).Instalments.Count);
        Assert.Single(first.Items, c => c.CompanyId == 32 && c.TenantId == "3001");
    }

    [Fact]
    public async Task RepeatedDueDatesNeverBecomeInflatedTotals_AndRawRowsArePreserved()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6), Row(6, voucher: "INV-2"), Row(7)]);
        var customer = Assert.Single((await h.Service.ListAsync(h.Agent)).Value!.Items);
        Assert.Null(customer.OverdueAmount);
        Assert.Equal(100m, customer.DueAmount);
        Assert.Equal("NeedsReview", customer.AmountStatus);
        Assert.Equal(3, customer.Instalments.Count);
    }

    [Theory]
    [InlineData("due", 1)]
    [InlineData("overdue", 2)]
    [InlineData("all", 3)]
    public async Task DueAndOverdueAreAUnion_NotARequirementToHaveBoth(string status, int expected)
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(7), Row(6, tenant: "3002"), Row(5, tenant: "3003")]);
        Assert.Equal(expected, (await h.Service.ListAsync(h.Agent, status: status)).Value!.TotalCount);
    }

    [Fact]
    public async Task PhoneSearchHandlesFormatting_CompanyFilterDoesNotMixCompanyIds()
    {
        var h = new Harness();
        h.Source.Items.AddRange([Row(6), Row(5, company: 32)]);
        var report = (await h.Service.ListAsync(h.Agent, companyId: 32, search: "971500003001")).Value!;
        Assert.Equal(32, Assert.Single(report.Items).CompanyId);
    }

    [Fact]
    public async Task UnauthorizedDisabledAndInvalidRequestsDoNotTouchPACT()
    {
        var h = new Harness();
        var reporter = new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []);
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.ListAsync(reporter)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, companyId: 25)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, page: int.MaxValue)).Outcome);
        h.SqlOptions.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await h.Service.ListAsync(h.Agent)).Outcome);
        Assert.Equal(0, h.Source.Reads);
    }

    [Fact]
    public async Task SourceFailureIsUnavailable_NotAnEmptyOrPartialList()
    {
        var h = new Harness();
        h.Source.Failure = new PactReceivablesSourceException("CRM exclusions not configured.");
        var result = await h.Service.ListAsync(h.Agent);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void LegacyExclusionsKeepCutoffBoundariesAndDoNotActivateTheCommentedTP119Rule()
    {
        var options = new PactReceivablesOptions
        {
            SpecialCaseUnitCodes = ["TP140-special"], PdcExcludedUnitCodes = ["TP140-pdc"]
        };
        var boundary = Row(6, unit: "TP121-101") with { DueDate = new DateTime(2025, 12, 15) };
        var after = boundary with { DueDate = new DateTime(2025, 12, 16) };
        var handover = Row(5, unit: "TP140-handover");
        var rows = new[] { boundary, after, handover, Row(6, unit: "TP103-1"),
            Row(6, unit: "TP140-special"), Row(6, unit: "TP140-pdc"),
            Row(6) with { FullName = "*Hidden" }, Row(6, unit: "TP119-101"), Row(6, unit: "TP140-kept") };
        var kept = PactSqlReceivablesSource.ApplyExclusions(rows,
            new HashSet<(string, DateTime)> { (handover.UnitCode, handover.DueDate) }, options).ToList();
        Assert.Equal(3, kept.Count);
        Assert.Contains(boundary, kept);
        Assert.Contains(kept, r => r.UnitCode == "TP119-101");
        Assert.Contains(kept, r => r.UnitCode == "TP140-kept");
    }
}
