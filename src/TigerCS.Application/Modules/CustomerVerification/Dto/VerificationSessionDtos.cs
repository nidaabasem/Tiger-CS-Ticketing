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
/// <param name="UnitReferenceId">Required. The <c>unitReferenceId</c> of an already-looked-up unit.</param>
/// <param name="ContactReferenceId">Required. The <c>contactReferenceId</c> of one of that unit's contacts.</param>
/// <param name="Confirmed">Required, and must be <c>true</c> — a request with <c>false</c> is rejected with 400.</param>
/// <param name="VerificationMethod">Required. One of ManualAgentConfirmation, AuthenticatedDigitalUser, Otp, FaceToFaceDocumentCheck, Other. Case-sensitive.</param>
public sealed record CreateVerificationSessionRequestDto(int UnitReferenceId, int ContactReferenceId, bool Confirmed, string VerificationMethod);

/// <summary>A verification session, with the immutable snapshot taken at verification time.</summary>
/// <param name="VerificationSessionId">The session. Pass this to ticket creation or reconciliation.</param>
/// <param name="AgentEmployeeId">The agent who owns the session. Only they may read it back.</param>
/// <param name="UnitReferenceId">The verified unit.</param>
/// <param name="ContactReferenceId">The verified contact.</param>
/// <param name="Status">One of InProgress, Confirmed, Consumed, Expired, Abandoned.</param>
/// <param name="Confirmed">Whether the agent confirmed the match.</param>
/// <param name="VerificationMethod">How the match was confirmed; null until confirmed.</param>
/// <param name="CreatedAtUtc">When the session was created, in UTC.</param>
/// <param name="ConfirmedAtUtc">When it was confirmed, in UTC; null until confirmed.</param>
/// <param name="ExpiresAtUtc">When the session stops being usable for ticket creation, in UTC.</param>
/// <param name="SnapshotUnitNumber">The unit number as it read at verification time.</param>
/// <param name="SnapshotPropertyName">The property name as it read at verification time.</param>
/// <param name="SnapshotTowerName">The tower name as it read at verification time.</param>
/// <param name="SnapshotUnitType">The unit type as it read at verification time.</param>
/// <param name="SnapshotContactDisplayName">The contact's name as it read at verification time.</param>
/// <param name="SnapshotContactChannel">The contact's channel as it read at verification time.</param>
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
