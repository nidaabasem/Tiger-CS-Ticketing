using System.Text.Json;
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
    string? DeliveryChannel = null)
{
    /// <summary>
    /// Optional. The CRM lead of the unit the customer chose (from a previous
    /// <c>SelectionRequired</c> answer with <c>choiceKind: "Unit"</c>). It must be
    /// one of the verified customer's own leads. Omitted or blank, the verified
    /// unit is used. A number or a numeric string.
    /// </summary>
    [JsonConverter(typeof(LenientNullableIntConverter))]
    public int? CrmLeadId { get; init; }
}

/// <summary>
/// Reads <c>crmLeadId</c> as a number, a numeric string, or blank/null → null.
/// A Genesys data action cannot leave a field out of its request template, so
/// an unset Architect variable arrives as <c>""</c> — which means "not
/// supplied", exactly like the other optional fields of this request.
/// </summary>
public sealed class LenientNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                return number;
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                break;
        }

        throw new JsonException("crmLeadId must be a whole number.");
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

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

/// <summary>
/// One option to offer the customer. For <c>choiceKind: "Document"</c>,
/// <c>recordId</c> is passed back as <c>recordId</c>; for <c>"Unit"</c> it is the
/// CRM lead id, passed back as <c>crmLeadId</c>.
/// </summary>
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
    IReadOnlyList<CrmDocumentChoice>? Choices = null,
    string? ChoiceKind = null);

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

    /// <summary>CRM answered 400 to GetCustomerDocuments.</summary>
    public const string CrmRequestRejected = "CRM_REQUEST_REJECTED";

    /// <summary>CRM answered 401: <c>Crm:SecretKey</c> is wrong or missing.</summary>
    public const string CrmAuthenticationFailed = "CRM_AUTHENTICATION_FAILED";

    /// <summary>CRM answered 403.</summary>
    public const string CrmAccessDenied = "CRM_ACCESS_DENIED";

    /// <summary>CRM returned a body that is not the contract, named another customer/lead, or pointed at a file TigerCS may not fetch.</summary>
    public const string CrmInvalidResponse = "CRM_INVALID_RESPONSE";

    /// <summary>The verified contact cannot be resolved to exactly one CRM buyer (no phone on record, not a buyer, or ambiguous).</summary>
    public const string CrmCustomerNotResolved = "CRM_CUSTOMER_NOT_RESOLVED";
    public const string DeliveryChannelNotIntegrated = "DELIVERY_CHANNEL_NOT_INTEGRATED";
    public const string DeliveryDestinationUnavailable = "DELIVERY_DESTINATION_UNAVAILABLE";
    public const string DocumentTooLarge = "DOCUMENT_TOO_LARGE";
    public const string DeliveryFailed = "DELIVERY_FAILED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string RequestInProgress = "REQUEST_IN_PROGRESS";
}
