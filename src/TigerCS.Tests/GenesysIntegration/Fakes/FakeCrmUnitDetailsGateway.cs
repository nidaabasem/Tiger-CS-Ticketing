using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

namespace TigerCS.Tests.GenesysIntegration.Fakes;

/// <summary>Queued-answer <see cref="ICrmUnitDetailsGateway"/> double that records which units were asked about.</summary>
public sealed class FakeCrmUnitDetailsGateway : ICrmUnitDetailsGateway
{
    private readonly Dictionary<int, CrmUnitDetailsResult> _byUnit = [];

    public Exception? Throws { get; set; }

    public List<(int CustomerId, int UnitId)> Calls { get; } = [];

    /// <summary>The lead id and includeSale flag of each call, in order — proves the sale is only requested with proof and for the bound Lead.</summary>
    public List<(int? LeadId, bool IncludeSale)> SaleRequests { get; } = [];

    public FakeCrmUnitDetailsGateway Returns(int unitId, CrmUnitDetailsResult result)
    {
        _byUnit[unitId] = result;
        return this;
    }

    public Task<CrmUnitDetailsResult> GetUnitDetailsAsync(
        int crmCustomerId, int crmUnitId, int? crmLeadId = null, bool includeSale = false, CancellationToken cancellationToken = default)
    {
        Calls.Add((crmCustomerId, crmUnitId));
        SaleRequests.Add((crmLeadId, includeSale));
        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(_byUnit.GetValueOrDefault(crmUnitId) ?? CrmUnitDetailsResult.NotAvailable());
    }
}
