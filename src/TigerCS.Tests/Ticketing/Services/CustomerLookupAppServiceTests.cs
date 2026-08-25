using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Business-rule change: which of CRM/PACT/Tasleeh are searched depends on
/// the IntakeRecord's DepartmentId — the Department's configured source(s)
/// only when set (never a hardcoded department-id branch; see
/// <see cref="DepartmentCustomerLookupSource"/>), or all three when no
/// Department was selected. Either way, the search is enrichment/
/// identification only, never a Ticket creation gate. Required regression
/// coverage: department scoped to one source, department scoped to
/// multiple sources, no department (all three), each source's Found/
/// NotFound/Failed outcome, and that a partial result set (one source
/// Found, one Failed, one NotFound) still returns everything together.
/// </summary>
public class CustomerLookupAppServiceTests
{
    private const string Phone = "+971500000001";
    private const int DepartmentId = 7;

    private sealed record Fixture(
        CustomerLookupAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeDepartmentCustomerLookupSourceRepository DepartmentSources,
        FakeCrmCustomerLookupGateway Crm,
        FakePactGateway Pact,
        FakeTasleehGateway Tasleeh,
        FakeCrmGateway CrmUnitGateway);

    private static Fixture CreateService()
    {
        var intakeRecords = new FakeIntakeRecordRepository();
        var departmentSources = new FakeDepartmentCustomerLookupSourceRepository();
        var crmLookup = new FakeCrmCustomerLookupGateway();
        var pact = new FakePactGateway();
        var tasleeh = new FakeTasleehGateway();

        // CustomerLookupAppService resolves a CRM phone match's local
        // reference ids by re-fetching the same unit/contacts through
        // CrmUnitLookupAppService (the existing cache-aside upsert) — in
        // production both interfaces are backed by the same MockCrmGateway
        // fixture data, so a test that seeds a phone match must seed this
        // gateway with the matching unit/contacts too, exactly as
        // MockCrmGateway's single Fixtures dictionary does for real.
        var crmGateway = new FakeCrmGateway();
        var unitReferences = new FakeUnitReferenceRepository();
        var contactReferences = new FakeContactReferenceRepository();
        var crmUnitOfWork = new FakeCustomerVerificationUnitOfWork();
        var crmUnitLookup = new CrmUnitLookupAppService(crmGateway, unitReferences, contactReferences, crmUnitOfWork, TimeProvider.System);

        var service = new CustomerLookupAppService(intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmUnitLookup);

        return new Fixture(service, intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmGateway);
    }

    private static async Task<long> SeedIntakeAsync(
        FakeIntakeRecordRepository repo, string phoneNumber = Phone, int? departmentId = null)
    {
        var record = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            Channel.Phone, phoneNumber, departmentId, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);
        await repo.AddAsync(record);
        return record.IntakeRecordId;
    }

    [Fact]
    public async Task SearchAsync_IntakeRecordNotFound_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.SearchAsync(999);

        Assert.Equal(CustomerLookupOutcome.IntakeRecordNotFound, result.Outcome);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_SearchesAllThreeSourcesFindNothing_ReturnsNotFoundForEach()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.All(result.Response.Sources, s => Assert.Equal("NotFound", s.Status));
    }

    [Fact]
    public async Task SearchAsync_CrmFindsMatch_ReturnsFoundWithLocalUnitAndContactReferenceIds()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var unit = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit, contact);
        f.Crm.Seed(Phone, new CrmCustomerMatch(unit.CrmUnitId, contact.CrmContactId, contact.DisplayName, Phone));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var crmResult = Assert.Single(result.Response!.Sources, s => s.Source == "Crm");
        Assert.Equal("Found", crmResult.Status);
        Assert.Equal("Ahmed Al-Farsi", crmResult.DisplayName);
        Assert.NotNull(crmResult.UnitReferenceId);
        Assert.NotNull(crmResult.ContactReferenceId);
        Assert.Equal("1204", crmResult.UnitNumber);
    }

    [Fact]
    public async Task SearchAsync_CrmUnavailable_ReturnsFailed_NeverBlocksTheOtherTwoSources()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Crm.ThrowUnavailable = true;
        f.Pact.Seed(Phone, new PactCustomerMatch("PACT-CUST-1", "Fatima Noor", Phone));

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Crm").Status);
        Assert.Equal("Found", Assert.Single(result.Response.Sources, s => s.Source == "Pact").Status);
    }

    [Fact]
    public async Task SearchAsync_PactFindsMatch_ReturnsFound()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatch("PACT-CUST-1", "Fatima Noor", Phone));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = Assert.Single(result.Response!.Sources, s => s.Source == "Pact");
        Assert.Equal("Found", pactResult.Status);
        Assert.Equal("Fatima Noor", pactResult.DisplayName);
        Assert.Null(pactResult.UnitReferenceId);
    }

    [Fact]
    public async Task SearchAsync_PactUnavailable_ReturnsFailed()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.ThrowUnavailable = true;

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Pact").Status);
    }

    [Fact]
    public async Task SearchAsync_TasleehFindsMatch_ReturnsFound()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var tasleehResult = Assert.Single(result.Response!.Sources, s => s.Source == "Tasleeh");
        Assert.Equal("Found", tasleehResult.Status);
        Assert.Equal("Omar Khalid", tasleehResult.DisplayName);
    }

    [Fact]
    public async Task SearchAsync_TasleehUnavailable_ReturnsFailed()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Tasleeh.ThrowUnavailable = true;

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Tasleeh").Status);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_PartialResults_CrmFoundPactFailedTasleehNotFound_ReturnsAllThreeTogether()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);
        var unit = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit, contact);
        f.Crm.Seed(Phone, new CrmCustomerMatch(unit.CrmUnitId, contact.CrmContactId, contact.DisplayName, Phone));
        f.Pact.ThrowUnavailable = true;
        // Tasleeh has no fixture for this phone number — NotFound.

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.Equal("Found", Assert.Single(result.Response.Sources, s => s.Source == "Crm").Status);
        Assert.Equal("Failed", Assert.Single(result.Response.Sources, s => s.Source == "Pact").Status);
        Assert.Equal("NotFound", Assert.Single(result.Response.Sources, s => s.Source == "Tasleeh").Status);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_AllThreeSourcesFail_StillSucceedsWithThreeFailedEntries()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);
        f.Crm.ThrowUnavailable = true;
        f.Pact.ThrowUnavailable = true;
        f.Tasleeh.ThrowUnavailable = true;

        var result = await f.Service.SearchAsync(intakeRecordId);

        // A source that cannot be reached is never a Ticket-creation gate —
        // the lookup itself still returns Success with three Failed entries,
        // rather than failing the whole request.
        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.All(result.Response.Sources, s => Assert.Equal("Failed", s.Status));
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToCrmOnly_SearchesOnlyCrm()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm);
        f.Pact.Seed(Phone, new PactCustomerMatch("PACT-CUST-1", "Fatima Noor", Phone));
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToPactOnly_SearchesOnlyPact()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Pact);
        f.Pact.Seed(Phone, new PactCustomerMatch("PACT-CUST-1", "Fatima Noor", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Pact", source.Source);
        Assert.Equal("Found", source.Status);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToTasleehOnly_SearchesOnlyTasleeh()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Tasleeh);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Tasleeh", source.Source);
        Assert.Equal("Found", source.Status);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Pact.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToCrmAndTasleeh_SearchesOnlyThoseTwo_NeverPact()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm, CustomerLookupSource.Tasleeh);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Response!.Sources.Count);
        Assert.Contains(result.Response.Sources, s => s.Source == "Crm");
        Assert.Contains(result.Response.Sources, s => s.Source == "Tasleeh");
        Assert.DoesNotContain(result.Response.Sources, s => s.Source == "Pact");
        Assert.Equal(0, f.Pact.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentConfiguredSourceReturnsNotFound_DoesNotFallBackToOtherSources()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm);
        // CRM has no fixture for this phone number (NotFound). PACT/Tasleeh
        // have matches seeded, but the Department is scoped to CRM only —
        // never an automatic fallback to the other sources.
        f.Pact.Seed(Phone, new PactCustomerMatch("PACT-CUST-1", "Fatima Noor", Phone));
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal("NotFound", source.Status);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentWithNoConfiguredSources_SearchesNothing()
    {
        var f = CreateService();
        // Deliberately not seeded — a Department with zero configured
        // sources searches nothing rather than silently falling back to all
        // three (never an automatic fallback unless explicitly configured).
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Empty(result.Response!.Sources);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }
}
