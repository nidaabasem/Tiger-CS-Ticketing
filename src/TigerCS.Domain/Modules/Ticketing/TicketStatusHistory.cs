namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.13 / MVP-Data-Dictionary.md §2.13 (ADR-0018) — append-only;
/// no update or delete path exists at any privilege level. One row per
/// change to any of the five independent dimensions (ADR-0008); ticket
/// creation seeds one row per dimension with <c>OldValue = null</c>
/// (MVP-API-Contracts.md §3.1's audit note).
///
/// <para>
/// Written directly by the Ticketing application service in the same
/// transaction as the ticket it describes, not via the event-subscriber
/// path Module-Design.md's "ownership note" describes — that path requires
/// the Outbox/event infrastructure (MVP-Implementation-Backlog.md S-05),
/// which has not been built yet. This mirrors the same pragmatic,
/// already-established pattern <c>VerificationSessionAppService</c> uses
/// for <c>AuditEntries</c> (a direct <c>IAuditEntryWriter</c> call, not an
/// event subscriber) ahead of S-05 landing.
/// </para>
/// </summary>
public class TicketStatusHistory
{
    public long TicketStatusHistoryId { get; private set; }
    public long TicketId { get; private set; }
    public TicketStatusDimension Dimension { get; private set; }
    public byte? OldValue { get; private set; }
    public byte NewValue { get; private set; }
    public Guid? ActorEmployeeId { get; private set; }
    public bool ActorIsSystem { get; private set; }
    public string? Note { get; private set; }
    public Guid CorrelationId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private TicketStatusHistory() { }

    public TicketStatusHistory(
        long ticketId,
        TicketStatusDimension dimension,
        byte? oldValue,
        byte newValue,
        Guid? actorEmployeeId,
        bool actorIsSystem,
        string? note,
        Guid correlationId,
        DateTime occurredAtUtc)
    {
        if (!actorIsSystem && actorEmployeeId is null)
        {
            throw new ArgumentException(
                "ActorEmployeeId is required unless ActorIsSystem is true.", nameof(actorEmployeeId));
        }

        TicketId = ticketId;
        Dimension = dimension;
        OldValue = oldValue;
        NewValue = newValue;
        ActorEmployeeId = actorEmployeeId;
        ActorIsSystem = actorIsSystem;
        Note = note;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }
}
