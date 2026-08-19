using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Application.Modules.CrmVerification.Services;
using TigerCS.Domain.Modules.CrmVerification;
using TigerCS.Tests.CrmVerification.Fakes;

namespace TigerCS.Tests.CrmVerification.Services;

public class CrmVerificationAppServiceTests
{
    private static (CrmVerificationAppService Service, FakeCrmGateway Gateway, FakeUnitReferenceRepository Units, FakeContactReferenceRepository Contacts)
        CreateService()
    {
        var gateway = new FakeCrmGateway();
        var units = new FakeUnitReferenceRepository();
        var contacts = new FakeContactReferenceRepository();
        var service = new CrmVerificationAppService(gateway, units, contacts, new FakeCrmVerificationUnitOfWork(), TimeProvider.System);
        return (service, gateway, units, contacts);
    }

    [Fact]
    public async Task GetUnitAsync_KnownUnit_ReturnsSuccessAndCachesRow()
    {
        var (service, gateway, units, _) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));

        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        Assert.Equal("1204", result.Response!.UnitNumber);
        Assert.NotNull(await units.GetByCrmUnitIdAsync("CRM-1"));
    }

    [Fact]
    public async Task GetUnitAsync_UnknownUnit_ReturnsNotFound()
    {
        var (service, _, _, _) = CreateService();

        var result = await service.GetUnitAsync("CRM-DOES-NOT-EXIST");

        Assert.Equal(CrmLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetUnitAsync_CrmUnavailable_ReturnsCrmUnavailable()
    {
        var (service, gateway, _, _) = CreateService();
        gateway.ThrowUnavailable = true;

        var result = await service.GetUnitAsync("CRM-1");

        Assert.Equal(CrmLookupOutcome.CrmUnavailable, result.Outcome);
    }

    [Fact]
    public async Task GetUnitAsync_SecondLookup_RefreshesExistingCacheRowRatherThanDuplicating()
    {
        var (service, gateway, units, _) = CreateService();
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
        var (service, gateway, _, _) = CreateService();
        gateway.Seed(new CrmUnitResult("CRM-1", "1204", "Tiger Tower A", "Tower A", "Residential"));

        var result = await service.SearchUnitsAsync("1204", propertyName: null);

        Assert.Equal(CrmLookupOutcome.Success, result.Outcome);
        Assert.Single(result.Units!);
    }

    [Fact]
    public async Task SearchUnitsAsync_CrmUnavailable_ReturnsCrmUnavailable()
    {
        var (service, gateway, _, _) = CreateService();
        gateway.ThrowUnavailable = true;

        var result = await service.SearchUnitsAsync("1204", propertyName: null);

        Assert.Equal(CrmLookupOutcome.CrmUnavailable, result.Outcome);
    }

    [Fact]
    public async Task GetContactsAsync_KnownUnit_ReturnsContactsAndCachesRepresentativeLink()
    {
        var (service, gateway, _, contacts) = CreateService();
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
        var (service, _, _, _) = CreateService();

        var result = await service.GetContactsAsync("CRM-DOES-NOT-EXIST");

        Assert.Equal(CrmLookupOutcome.NotFound, result.Outcome);
    }
}
