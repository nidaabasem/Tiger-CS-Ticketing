using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

public class CrmUnitLookupAppServiceTests
{
    private static (
        CrmUnitLookupAppService Service,
        FakeCrmGateway Gateway,
        FakeUnitReferenceRepository Units,
        FakeContactReferenceRepository Contacts,
        FakeCustomerVerificationUnitOfWork UnitOfWork)
        CreateService()
    {
        var gateway = new FakeCrmGateway();
        var units = new FakeUnitReferenceRepository();
        var contacts = new FakeContactReferenceRepository();
        var unitOfWork = new FakeCustomerVerificationUnitOfWork();
        var service = new CrmUnitLookupAppService(gateway, units, contacts, unitOfWork, TimeProvider.System);
        return (service, gateway, units, contacts, unitOfWork);
    }

    [Fact]
    public async Task GetUnitAsync_KnownUnit_ReturnsSuccessAndCachesRow()
    {
        var (service, gateway, units, _, _) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));

        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        Assert.Equal("1204", result.Response!.UnitNumber);
        Assert.NotNull(await units.GetByCrmUnitIdAsync("CRM-1"));
    }

    [Fact]
    public async Task GetUnitAsync_UnknownUnit_ReturnsNotFound()
    {
        var (service, _, _, _, _) = CreateService();

        var result = await service.GetUnitAsync("CRM-DOES-NOT-EXIST");

        Assert.Equal(CrmLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetUnitAsync_CrmUnavailable_ReturnsCrmUnavailable()
    {
        var (service, gateway, _, _, _) = CreateService();
        gateway.ThrowUnavailable = true;

        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.CrmUnavailable, result.Outcome);
    }

    [Fact]
    public async Task GetUnitAsync_SecondLookup_RefreshesExistingCacheRowRatherThanDuplicating()
    {
        var (service, gateway, units, _, _) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));
        await service.GetUnitAsync("CRM-1");

        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A (renamed)", "Tower A", "Residential"));
        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal("Tiger Tower A (renamed)", result.Response!.PropertyName);
        var cached = await units.GetByCrmUnitIdAsync("CRM-1");
        Assert.Equal(result.Response.UnitReferenceId, cached!.UnitReferenceId);
    }

    [Fact]
    public async Task SearchUnitsAsync_MatchingUnitNumber_ReturnsMatches()
    {
        var (service, gateway, _, _, _) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));

        var result = await service.SearchUnitsAsync("1204", propertyName: null);

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        Assert.Single(result.Units!);
    }

    [Fact]
    public async Task SearchUnitsAsync_CrmUnavailable_ReturnsCrmUnavailable()
    {
        var (service, gateway, _, _, _) = CreateService();
        gateway.ThrowUnavailable = true;

        var result = await service.SearchUnitsAsync("1204", propertyName: null);

        Assert.Equal(CrmLookupOutcome.CrmUnavailable, result.Outcome);
    }

    [Fact]
    public async Task GetContactsAsync_KnownUnit_ReturnsContactsAndCachesRepresentativeLink()
    {
        var (service, gateway, _, contacts, _) = CreateService();
        gateway.Seed(
            new CrmUnitResult("CRM-1", "0507", "Tiger Tower B", "Tower B", "Commercial"),
            new CrmContactResult("C-OWNER", "Layla Hassan", "layla@example.com", ContactType.Owner, null),
            new CrmContactResult("C-REP", "Property Management Co.", "pm@example.com", ContactType.Representative, "C-OWNER"));

        var result = await service.GetContactsAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Contacts!.Count);
        var rep = result.Contacts.Single(c => c.CrmContactId == "C-REP");
        var owner = await contacts.GetByCrmContactIdAsync("C-OWNER");
        Assert.Equal(owner!.ContactReferenceId, rep.AuthorizedRepresentativeOfContactReferenceId);
    }

    [Fact]
    public async Task GetContactsAsync_UnknownUnit_ReturnsNotFound()
    {
        var (service, _, _, _, _) = CreateService();

        var result = await service.GetContactsAsync("CRM-DOES-NOT-EXIST");

        Assert.Equal(CrmLookupOutcome.NotFound, result.Outcome);
    }

    /// <summary>
    /// Simulates a concurrent-duplicate-write race on a never-before-cached
    /// UnitReferences.CrmUnitId — this caller's own initial existence check
    /// finds nothing (nothing is pre-seeded, matching what a genuine race's
    /// loser actually observes), so it attempts an insert; that insert's
    /// SaveChangesAsync fails with DuplicateWriteException, exactly as the
    /// real unique index would produce under a genuine race
    /// (CustomerVerificationUnitOfWork's remarks). The recovery path must reload
    /// and return a row rather than propagate the exception.
    /// </summary>
    [Fact]
    public async Task GetUnitAsync_ConcurrentDuplicateWriteRace_ReturnsWinningRowInsteadOfThrowing()
    {
        var (service, gateway, units, _, unitOfWork) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));
        // The unit's own insert is the first SaveChangesAsync call this
        // service makes for a never-before-cached CrmUnitId.
        unitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        var cached = await units.GetByCrmUnitIdAsync("CRM-1");
        Assert.Equal(cached!.UnitReferenceId, result.Response!.UnitReferenceId);
    }

    /// <summary>
    /// Same race, on ContactReferences.CrmContactId (GetContactsAsync's own
    /// upsert path) — the unit itself upserts cleanly first (call #1); the
    /// never-before-cached contact's own insert (call #2) is where the race
    /// is simulated, proving the recovery is scoped to the specific entity
    /// whose write actually raced, not the whole request.
    /// </summary>
    [Fact]
    public async Task GetContactsAsync_ConcurrentDuplicateWriteRace_ReturnsWinningRowInsteadOfThrowing()
    {
        var (service, gateway, _, contacts, unitOfWork) = CreateService();
        gateway.Seed(
            new CrmUnitResult("CRM-1", "0507", "Tiger Tower B", "Tower B", "Commercial"),
            new CrmContactResult("C-OWNER", "Layla Hassan", "layla@example.com", ContactType.Owner, null));
        unitOfWork.ThrowDuplicateWriteExceptionOnCall = 2;

        var result = await service.GetContactsAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        var contact = Assert.Single(result.Contacts!);
        var cached = await contacts.GetByCrmContactIdAsync("C-OWNER");
        Assert.Equal(cached!.ContactReferenceId, contact.ContactReferenceId);
    }

    /// <summary>A genuine, unrelated save failure must never be swallowed as if it were a recoverable duplicate-write race.</summary>
    [Fact]
    public async Task GetUnitAsync_UnrelatedSaveFailure_PropagatesRatherThanBeingTreatedAsARace()
    {
        var (service, gateway, _, _, unitOfWork) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));
        unitOfWork.ThrowUnrelatedFailureOnce = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetUnitAsync("CRM-1"));
    }
}
