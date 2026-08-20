namespace TigerCS.Application.Modules.CustomerVerification.Dto;

/// <summary>
/// S-07's combined create+select+confirm request. Not MVP-API-Contracts.md
/// §2.4's original shape (that flow is split across four endpoints) — see
/// MVP-Implementation-Backlog.md §0.2/S-07 for the approved pilot simplification.
/// Channel-neutral by design: <paramref name="Confirmed"/> and
/// <paramref name="VerificationMethod"/> replace an earlier phone-specific
/// <c>ConfirmedVerbally</c> field name — this endpoint is unmerged with no
/// released consumers, so there is no backward-compatibility reason to keep
/// a channel assumption on the wire. <paramref name="VerificationMethod"/>
/// must be the name of a <see cref="TigerCS.Domain.Modules.CustomerVerification.VerificationMethod"/>
/// value (e.g. "ManualAgentConfirmation" — this pilot's only supported
/// value today; the others exist so a future channel needs no API change).
/// </summary>
public sealed record CreateVerificationSessionRequestDto(int UnitReferenceId, int ContactReferenceId, bool Confirmed, string VerificationMethod);

public sealed record VerificationSessionResponseDto(
    Guid VerificationSessionId,
    Guid AgentEmployeeId,
    int UnitReferenceId,
    int ContactReferenceId,
    string Status,
    bool Confirmed,
    string? VerificationMethod,
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
