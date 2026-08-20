namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>The normal path (FR-CH-01/FR-VER-02, MVP-API-Contracts.md §3.1): create a ticket from an already-confirmed VerificationSession, and link it back to the IntakeRecord that preceded it (MVP-ERD.md §2.9).</summary>
public sealed record CreateTicketFromVerificationRequestDto(
    long IntakeRecordId,
    Guid VerificationSessionId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

/// <summary>ISSUE-006's approved fallback: Critical/High proceeds immediately as a provisional ticket while the CRM is unreachable; there is no VerificationSession to consume.</summary>
public sealed record CreateProvisionalTicketRequestDto(
    long IntakeRecordId,
    int CategoryId,
    byte PriorityId,
    string RequestSummary);

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
    DateTime CreatedAtUtc);

public enum TicketCreationOutcome
{
    Success,

    /// <summary>ISSUE-006's "Medium/Low remain queued" outcome — no ticket was created; the IntakeRecord now awaits CRM reconciliation instead.</summary>
    QueuedPendingVerification,

    IntakeRecordNotFound,
    IntakeRecordAlreadyLinked,
    IntakeRecordNotUnitRelated,
    VerificationSessionNotFound,
    VerificationSessionForbidden,
    VerificationSessionNotConfirmed,
    VerificationSessionAlreadyConsumed,
    VerificationSessionExpired,
    CategoryNotFound,
    PriorityNotFound
}

public sealed record TicketCreationResult(TicketCreationOutcome Outcome, TicketResponseDto? Response = null, IntakeRecordResponseDto? QueuedIntakeRecord = null)
{
    public static TicketCreationResult Success(TicketResponseDto response) => new(TicketCreationOutcome.Success, response);
    public static TicketCreationResult QueuedPendingVerification(IntakeRecordResponseDto intakeRecord) =>
        new(TicketCreationOutcome.QueuedPendingVerification, QueuedIntakeRecord: intakeRecord);
    public static TicketCreationResult Failure(TicketCreationOutcome outcome) => new(outcome);
}
