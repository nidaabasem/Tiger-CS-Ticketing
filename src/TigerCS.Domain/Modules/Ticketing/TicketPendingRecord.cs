namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// One structured pending period of a ticket (Workflow/SLA Configuration
/// phase 2): why the ticket was put on Pending, by whom, from which status,
/// and — once resumed — when and by whom it resumed. Append-only alongside
/// the existing <see cref="TicketStatusHistory"/> rows (which keep recording
/// the raw status transitions); this table adds the structured reason and
/// the pause window the phase-4 SLA calculation will read <b>if</b> the
/// business approves pausing — recording the window is deliberately separate
/// from deciding whether it pauses anything.
///
/// <para>
/// At most one open record (<see cref="ResumedAtUtc"/> null) exists per
/// ticket, because the status machine allows only one Pending state at a
/// time; the writing application service enforces it transactionally.
/// </para>
/// </summary>
public class TicketPendingRecord
{
    public long TicketPendingRecordId { get; private set; }
    public long TicketId { get; private set; }
    public PendingKind Kind { get; private set; }

    /// <summary>Required — a ticket is never pending without a recorded why (e.g. "Missing passport copy", "Awaiting Accounting approval").</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>The status the ticket held before entering Pending — with the current machine always <see cref="TicketStatus.InProgress"/>, stored explicitly so history never has to infer it.</summary>
    public TicketStatus PreviousStatus { get; private set; }

    public DateTime StartedAtUtc { get; private set; }
    public Guid StartedByEmployeeId { get; private set; }

    /// <summary>Null while the ticket is still pending. Set exactly once by <see cref="Resume"/>.</summary>
    public DateTime? ResumedAtUtc { get; private set; }

    public Guid? ResumedByEmployeeId { get; private set; }

    /// <summary>Shared with the status-history and audit rows written in the same transaction, so one pending action reads as one event.</summary>
    public Guid CorrelationId { get; private set; }

    private TicketPendingRecord() { }

    public TicketPendingRecord(
        long ticketId,
        PendingKind kind,
        string reason,
        TicketStatus previousStatus,
        Guid startedByEmployeeId,
        DateTime startedAtUtc,
        Guid correlationId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Kind {kind} is not a defined pending kind.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required — a ticket is never pending without a recorded why.", nameof(reason));
        }

        if (startedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("StartedByEmployeeId is required.", nameof(startedByEmployeeId));
        }

        TicketId = ticketId;
        Kind = kind;
        Reason = reason;
        PreviousStatus = previousStatus;
        StartedByEmployeeId = startedByEmployeeId;
        StartedAtUtc = startedAtUtc;
        CorrelationId = correlationId;
    }

    /// <summary>Closes this pending period — write-once; resuming an already-resumed record is a defect.</summary>
    public void Resume(Guid resumedByEmployeeId, DateTime resumedAtUtc)
    {
        if (ResumedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Pending record {TicketPendingRecordId} of ticket {TicketId} was already resumed at {ResumedAtUtc:O}.");
        }

        if (resumedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("ResumedByEmployeeId is required.", nameof(resumedByEmployeeId));
        }

        ResumedByEmployeeId = resumedByEmployeeId;
        ResumedAtUtc = resumedAtUtc;
    }

    /// <summary>The paused duration once resumed — raw data for the phase-4 pause calculation, which only applies it where configuration says pending pauses the clock.</summary>
    public TimeSpan? PausedDuration => ResumedAtUtc is { } resumedAt ? resumedAt - StartedAtUtc : null;
}
