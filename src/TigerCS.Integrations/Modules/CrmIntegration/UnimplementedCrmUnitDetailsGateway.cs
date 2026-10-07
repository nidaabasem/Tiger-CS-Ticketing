using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// What <c>Crm:Provider = "Http"</c> resolves for <see cref="ICrmUnitDetailsGateway"/>.
/// Tiger CRM documents no endpoint for tower, bedrooms, area, parking, project
/// address/status/description/amenities or handover dates, so none can be
/// called without inventing a contract. It answers
/// <see cref="CrmUnitDetailsOutcome.NotAvailable"/> and never serves fixture
/// data: the unit-details API then returns those values as null.
/// </summary>
public sealed class UnimplementedCrmUnitDetailsGateway(ILogger<UnimplementedCrmUnitDetailsGateway> logger)
    : ICrmUnitDetailsGateway
{
    public Task<CrmUnitDetailsResult> GetUnitDetailsAsync(int crmCustomerId, int crmUnitId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("No Tiger CRM endpoint publishes unit/project detail facts; returning NotAvailable for unit {UnitId}.", crmUnitId);
        return Task.FromResult(CrmUnitDetailsResult.NotAvailable());
    }
}
