using System.Text;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Fixture documents for <c>Crm:Provider = "Mock"</c> (Development/Testing
/// only — <see cref="CrmGatewaySafety"/> refuses it elsewhere). Aligned with
/// <see cref="MockCrmGateway"/>'s units and contacts so the whole flow can be
/// exercised locally: verified unit CRM-UNIT-1001 / contact CRM-CONTACT-2001
/// has two contracts (a selection), one reservation form, one unit layout and
/// no registration receipt (document unavailable). CRM-UNIT-1002 / contact
/// CRM-CONTACT-2003 has exactly one of each — and a record that is another
/// customer's, so ownership checks have something real to refuse. The "files"
/// are plain text stand-ins and are labelled as such: never a real document.
/// </summary>
public sealed class MockCrmDocumentGateway : ICrmDocumentGateway
{
    private sealed record Doc(CrmDocumentType Type, CrmDocumentRecord Record);

    private static readonly Doc[] Docs =
    [
        new(CrmDocumentType.Contract, new("MOCK-CONTRACT-1", "Sale and Purchase Agreement", "CRM-UNIT-1001", "CRM-CONTACT-2001", "1204", new DateTime(2025, 3, 1))),
        new(CrmDocumentType.Contract, new("MOCK-CONTRACT-2", "Addendum 1 to the Sale and Purchase Agreement", "CRM-UNIT-1001", "CRM-CONTACT-2001", "1204", new DateTime(2025, 9, 15))),
        new(CrmDocumentType.ReservationForm, new("MOCK-RESERVATION-1", "Reservation Form", "CRM-UNIT-1001", "CRM-CONTACT-2001", "1204", new DateTime(2025, 2, 10))),
        new(CrmDocumentType.UnitLayout, new("MOCK-LAYOUT-1", "Unit Layout — Tower A, 1204", "CRM-UNIT-1001", "CRM-CONTACT-2001", "1204")),

        new(CrmDocumentType.Contract, new("MOCK-CONTRACT-3", "Sale and Purchase Agreement", "CRM-UNIT-1002", "CRM-CONTACT-2003", "0507", new DateTime(2025, 4, 20))),
        new(CrmDocumentType.ReservationForm, new("MOCK-RESERVATION-2", "Reservation Form", "CRM-UNIT-1002", "CRM-CONTACT-2003", "0507", new DateTime(2025, 4, 1))),
        new(CrmDocumentType.UnitLayout, new("MOCK-LAYOUT-2", "Unit Layout — Tower B, 0507", "CRM-UNIT-1002", "CRM-CONTACT-2003", "0507")),
        new(CrmDocumentType.RegistrationReceipt, new("MOCK-RECEIPT-1", "Registration Receipt", "CRM-UNIT-1002", "CRM-CONTACT-2003", "0507", new DateTime(2025, 6, 2)))
    ];

    private static readonly Dictionary<string, string> EmailByContact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRM-CONTACT-2001"] = "ahmed.alfarsi@example.com",
        ["CRM-CONTACT-2003"] = "layla.hassan@example.com"
    };

    public Task<CrmDocumentListing> ListAsync(
        CrmDocumentType type, string crmUnitId, string crmContactId, CancellationToken cancellationToken = default)
    {
        var records = Docs
            .Where(d => d.Type == type
                && d.Record.CrmUnitId.Equals(crmUnitId, StringComparison.OrdinalIgnoreCase)
                && d.Record.OwnerCrmContactId.Equals(crmContactId, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Record)
            .ToList();

        return Task.FromResult(new CrmDocumentListing(records, EmailByContact.GetValueOrDefault(crmContactId)));
    }

    public Task<CrmDocumentContent?> GetContentAsync(
        CrmDocumentType type, string recordId, CancellationToken cancellationToken = default)
    {
        var doc = Docs.FirstOrDefault(d => d.Type == type && d.Record.RecordId.Equals(recordId, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            return Task.FromResult<CrmDocumentContent?>(null);
        }

        var bytes = Encoding.UTF8.GetBytes($"MOCK {type} FIXTURE — not a real document.\r\nRecord {doc.Record.RecordId}: {doc.Record.Label}\r\n");
        return Task.FromResult<CrmDocumentContent?>(new CrmDocumentContent(
            doc.Record.RecordId, doc.Record.CrmUnitId, doc.Record.OwnerCrmContactId, bytes, "text/plain",
            $"{doc.Record.RecordId}.txt"));
    }
}
