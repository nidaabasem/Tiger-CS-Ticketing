namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.15 / MVP-ERD.md §2.15 — one immutable row per
/// SLA period a ticket passes through. Never deleted and never overwritten:
/// ISSUE-023's explicit requirement is that management reporting can show
/// both the original and any changed period for a re-prioritized ticket.
///
/// <para>
/// <b>Exactly one row per ticket has <see cref="PeriodEndAtUtc"/> null</b> —
/// the current period (MVP-ERD.md §2.15). Two code paths create rows: the
/// initial period (<see cref="SlaChangeReason.InitialCreation"/>) and the
/// approved Reopen rule's successor cycle
/// (<see cref="SlaChangeReason.Reopen"/>), which ends the closed period
/// first. Backlog S-14's priority upgrade will be the third. The filtered
/// unique index in the EF configuration enforces the one-current-period
/// invariant at the database, so it cannot be violated by a future code path
/// either — which is exactly why <see cref="OpenReopenCycle"/> cannot be
/// called without <see cref="EndPeriod"/> having run on the predecessor.
/// </para>
///
/// <para>
/// <b>The breach flags are the highest-consequence integrity rule in this
/// schema</b> (MVP-ERD.md §2.15, Architecture-Review-Checklist.md): once
/// <c>true</c> they are never reset to <c>false</c> by any code path.
/// <see cref="MarkBreached"/> is the only writer and is one-way; there is no
/// setter, no "clear", and no overload that takes a bool.
/// </para>
/// </summary>
public class TicketSlaInstance
{
    public long TicketSlaInstanceId { get; private set; }
    public long TicketId { get; private set; }
    public byte PriorityId { get; private set; }

    /// <summary>The approved SLA clock-start moment for this period (ISSUE-001 Option C — ticket creation, for the initial period).</summary>
    public DateTime PeriodStartAtUtc { get; private set; }

    /// <summary>Null marks this as the ticket's current period. Only a priority change closes a period (backlog S-14, not this increment).</summary>
    public DateTime? PeriodEndAtUtc { get; private set; }

    public DateTime FirstResponseDueAtUtc { get; private set; }
    public DateTime ResolutionDueAtUtc { get; private set; }

    /// <summary>Immutable once true — see this type's remarks. Write via <see cref="MarkBreached"/> only.</summary>
    public bool FirstResponseBreached { get; private set; }

    /// <summary>Immutable once true — see this type's remarks. Write via <see cref="MarkBreached"/> only.</summary>
    public bool ResolutionBreached { get; private set; }

    public SlaChangeReason ChangeReason { get; private set; }

    /// <summary>Required only for <see cref="SlaChangeReason.Downgrade"/>, which no pilot code path can produce (MVP-Implementation-Backlog.md §0 hard-disables downgrades). Never client-supplied on any endpoint (Finding DR-05).</summary>
    public Guid? ApprovedByEmployeeId { get; private set; }

    private TicketSlaInstance() { }

    private TicketSlaInstance(
        long ticketId,
        byte priorityId,
        DateTime periodStartAtUtc,
        DateTime firstResponseDueAtUtc,
        DateTime resolutionDueAtUtc,
        SlaChangeReason changeReason)
    {
        TicketId = ticketId;
        PriorityId = priorityId;
        PeriodStartAtUtc = periodStartAtUtc;
        FirstResponseDueAtUtc = firstResponseDueAtUtc;
        ResolutionDueAtUtc = resolutionDueAtUtc;
        ChangeReason = changeReason;
    }

    /// <summary>
    /// Opens a ticket's first SLA period. The only factory this increment
    /// exposes: <see cref="SlaChangeReason.Upgrade"/> rows belong to backlog
    /// S-14 and <see cref="SlaChangeReason.Downgrade"/> rows to the
    /// post-pilot approval workflow, so neither has a constructor here that
    /// could be called by accident.
    /// </summary>
    public static TicketSlaInstance OpenInitialPeriod(
        long ticketId,
        byte priorityId,
        DateTime clockStartAtUtc,
        DateTime firstResponseDueAtUtc,
        DateTime resolutionDueAtUtc)
    {
        if (firstResponseDueAtUtc < clockStartAtUtc)
        {
            throw new ArgumentException("FirstResponseDueAtUtc cannot precede the clock-start moment.", nameof(firstResponseDueAtUtc));
        }

        if (resolutionDueAtUtc < clockStartAtUtc)
        {
            throw new ArgumentException("ResolutionDueAtUtc cannot precede the clock-start moment.", nameof(resolutionDueAtUtc));
        }

        return new TicketSlaInstance(
            ticketId, priorityId, clockStartAtUtc, firstResponseDueAtUtc, resolutionDueAtUtc, SlaChangeReason.InitialCreation);
    }

    /// <summary>
    /// Opens the successor period the approved Reopen rule requires: a new
    /// <b>Resolution</b> cycle starting at the reopen moment, while the First
    /// Response clock is carried across untouched.
    ///
    /// <para>
    /// <b>First Response never restarts.</b> Its due timestamp and its breach
    /// flag are copied verbatim from the period being succeeded, so the
    /// original first-response result survives every reopen: a target already
    /// missed stays missed (the flag is carried, so the sweep never re-flags
    /// it), and a target still pending keeps its original deadline rather than
    /// being generously pushed out. That is why this factory — unlike
    /// <see cref="OpenInitialPeriod"/> — does NOT require
    /// <paramref name="carriedFirstResponseDueAtUtc"/> to fall on or after the
    /// period start: for a reopened ticket that timestamp is deliberately
    /// historical.
    /// </para>
    /// </summary>
    /// <param name="ticketId">The ticket being reopened.</param>
    /// <param name="priorityId">The priority the new cycle's policy is selected by.</param>
    /// <param name="reopenedAtUtc">The reopen moment — this cycle's clock start.</param>
    /// <param name="carriedFirstResponseDueAtUtc">The predecessor period's First Response deadline, copied unchanged.</param>
    /// <param name="carriedFirstResponseBreached">The predecessor period's First Response breach flag, copied unchanged.</param>
    /// <param name="resolutionDueAtUtc">The newly computed Resolution deadline for this cycle.</param>
    public static TicketSlaInstance OpenReopenCycle(
        long ticketId,
        byte priorityId,
        DateTime reopenedAtUtc,
        DateTime carriedFirstResponseDueAtUtc,
        bool carriedFirstResponseBreached,
        DateTime resolutionDueAtUtc)
    {
        if (resolutionDueAtUtc < reopenedAtUtc)
        {
            throw new ArgumentException(
                "ResolutionDueAtUtc cannot precede the reopen moment.", nameof(resolutionDueAtUtc));
        }

        return new TicketSlaInstance(
            ticketId, priorityId, reopenedAtUtc, carriedFirstResponseDueAtUtc, resolutionDueAtUtc, SlaChangeReason.Reopen)
        {
            FirstResponseBreached = carriedFirstResponseBreached
        };
    }

    /// <summary>
    /// Ends this period at <paramref name="periodEndAtUtc"/>, making room for
    /// a successor under the one-current-period invariant. One-way: an ended
    /// period is history and is never reopened, never deleted, and never
    /// re-flagged — its breach flags are frozen exactly as the sweep left
    /// them, which is what keeps the original cycle honestly reportable
    /// (ISSUE-023).
    /// </summary>
    public void EndPeriod(DateTime periodEndAtUtc)
    {
        if (PeriodEndAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"TicketSlaInstance {TicketSlaInstanceId} is already ended — an SLA period closes exactly once.");
        }

        if (periodEndAtUtc < PeriodStartAtUtc)
        {
            throw new ArgumentException(
                "PeriodEndAtUtc cannot precede the period's own start.", nameof(periodEndAtUtc));
        }

        PeriodEndAtUtc = periodEndAtUtc;
    }

    public DateTime DueAtUtcFor(SlaDeadlineType deadlineType) => deadlineType switch
    {
        SlaDeadlineType.FirstResponse => FirstResponseDueAtUtc,
        SlaDeadlineType.Resolution => ResolutionDueAtUtc,
        _ => throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.")
    };

    public bool IsBreached(SlaDeadlineType deadlineType) => deadlineType switch
    {
        SlaDeadlineType.FirstResponse => FirstResponseBreached,
        SlaDeadlineType.Resolution => ResolutionBreached,
        _ => throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.")
    };

    /// <summary>
    /// Records a breach of one deadline. One-way and idempotent by
    /// construction: calling it on an already-breached deadline changes
    /// nothing and returns <c>false</c>, so a caller can tell a first
    /// detection from a repeat without inspecting the flag itself.
    /// </summary>
    /// <returns><c>true</c> if this call is what flipped the flag.</returns>
    public bool MarkBreached(SlaDeadlineType deadlineType)
    {
        switch (deadlineType)
        {
            case SlaDeadlineType.FirstResponse:
                if (FirstResponseBreached)
                {
                    return false;
                }

                FirstResponseBreached = true;
                return true;

            case SlaDeadlineType.Resolution:
                if (ResolutionBreached)
                {
                    return false;
                }

                ResolutionBreached = true;
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(deadlineType), deadlineType, "Unknown SLA deadline type.");
        }
    }
}
