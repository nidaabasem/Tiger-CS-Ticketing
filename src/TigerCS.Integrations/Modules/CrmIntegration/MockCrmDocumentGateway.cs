using System.Text;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Fixture documents for <c>Crm:Provider = "Mock"</c> (Development/Testing
/// only — <see cref="CrmGatewaySafety"/> refuses it elsewhere), shaped like
/// CRM's <c>GetCustomerDocuments</c> answers. Customer 9001 / lead 12345 has
/// two contracts (CRM says selection required), one reservation form, one
/// layout (no attachmentId) and no registration receipt; customer 9001 /
/// lead 12346 has exactly one of each, receipt included. Any other
/// customer/lead pair is "nothing on record". The "files" are plain-text
/// stand-ins, labelled as such — never a real document.
/// </summary>
public sealed class MockCrmDocumentGateway : ICrmDocumentGateway
{
    private sealed record Doc(int CustomerId, int LeadId, CrmDocumentType Type, string RecordId, string Name);

    private static readonly Doc[] Docs =
    [
        new(9001, 12345, CrmDocumentType.Contract, "5001", "Sale and Purchase Agreement.pdf"),
        new(9001, 12345, CrmDocumentType.Contract, "5002", "Addendum 1.pdf"),
        new(9001, 12345, CrmDocumentType.ReservationForm, "4001", "Reservation Form.pdf"),
        new(9001, 12345, CrmDocumentType.UnitLayout, "LAYOUT-12345", "Unit Layout.pdf"),

        new(9001, 12346, CrmDocumentType.Contract, "5003", "Sale and Purchase Agreement.pdf"),
        new(9001, 12346, CrmDocumentType.ReservationForm, "4002", "Reservation Form.pdf"),
        new(9001, 12346, CrmDocumentType.UnitLayout, "LAYOUT-12346", "Unit Layout.pdf"),
        new(9001, 12346, CrmDocumentType.RegistrationReceipt, "6001", "Registration Receipt.pdf"),

        // Another customer's document — must never be reachable through customer 9001.
        new(9002, 22222, CrmDocumentType.Contract, "5999", "Someone else's contract.pdf")
    ];

    public Task<CrmDocumentListing> ListAsync(CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken = default)
    {
        var rows = Docs.Where(d => d.CustomerId == customerId && d.LeadId == leadId && d.Type == type).ToList();
        return Task.FromResult(new CrmDocumentListing(
            customerId, leadId, SelectionRequired: rows.Count > 1,
            rows.Select(d => new CrmDocumentRecord(d.RecordId, d.Name, $"mock://documents/{d.RecordId}")).ToList()));
    }

    public Task<CrmDocumentContent?> DownloadAsync(CrmDocumentRecord record, CancellationToken cancellationToken = default)
    {
        var doc = Docs.FirstOrDefault(d => d.RecordId == record.RecordId);
        if (doc is null)
        {
            return Task.FromResult<CrmDocumentContent?>(null);
        }

        var bytes = Encoding.UTF8.GetBytes($"MOCK {doc.Type} FIXTURE — not a real document.\r\nRecord {doc.RecordId}: {doc.Name}\r\n");
        return Task.FromResult<CrmDocumentContent?>(new CrmDocumentContent(doc.RecordId, bytes, "text/plain", $"{doc.RecordId}.txt"));
    }
}
