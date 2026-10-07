using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.CrmDocuments.Dto;

/// <summary>
/// What the chatbot asks for. There is deliberately <b>no phone number, no
/// customer id and no destination</b>: who the customer is comes from the
/// confirmed verification session, and where the document goes comes from CRM.
/// </summary>
/// <param name="VerificationSessionId">The confirmed verification session (existing flow) that proves who the customer is.</param>
/// <param name="DocumentType">Contract, ReservationForm, UnitLayout or RegistrationReceipt.</param>
/// <param name="CrmUnitId">Optional. When sent it must be the verified unit — a different unit is an ownership mismatch, never a lookup.</param>
/// <param name="RecordId">Optional. The choice the customer made from a previous <c>SelectionRequired</c> answer.</param>
/// <param name="DeliveryChannel">Optional. Only Email is integrated; default Email.</param>
public sealed record CrmDocumentCopyRequestDto(
    Guid? VerificationSessionId,
    string? DocumentType,
    string? CrmUnitId = null,
    string? RecordId = null,
    string? DeliveryChannel = null);

/// <summary>Serialized by name ("Sent", "SelectionRequired", …) — the wire contract is the word, never the number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrmDocumentCopyStatus
{
    /// <summary>The delivery channel accepted the document. Also the answer to a replay of a request that was already sent.</summary>
    Sent,

    /// <summary>An identical request is already being processed; nothing new was sent. Ask again with the same key.</summary>
    Queued,

    /// <summary>More than one record matches; <c>choices</c> lists them. Nothing was sent.</summary>
    SelectionRequired,

    /// <summary>No such document for this customer, or CRM's document source is not available.</summary>
    DocumentUnavailable,

    /// <summary>The document was found but could not be delivered.</summary>
    DeliveryFailed,

    InvalidRequest,
    VerificationFailed,
    OwnershipMismatch,
    IdempotencyConflict,
    Disabled
}

public sealed record CrmDocumentChoice(string RecordId, string Label, string? UnitNumber, DateTime? IssuedOn);

/// <summary>
/// The outcome. Carries identifiers and status only — never the document,
/// never the customer's address (a masked form at most), never the phone.
/// </summary>
public sealed record CrmDocumentCopyResult(
    CrmDocumentCopyStatus Status,
    string? Code = null,
    string? Message = null,
    string? DocumentType = null,
    string? RecordId = null,
    string? DeliveryChannel = null,
    string? MaskedDestination = null,
    long? DeliveryRequestId = null,
    bool Duplicate = false,
    bool? Retryable = null,
    IReadOnlyList<CrmDocumentChoice>? Choices = null);

/// <summary>Machine-readable codes carried in <see cref="CrmDocumentCopyResult.Code"/>.</summary>
public static class CrmDocumentCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string DocumentCopyDisabled = "DOCUMENT_COPY_DISABLED";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string RecordOwnershipMismatch = "RECORD_OWNERSHIP_MISMATCH";
    public const string SelectionRequired = "SELECTION_REQUIRED";
    public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
    public const string DocumentSourceUnavailable = "DOCUMENT_SOURCE_UNAVAILABLE";
    public const string DeliveryChannelNotIntegrated = "DELIVERY_CHANNEL_NOT_INTEGRATED";
    public const string DeliveryDestinationUnavailable = "DELIVERY_DESTINATION_UNAVAILABLE";
    public const string DocumentTooLarge = "DOCUMENT_TOO_LARGE";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string RequestInProgress = "REQUEST_IN_PROGRESS";
}
