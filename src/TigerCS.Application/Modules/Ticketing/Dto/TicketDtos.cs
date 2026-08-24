namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>The normal path (FR-CH-01/FR-VER-02, MVP-API-Contracts.md §3.1): create a ticket from an already-confirmed VerificationSession, and link it back to the IntakeRecord that preceded it (MVP-ERD.md §2.9).</summary>
/// <param name="IntakeRecordId">Required. The unit-related intake record to promote. It must not already be linked to a ticket.</param>
/// <param name="VerificationSessionId">Required. A confirmed, unexpired, not-yet-consumed session owned by the caller.</param>
/// <param name="CategoryId">Required. Determines which department the ticket routes to.</param>
/// <param name="PriorityId">Required. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="RequestSummary">Required. The caller's request, in the agent's words.</param>
public sealed record CreateTicketFromVerificationRequestDto(
    long IntakeRecordId,
    Guid VerificationSessionId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

/// <summary>ISSUE-006's approved fallback: Critical/High proceeds immediately as a provisional ticket while the CRM is unreachable; there is no VerificationSession to consume.</summary>
/// <param name="IntakeRecordId">Required. The unit-related intake record to promote. It must not already be linked to a ticket.</param>
/// <param name="CategoryId">Required. Determines which department the ticket routes to.</param>
/// <param name="PriorityId">Required. 1=Critical, 2=High, 3=Medium, 4=Low. Critical/High proceed immediately; Medium/Low are answered with 200 and remain queued.</param>
/// <param name="RequestSummary">Required. The caller's request, in the agent's words.</param>
public sealed record CreateProvisionalTicketRequestDto(
    long IntakeRecordId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

/// <summary>
/// Product correction: every intake, whether unit-related or not, can be
/// converted into a ticket after selecting one of the three categories; CRM
/// verification is required only when the request is unit-related. This is
/// the non-unit-related path — there is no VerificationSession to consume,
/// and the resulting ticket carries no unit/contact reference.
/// </summary>
/// <param name="IntakeRecordId">Required. The non-unit-related intake record to promote. It must not already be linked to a ticket.</param>
/// <param name="CategoryId">Required. Determines which department the ticket routes to.</param>
/// <param name="PriorityId">Required. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="RequestSummary">Required. The caller's request, in the agent's words.</param>
public sealed record CreateTicketFromNonUnitIntakeRequestDto(
    long IntakeRecordId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

/// <summary>A category available for ticket creation (MVP-Data-Dictionary.md §2.7).</summary>
/// <param name="CategoryId">The category.</param>
/// <param name="Name">The display name.</param>
/// <param name="DepartmentId">The department this category routes to (FR-CLS-01/FR-RTE-01).</param>
public sealed record CategoryResponseDto(int CategoryId, string Name, int DepartmentId);

/// <summary>A newly created ticket (MVP-API-Contracts.md §3.1).</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="OriginatingDepartmentId">The department the ticket was raised in.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="UnitReferenceId">The verified unit, or null for a provisional ticket.</param>
/// <param name="ContactReferenceId">The verified contact, or null for a provisional ticket.</param>
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

    /// <summary>ISSUE-006's "Medium/Low remain queued" outcome — no ticket was created; the IntakeRecord now awaits CRM reconciliation instead.</summary>
    QueuedPendingVerification,

    IntakeRecordNotFound,
    IntakeRecordAlreadyLinked,
    IntakeRecordNotUnitRelated,

    /// <summary>The intake record IS unit-related — it must go through the verified or provisional path (POST /api/tickets or POST /api/tickets/provisional), not the non-unit path.</summary>
    IntakeRecordIsUnitRelated,

    VerificationSessionNotFound,
    VerificationSessionForbidden,
    VerificationSessionNotConfirmed,
    VerificationSessionAlreadyConsumed,
    VerificationSessionExpired,
    CategoryNotFound,
    PriorityNotFound,

    /// <summary>Item 9 (senior review): the Category's routed Department is missing or deactivated — never silently route a ticket to a department nobody is staffing.</summary>
    DepartmentInactive,

    /// <summary>A same-department, same-day TicketNumber collision (real DB unique-index race) — nothing else was touched; retrying the whole request is always correct.</summary>
    TicketNumberCollision
}

public sealed record TicketCreationResult(TicketCreationOutcome Outcome, TicketResponseDto? Response = null, IntakeRecordResponseDto? QueuedIntakeRecord = null)
{
    public static TicketCreationResult Success(TicketResponseDto response) => new(TicketCreationOutcome.Success, response);
    public static TicketCreationResult QueuedPendingVerification(IntakeRecordResponseDto intakeRecord) =>
        new(TicketCreationOutcome.QueuedPendingVerification, QueuedIntakeRecord: intakeRecord);
    public static TicketCreationResult Failure(TicketCreationOutcome outcome) => new(outcome);
}
