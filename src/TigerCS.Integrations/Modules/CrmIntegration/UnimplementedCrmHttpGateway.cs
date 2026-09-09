using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// What <c>Crm:Provider = "Http"</c> — the standard for Development, UAT and
/// Production — resolves for the two CRM ports that have no Tiger CRM
/// endpoint yet: <see cref="ICrmGateway"/> (unit by id, unit search, contacts
/// for a unit) and <see cref="ICrmCustomerLookupGateway"/> (customer search
/// by phone). It never answers with data. Every call fails closed through the
/// port's own outage contract — the same exception a real gateway throws when
/// Tiger CRM cannot be reached — so callers take the "CRM unavailable" paths
/// they already have (<c>502 crm-unavailable</c> on <c>GET /api/crm/units…</c>,
/// a <c>Failed</c> Crm source on the department-aware customer lookup) and
/// nothing else is affected. A real environment configured for "Http"
/// therefore never receives <see cref="MockCrmGateway"/> fixture data.
///
/// <para>
/// Tiger CRM documents exactly one endpoint today,
/// <c>GET /TicketingSystem/GetBuyerByPhone</c>, served by
/// <see cref="CrmBuyerHttpGateway"/> on its own port. The operations below are
/// named in docs/design/Internal-CRM-API-Contract.md §1.1–§1.3 without a
/// method, path, host or authentication scheme, and the phone search is not
/// documented at all, so no HTTP implementation can be written for them
/// without inventing a contract. Replace this class with a real gateway once
/// the CRM team publishes those endpoints; the registration in
/// <see cref="IntegrationsServiceCollectionExtensions"/> is the only wiring.
/// </para>
/// </summary>
public sealed class UnimplementedCrmHttpGateway(ILogger<UnimplementedCrmHttpGateway> logger)
    : ICrmGateway, ICrmCustomerLookupGateway
{
    public Task<CrmUnitResult?> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default) =>
        Task.FromException<CrmUnitResult?>(Unavailable("get unit by CRM unit id", "Internal-CRM-API-Contract.md §1.1"));

    public Task<IReadOnlyList<CrmUnitResult>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CrmUnitResult>>(Unavailable("search units by unit number", "Internal-CRM-API-Contract.md §1.2"));

    public Task<IReadOnlyList<CrmContactResult>> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CrmContactResult>>(Unavailable("get contacts for a unit", "Internal-CRM-API-Contract.md §1.3"));

    public Task<IReadOnlyList<CrmCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        const string operation = "search customers by phone";
        const string documentedAs = "no documented Tiger CRM operation";
        LogFailClosed(operation, documentedAs);
        return Task.FromException<IReadOnlyList<CrmCustomerMatch>>(
            new CrmCustomerLookupGatewayUnavailableException(Message(operation, documentedAs)));
    }

    private CrmGatewayUnavailableException Unavailable(string operation, string documentedAs)
    {
        LogFailClosed(operation, documentedAs);
        return new CrmGatewayUnavailableException(Message(operation, documentedAs));
    }

    private void LogFailClosed(string operation, string documentedAs) =>
        logger.LogWarning(
            "Tiger CRM operation {Operation} has no published endpoint ({DocumentedAs}); Crm:Provider \"Http\" fails closed for it instead of serving MockCrmGateway fixture data.",
            operation, documentedAs);

    private static string Message(string operation, string documentedAs) =>
        $"Tiger CRM operation '{operation}' has no published endpoint ({documentedAs}); Crm:Provider \"Http\" fails closed " +
        "for it rather than serving MockCrmGateway fixture data. Implement the endpoint CRM-side and replace " +
        "UnimplementedCrmHttpGateway with a real gateway.";
}
