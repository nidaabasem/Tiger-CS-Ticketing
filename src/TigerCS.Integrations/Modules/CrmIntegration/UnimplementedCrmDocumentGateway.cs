using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The <see cref="ICrmDocumentGateway"/> for <c>Crm:Provider = "Http"</c>:
/// <b>Tiger CRM has published no operation that lists or returns a customer's
/// contract, reservation form, unit layout or registration receipt</b>
/// (neither <c>Internal-CRM-API-Contract.md</c> nor the
/// <c>TicketingSystem/GetBuyerByPhone</c> integration carries documents), so
/// there is nothing to call. Like <see cref="UnimplementedCrmHttpGateway"/>
/// this fails closed — it never serves fixture data and never invents a
/// document. Replace it with a real gateway once CRM exposes, per document
/// type, (1) "records for unit U and contact C" and (2) "the file for record
/// R", plus the contact's email on record.
/// </summary>
public sealed class UnimplementedCrmDocumentGateway(ILogger<UnimplementedCrmDocumentGateway> logger) : ICrmDocumentGateway
{
    public Task<CrmDocumentListing> ListAsync(
        CrmDocumentType type, string crmUnitId, string crmContactId, CancellationToken cancellationToken = default) =>
        Task.FromException<CrmDocumentListing>(Unavailable($"list {type} records"));

    public Task<CrmDocumentContent?> GetContentAsync(
        CrmDocumentType type, string recordId, CancellationToken cancellationToken = default) =>
        Task.FromException<CrmDocumentContent?>(Unavailable($"download a {type} document"));

    private CrmDocumentSourceUnavailableException Unavailable(string operation)
    {
        logger.LogWarning(
            "Tiger CRM operation '{Operation}' has no published endpoint; Crm:Provider \"Http\" fails closed for customer documents.",
            operation);
        return new CrmDocumentSourceUnavailableException(
            $"Tiger CRM has no published endpoint to {operation}. Implement it CRM-side and replace UnimplementedCrmDocumentGateway.");
    }
}
