namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// <see cref="TicketInteraction.End"/> is write-once: a second end event for
/// the same conversation (an at-least-once redelivery) must not move the
/// recorded end time. The application layer treats this as "already
/// processed" and answers idempotently rather than as a failure.
/// </summary>
public sealed class TicketInteractionAlreadyEndedException(long ticketInteractionId, DateTime endedAtUtc)
    : TicketException($"Interaction {ticketInteractionId} already ended at {endedAtUtc:O}.")
{
    public long TicketInteractionId { get; } = ticketInteractionId;
    public DateTime EndedAtUtc { get; } = endedAtUtc;
}
