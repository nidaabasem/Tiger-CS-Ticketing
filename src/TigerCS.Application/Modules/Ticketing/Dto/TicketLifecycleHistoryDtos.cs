namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// One recorded change to one of the ticket's five independent lifecycle
/// dimensions (ADR-0008) — the append-only <c>TicketStatusHistory</c> row, as
/// served to Ticket Details.
///
/// <para>
/// Values are rendered as their enum names rather than the stored bytes, so no
/// client re-implements the byte mapping. <see cref="OldValue"/> is null for
/// the seeding rows a ticket is created with.
/// </para>
/// </summary>
/// <param name="Dimension">Which dimension changed — TicketStatus, VerificationStatus, EscalationLevel, SlaState or ResolutionOutcome.</param>
/// <param name="OldValue">The previous value's name; null when there was none.</param>
/// <param name="NewValue">The new value's name.</param>
/// <param name="ActorEmployeeId">Who made the change; null when <paramref name="ActorIsSystem"/> is true.</param>
/// <param name="ActorIsSystem">True when a background job or automation made the change rather than a person.</param>
/// <param name="Note">The free text recorded with the change — a reopen reason, a resolution note, a breach explanation.</param>
/// <param name="CorrelationId">Ties rows written by one action together.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
public sealed record TicketStatusHistoryEntryDto(
    string Dimension,
    string? OldValue,
    string NewValue,
    Guid? ActorEmployeeId,
    bool ActorIsSystem,
    string? Note,
    Guid CorrelationId,
    DateTime OccurredAtUtc);

/// <summary>
/// A ticket's lifecycle history, oldest first — the read model behind
/// <c>GET /api/tickets/{ticketId}/history</c>.
/// </summary>
/// <param name="Entries">Every recorded dimension change, oldest first.</param>
public sealed record TicketLifecycleHistoryDto(IReadOnlyList<TicketStatusHistoryEntryDto> Entries);
