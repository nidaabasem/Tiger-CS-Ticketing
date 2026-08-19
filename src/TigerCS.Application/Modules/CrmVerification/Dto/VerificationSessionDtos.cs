namespace TigerCS.Application.Modules.CrmVerification.Dto;

/// <summary>
/// S-07's combined create+select+confirm request. Not MVP-API-Contracts.md
/// §2.4's original shape (that flow is split across four endpoints) — see
/// MVP-Implementation-Backlog.md §0.2/S-07 for the approved pilot simplification.
/// </summary>
public sealed record CreateVerificationSessionRequestDto(int UnitReferenceId, int ContactReferenceId, bool ConfirmedVerbally);

public sealed record VerificationSessionResponseDto(
    Guid VerificationSessionId,
    Guid AgentEmployeeId,
    int UnitReferenceId,
    int ContactReferenceId,
    string Status,
    bool ConfirmedVerbally,
    DateTime CreatedAtUtc,
    DateTime? ConfirmedAtUtc,
    DateTime ExpiresAtUtc,
    string? SnapshotUnitNumber,
    string? SnapshotPropertyName,
    string? SnapshotTowerName,
    string? SnapshotUnitType,
    string? SnapshotContactDisplayName,
    string? SnapshotContactChannel);

public enum VerificationSessionOutcome
{
    Success,
    UnitOrContactNotFound,
    NotFound,
    Forbidden
}

public sealed record VerificationSessionResult(VerificationSessionOutcome Outcome, VerificationSessionResponseDto? Response = null)
{
    public static VerificationSessionResult Success(VerificationSessionResponseDto response) =>
        new(VerificationSessionOutcome.Success, response);
    public static VerificationSessionResult UnitOrContactNotFound() => new(VerificationSessionOutcome.UnitOrContactNotFound);
    public static VerificationSessionResult NotFound() => new(VerificationSessionOutcome.NotFound);
    public static VerificationSessionResult Forbidden() => new(VerificationSessionOutcome.Forbidden);
}
