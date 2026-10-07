using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Abstractions;

/// <summary>
/// Tiger CRM's customer-document source —
/// <c>POST {Crm}/TicketingSystem/GetCustomerDocuments</c> — and the
/// authorized retrieval of the files it points at.
///
/// <para>
/// Documents are addressed by CRM's own <b>CustomerID + LeadID</b> (the
/// customer and the selected unit), obtained from the verified customer's CRM
/// buyer lookup — never from the caller. The gateway promises that what it
/// returns is what CRM said <i>for that customer and lead</i>: a response that
/// names a different customer or lead is refused as an invalid response.
/// </para>
/// </summary>
public interface ICrmDocumentGateway
{
    /// <summary>The documents CRM holds of <paramref name="type"/> for this customer and lead. An empty list is a normal answer (nothing on record, or CRM 404).</summary>
    /// <exception cref="CrmDocumentSourceException">CRM refused, failed, or returned something unusable.</exception>
    Task<CrmDocumentListing> ListAsync(CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes of one listed record, fetched with the server-side CRM
    /// credential — <see cref="CrmDocumentRecord.FileReference"/> is a storage
    /// reference, not a public URL — or null when the file is gone.
    /// </summary>
    /// <exception cref="CrmDocumentSourceException">CRM refused, failed, or the reference is not one the gateway may fetch.</exception>
    Task<CrmDocumentContent?> DownloadAsync(CrmDocumentRecord record, CancellationToken cancellationToken = default);
}

/// <param name="RecordId">The attachment id CRM gave (Layout has none: <c>LAYOUT-{leadId}</c>). The id the chatbot passes back as <c>recordId</c>.</param>
/// <param name="Label">CRM's attachment name — what the customer is shown in a selection list.</param>
/// <param name="FileReference">CRM's <c>fileUrl</c>: a storage reference. Never returned to callers, never assumed public.</param>
public sealed record CrmDocumentRecord(string RecordId, string Label, string FileReference);

/// <param name="CustomerId">The customer CRM answered for (echoed and checked by the gateway).</param>
/// <param name="LeadId">The lead CRM answered for (echoed and checked by the gateway).</param>
/// <param name="SelectionRequired">CRM says more than one document matches: the customer must choose.</param>
/// <param name="Records">The documents CRM listed, in CRM's order.</param>
public sealed record CrmDocumentListing(
    int CustomerId, int LeadId, bool SelectionRequired, IReadOnlyList<CrmDocumentRecord> Records);

public sealed record CrmDocumentContent(string RecordId, byte[] Bytes, string ContentType, string FileName);

/// <summary>Why a CRM document call could not give an answer. Each one has its own result code; none is retried blindly.</summary>
public enum CrmDocumentSourceFailure
{
    /// <summary>CRM could not be reached, timed out, or answered 5xx (500/503/…): worth retrying later.</summary>
    Unavailable,

    /// <summary>CRM answered 400: it rejected the request as malformed. A TigerCS/contract defect — not retryable.</summary>
    RequestRejected,

    /// <summary>CRM answered 401: the configured <c>Crm:SecretKey</c> is wrong or missing. Operations must fix configuration.</summary>
    AuthenticationFailed,

    /// <summary>CRM answered 403: the credential is not allowed this operation/customer.</summary>
    AccessDenied,

    /// <summary>CRM answered 200 with a body that is not the contract, or names another customer/lead.</summary>
    InvalidResponse,

    /// <summary>A <c>fileUrl</c> that is not on the configured CRM origin (or allow-list), so the credential is not sent to it.</summary>
    ReferenceRejected
}

public sealed class CrmDocumentSourceException(CrmDocumentSourceFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public CrmDocumentSourceFailure Failure { get; } = failure;
}
