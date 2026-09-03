// Alias needed because this type's own ResolutionOutcome *property* shadows
// the ResolutionOutcome *enum* by simple name inside instance methods (see
// Ticket.cs's identical alias for the same reason).
using ResolutionOutcomeValue = TigerCS.Domain.Modules.Ticketing.ResolutionOutcome;

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.14 / MVP-Data-Dictionary.md §2.14 — one row per resolve
/// cycle (BR-011: <see cref="ResolutionNote"/> is mandatory regardless of
/// outcome). On Reopen (FR-RES-04), the current row's <see cref="IsCurrent"/>
/// flips to false via <see cref="Archive"/> — history preserved, never
/// deleted — and a new row is created on the next resolution, never
/// overwriting the old one (MVP-ERD.md §2.14's Reopen-driven history model).
/// </summary>
public class TicketResolution
{
    public long TicketResolutionId { get; private set; }
    public long TicketId { get; private set; }
    public ResolutionOutcome ResolutionOutcome { get; private set; }
    public string ResolutionNote { get; private set; } = string.Empty;
    public byte? ReasonCode { get; private set; }
    public long? DuplicateOfTicketId { get; private set; }
    public Guid ResolvingEmployeeId { get; private set; }
    public DateTime ResolvedAtUtc { get; private set; }
    public bool IsCurrent { get; private set; }

    private TicketResolution() { }

    /// <summary>
    /// Reopen (FR-RES-04): this resolution is no longer the ticket's live
    /// outcome — it becomes history, never deleted. One-way by design; the
    /// next resolve cycle creates a new row rather than reviving this one.
    /// </summary>
    public void Archive() => IsCurrent = false;

    public TicketResolution(
        long ticketId,
        ResolutionOutcome resolutionOutcome,
        string resolutionNote,
        byte? reasonCode,
        long? duplicateOfTicketId,
        Guid resolvingEmployeeId,
        DateTime resolvedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(resolutionNote))
        {
            throw new ArgumentException("ResolutionNote is required (BR-011).", nameof(resolutionNote));
        }

        if (resolutionOutcome == ResolutionOutcomeValue.Duplicate && duplicateOfTicketId is null)
        {
            throw new ArgumentException(
                "DuplicateOfTicketId is required when ResolutionOutcome is Duplicate.", nameof(duplicateOfTicketId));
        }

        if (resolvingEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("ResolvingEmployeeId is required.", nameof(resolvingEmployeeId));
        }

        TicketId = ticketId;
        ResolutionOutcome = resolutionOutcome;
        ResolutionNote = resolutionNote;
        ReasonCode = reasonCode;
        DuplicateOfTicketId = duplicateOfTicketId;
        ResolvingEmployeeId = resolvingEmployeeId;
        ResolvedAtUtc = resolvedAtUtc;
        IsCurrent = true;
    }
}
