using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Fixture-backed <see cref="ICrmUnitDetailsGateway"/> for <c>Crm:Provider = "Mock"</c>
/// (the automated test host; <c>CrmGatewaySafety</c> refuses it outside
/// Development/Testing). Never production-ready: the values are test data,
/// not a statement about what Tiger CRM will publish.
/// </summary>
public sealed class MockCrmUnitDetailsGateway : ICrmUnitDetailsGateway
{
    /// <summary>Unit 9200 (the API test host's buyer unit): unit-level handover recorded, project-level expected only.</summary>
    private static readonly CrmUnitDetails Unit9200 = new(
        UnitTypeName: "Apartment",
        TowerName: "Tower A",
        Bedrooms: 2,
        Area: 1250.5m,
        AreaUnit: "sqft",
        Parking: [new CrmParkingSpace("P-114", "P1", null)],
        UnitExpectedHandoverDate: new DateOnly(2027, 6, 30),
        UnitActualHandoverDate: null,
        Project: new CrmProjectDetails(
            "Dubai, UAE", "Under construction", new DateOnly(2027, 3, 31), null,
            "A residential tower.", ["Pool", "Gym"], 62.5m, new DateOnly(2027, 1, 31), null),
        Sale: new CrmSaleDetails(LeadId: 9100, SoldPrice: 1850000m, RegistrationCost: 74000m, Currency: "AED"));

    public Task<CrmUnitDetailsResult> GetUnitDetailsAsync(
        int crmCustomerId, int crmUnitId, int? crmLeadId = null, bool includeSale = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(crmUnitId == 9200
            ? CrmUnitDetailsResult.Found(includeSale ? Unit9200 : Unit9200 with { Sale = null })
            : CrmUnitDetailsResult.NotAvailable());
}
