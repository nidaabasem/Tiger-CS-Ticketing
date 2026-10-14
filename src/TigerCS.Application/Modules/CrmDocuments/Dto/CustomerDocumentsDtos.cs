using System.Text.Json.Serialization;

namespace TigerCS.Application.Modules.CrmDocuments.Dto;

/// <summary>What the Customer Details Documents tab shows for one customer and unit.</summary>
/// <param name="Status">
/// <c>Ready</c> (documents below), <c>UnitSelectionRequired</c> (several units, none chosen — nothing is guessed),
/// <c>NoUnits</c>, <c>NotCrmCustomer</c> (documents live in Tiger CRM, so only a CRM-identified customer has any) or
/// <c>CustomerNotResolved</c> (TigerCS holds no phone that CRM resolves to this customer).
/// </param>
/// <param name="Message">Why there is nothing to show, when <paramref name="Status"/> is not <c>Ready</c>.</param>
/// <param name="Units">The customer's CRM units (the unit selector), each with the CRM lead id documents are filed under.</param>
/// <param name="SelectedLeadId">The unit the documents belong to; null until one is chosen.</param>
/// <param name="Groups">One group per document type (Reservation Form 4, Tiger Contract 5, Registration Receipt 6) — each succeeds or fails on its own.</param>
/// <param name="Send">Whether "Send to customer" can work for this unit, and the reason when it cannot.</param>
public sealed record CustomerDocumentsDto(
    string Status,
    string? Message,
    IReadOnlyList<CustomerDocumentUnitDto> Units,
    int? SelectedLeadId,
    IReadOnlyList<CustomerDocumentGroupDto> Groups,
    CustomerDocumentSendDto Send);

public sealed record CustomerDocumentUnitDto(int LeadId, string? UnitNumber, string? ProjectName, string? LeadStatusName);

/// <param name="Label">The name shown to the agent.</param>
/// <param name="DocumentType">The public type name (<c>ReservationForm</c>, <c>Contract</c>, <c>RegistrationReceipt</c>) — what the file and send routes take.</param>
/// <param name="CrmAttachmentType">CRM's attachment type number: 4, 5 or 6.</param>
/// <param name="Status"><c>Ok</c> or <c>Unavailable</c> (CRM refused or failed for this type only).</param>
/// <param name="Message">Why the type is unavailable.</param>
/// <param name="Documents">The documents CRM lists for this customer, unit and type.</param>
public sealed record CustomerDocumentGroupDto(
    string DocumentType, string Label, int CrmAttachmentType, string Status, string? Message,
    IReadOnlyList<CustomerDocumentDto> Documents);

/// <param name="RecordId">CRM's attachment id. Never CRM's storage reference — files are fetched by TigerCS, not by the browser.</param>
/// <param name="Name">CRM's attachment name.</param>
public sealed record CustomerDocumentDto(string RecordId, string Name);

/// <summary>
/// Whether "Send to customer" can work right now, and if not, why. <c>MaskedDestination</c> is CRM's email for the
/// customer in masked form; the full address is never returned.
/// </summary>
public sealed record CustomerDocumentSendDto(bool Available, string? UnavailableReason, string? MaskedDestination);

/// <summary>The body of the agent "Send to customer" request.</summary>
public sealed record CustomerDocumentSendRequestDto(int? CrmLeadId, string? DocumentType, string? RecordId);

public enum CustomerDocumentsOutcome
{
    Success,
    InvalidRequest,
    NotFound,

    /// <summary>The unit or record is not this customer's.</summary>
    OwnershipMismatch,

    /// <summary>CRM could not be reached or answered 5xx.</summary>
    SourceUnavailable,

    /// <summary>CRM refused (400/401/403) or answered outside its contract: retrying will not fix it.</summary>
    SourceRejected
}

public sealed record CustomerDocumentsResult<T>(CustomerDocumentsOutcome Outcome, T? Value = default, string? Code = null, string? Message = null)
{
    public bool IsSuccess => Outcome == CustomerDocumentsOutcome.Success;

    public static CustomerDocumentsResult<T> Ok(T value) => new(CustomerDocumentsOutcome.Success, value);

    public static CustomerDocumentsResult<T> Fail(CustomerDocumentsOutcome outcome, string code, string message) =>
        new(outcome, default, code, message);
}
