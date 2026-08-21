namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.15 / MVP-ERD.md §2.15 — one immutable row per
/// SLA period a ticket passes through. Never deleted and never overwritten:
/// ISSUE-023's explicit requirement is that management reporting can show
/// both the original and any changed period for a re-prioritized ticket.
///
/// <para>
/// <b>Exactly one row per ticket has <see cref="PeriodEndAtUtc"/> null</b> —
/// the current period (MVP-ERD.md §2.15). This increment only ever creates
/// that first row (<see cref="SlaChangeReason.InitialCreation"/>); closing a
/// period and opening a successor belongs to backlog S-14's priority
/// upgrade. The filtered unique index in the EF configuration enforces the
/// one-current-period invariant at the database, so it cannot be violated by
/// a future code path either.
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
