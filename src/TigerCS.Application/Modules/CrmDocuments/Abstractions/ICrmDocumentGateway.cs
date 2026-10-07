using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Abstractions;

/// <summary>
/// Tiger CRM's document source: the records a customer has for one document
/// type, and the document itself. <b>No Tiger CRM operation for this has been
/// published</b>, so the only real-environment implementation today is
/// <c>UnimplementedCrmDocumentGateway</c>, which fails closed — the API
/// answers "document source unavailable" rather than inventing a document. The
/// port is what a real CRM integration replaces; everything above it (identity
/// check, ownership, selection, delivery, idempotency) is already built.
/// </summary>
public interface ICrmDocumentGateway
{
    /// <summary>
    /// The records of <paramref name="type"/> that CRM holds for this unit and
    /// contact, plus the contact's email on record in CRM. <b>Scoping is
    /// asked of CRM, and re-checked by the caller</b> — a record that is not
    /// for this unit and contact is dropped even if CRM returned it.
    /// </summary>
    /// <exception cref="CrmDocumentSourceUnavailableException">CRM cannot be reached, or has no such operation.</exception>
    Task<CrmDocumentListing> ListAsync(CrmDocumentType type, string crmUnitId, string crmContactId, CancellationToken cancellationToken = default);

    /// <summary>The document bytes for one record, or null when CRM no longer has it.</summary>
    /// <exception cref="CrmDocumentSourceUnavailableException">CRM cannot be reached, or has no such operation.</exception>
    Task<CrmDocumentContent?> GetContentAsync(CrmDocumentType type, string recordId, CancellationToken cancellationToken = default);
}

/// <param name="RecordId">CRM's id for the record — opaque to TigerCS.</param>
/// <param name="Label">Customer-readable name for a selection list, e.g. "Sale and Purchase Agreement".</param>
/// <param name="CrmUnitId">The unit the record belongs to.</param>
/// <param name="OwnerCrmContactId">The CRM contact the record belongs to.</param>
/// <param name="UnitNumber">For the selection list only.</param>
/// <param name="IssuedOn">When the record was issued, where CRM says.</param>
public sealed record CrmDocumentRecord(
    string RecordId, string Label, string CrmUnitId, string OwnerCrmContactId, string? UnitNumber = null, DateTime? IssuedOn = null);

/// <param name="Records">Everything CRM returned (unfiltered).</param>
/// <param name="CustomerEmail">The owning contact's email on record in CRM — the only address a document is ever sent to. Never supplied by the caller.</param>
public sealed record CrmDocumentListing(IReadOnlyList<CrmDocumentRecord> Records, string? CustomerEmail);

public sealed record CrmDocumentContent(
    string RecordId, string CrmUnitId, string OwnerCrmContactId, byte[] Bytes, string ContentType, string FileName);

public sealed class CrmDocumentSourceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
