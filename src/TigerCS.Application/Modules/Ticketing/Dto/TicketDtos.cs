namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Create a ticket from an IntakeRecord. Customer information from CRM,
/// PACT, or Tasleeh is attached when available; lack of a match does not
/// prevent ticket creation — Ticket Category is the only thing every ticket
/// requires (business-rule change: customer lookup is enrichment, never a
/// creation gate, for unit-related and non-unit-related intakes alike).
/// </summary>
/// <param name="IntakeRecordId">Required. The intake record to promote. It must not already be linked to a ticket.</param>
/// <param name="UnitReferenceId">
/// Optional. The local reference id of a unit the agent selected from a
/// customer-lookup match (<c>GET /api/intake-records/{id}/customer-lookup</c>).
/// Must be supplied together with <paramref name="ContactReferenceId"/> or
/// not at all — never one without the other.
/// </param>
/// <param name="ContactReferenceId">Optional. The matching contact's local reference id — see <paramref name="UnitReferenceId"/>.</param>
/// <param name="CategoryId">Required. Determines which department the ticket routes to.</param>
/// <param name="PriorityId">Required. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="RequestSummary">Required. The caller's request, in the agent's words.</param>
public sealed record CreateTicketRequestDto(
    long IntakeRecordId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

/// <summary>A newly created ticket (MVP-API-Contracts.md §3.1).</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="OriginatingDepartmentId">The department the ticket was raised in.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="UnitReferenceId">The matched unit, or null when no customer match was linked at creation.</param>
/// <param name="ContactReferenceId">The matched contact, or null when no customer match was linked at creation.</param>
/// <param name="CategoryId">The ticket's category.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="EscalationLevel">One of None, Level1, Level2, Level3, Level4.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
/// <param name="RowVersion">The concurrency token, Base64-encoded. Send it back on any subsequent write to this ticket.</param>
public sealed record TicketResponseDto(
    long TicketId,
    string TicketNumber,
    int OriginatingDepartmentId,
    int CurrentDepartmentId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int CategoryId,
    byte PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string EscalationLevel,
    string SlaState,
    string RequestSummary,
    DateTime CreatedAtUtc,
    string RowVersion);

public enum TicketCreationOutcome
{
    Success,

    IntakeRecordNotFound,
    IntakeRecordAlreadyLinked,

    /// <summary>UnitReferenceId and ContactReferenceId were not both supplied or both omitted — a customer match is always a unit+contact pair, never one alone.</summary>
    UnitOrContactReferenceMismatch,

    /// <summary>UnitReferenceId did not resolve to a real, previously cached unit reference.</summary>
    UnitReferenceNotFound,

    /// <summary>ContactReferenceId did not resolve to a real, previously cached contact reference.</summary>
    ContactReferenceNotFound,

    /// <summary>Ticket Category is required for every ticket.</summary>
    CategoryNotFound,
    PriorityNotFound,

    /// <summary>Item 9 (senior review): the Category's routed Department is missing or deactivated — never silently route a ticket to a department nobody is staffing.</summary>
    DepartmentInactive,

    /// <summary>The IntakeRecord named a Department and the selected Category routes to a different one — the Category dropdown never offers this combination, so this only fires against a request built outside it.</summary>
    CategoryDepartmentMismatch,

    /// <summary>A same-department, same-day TicketNumber collision (real DB unique-index race) — nothing else was touched; retrying the whole request is always correct.</summary>
    TicketNumberCollision
}

public sealed record TicketCreationResult(TicketCreationOutcome Outcome, TicketResponseDto? Response = null)
{
    public static TicketCreationResult Success(TicketResponseDto response) => new(TicketCreationOutcome.Success, response);
    public static TicketCreationResult Failure(TicketCreationOutcome outcome) => new(outcome);
}
