using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments;

/// <summary>
/// Maps TigerCS's public document-type values (the ones Genesys sends — they
/// do not change) to CRM's <c>GetCustomerDocuments</c> vocabulary.
/// </summary>
public static class CrmDocumentTypeMapping
{
    /// <summary>The CRM <c>DocumentType</c> string for a public type.</summary>
    public static string ToCrmName(CrmDocumentType type) => type switch
    {
        CrmDocumentType.ReservationForm => "ReservationForm",
        CrmDocumentType.Contract => "TigerContract",
        CrmDocumentType.RegistrationReceipt => "RegistrationReceipt",
        CrmDocumentType.UnitLayout => "Layout",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.")
    };

    /// <summary>
    /// CRM's attachment-type number behind the name (ReservationForm 4,
    /// TigerContract 5, RegistrationReceipt 6); null for Layout, which is the
    /// unit's <c>unitplan</c> field rather than an attachment. Documentation
    /// and diagnostics only — the request carries the name.
    /// </summary>
    public static int? ToCrmAttachmentType(CrmDocumentType type) => type switch
    {
        CrmDocumentType.ReservationForm => 4,
        CrmDocumentType.Contract => 5,
        CrmDocumentType.RegistrationReceipt => 6,
        _ => null
    };

    /// <summary>Layout documents carry no <c>attachmentId</c>; their record id is synthesised from the lead.</summary>
    public static string LayoutRecordId(int leadId) => $"LAYOUT-{leadId}";
}
