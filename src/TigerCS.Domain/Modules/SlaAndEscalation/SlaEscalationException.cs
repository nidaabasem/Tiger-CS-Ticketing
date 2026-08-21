using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Domain.Modules.SlaAndEscalation;

public abstract class SlaEscalationException(string message) : Exception(message);

/// <summary>MVP-API-Contracts.md §5.2: `409 first-response-already-recorded` — <c>Ticket.FirstHumanResponseAtUtc</c> is write-once at the ticket level (MVP-ERD.md §2.10).</summary>
public sealed class FirstResponseAlreadyRecordedException(long ticketId, DateTime recordedAtUtc)
    : SlaEscalationException($"Ticket {ticketId} already recorded its first human response at {recordedAtUtc:O} — the field is write-once.")
{
    public long TicketId { get; } = ticketId;
    public DateTime RecordedAtUtc { get; } = recordedAtUtc;
}

/// <summary>
/// The escalation level a ticket is at only ever rises (Solution-Analysis.md
/// §7.6's Level 1 → 4 hierarchy; ADR-0011 models the level as a state, not a
/// counter — the retry-count design was explicitly rejected). Without this,
/// an automatic Level 2 raised on a later breach would silently de-escalate a
/// ticket a CS Manager had already taken to Level 4.
/// </summary>
public sealed class EscalationLevelCannotBeLoweredException(long ticketId, EscalationLevel currentLevel, EscalationLevel requestedLevel)
    : SlaEscalationException($"Ticket {ticketId} is at escalation {currentLevel} — it cannot be lowered to {requestedLevel}.")
{
    public long TicketId { get; } = ticketId;
    public EscalationLevel CurrentLevel { get; } = currentLevel;
    public EscalationLevel RequestedLevel { get; } = requestedLevel;
}

/// <summary>
/// Level 4 (Chairman/CEO) is manual-only and role-gated to CS Manager/GM
/// (Solution-Analysis.md §7.6, MVP-ERD.md §2.17, MVP-API-Contracts.md §5.7).
/// A request pairing <see cref="EscalationTriggerType.ManualFlag"/> — which
/// any Agent may use — with <c>Level = 4</c> would reach Level 4 without
/// passing that gate, so the pairing is rejected outright rather than left to
/// the authorization check alone.
/// </summary>
public sealed class EscalationLevelTriggerMismatchException(byte level, EscalationTriggerType triggerType)
    : SlaEscalationException($"Escalation level {level} cannot be raised with TriggerType {triggerType}.")
{
    public byte Level { get; } = level;
    public EscalationTriggerType TriggerType { get; } = triggerType;
}
