namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md <c>TicketSlaPausePeriods</c> — one row per time the
/// Resolution SLA clock of a ticket's SLA period was paused (ISSUE-018,
/// SLA-Architecture.md §6/§8). History, never deleted and never edited after
/// it is closed: elapsed time, breaches and SLA history are never erased
/// (ISSUE-023's guarantee), so a pause is recorded beside the period rather
/// than rewriting it.
///
/// <para>
/// <b>At most one open pause per ticket</b> (filtered unique index on
/// <c>ResumedAtUtc IS NULL</c>): a repeated Pending Customer entry or a
/// duplicate resume can neither double-count nor leave two open rows.
/// </para>
///
/// <para>
/// Only the <b>Resolution</b> clock pauses. First Response never pauses
/// (<see cref="SlaPauseRules.FirstResponseCanPause"/>), so no row ever refers
/// to it.
/// </para>
/// </summary>
public class TicketSlaPausePeriod
{
    public long TicketSlaPausePeriodId { get; private set; }
    public long TicketId { get; private set; }
    public long TicketSlaInstanceId { get; private set; }

    public SlaPauseReason Reason { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    /// <summary>Null while the pause is open (the clock is stopped).</summary>
    public DateTime? ResumedAtUtc { get; private set; }

    /// <summary>The Resolution due timestamp when the pause began — held, unchanged, for the whole pause.</summary>
    public DateTime ResolutionDueBeforeAtUtc { get; private set; }

    /// <summary>The Resolution due timestamp after this pause was closed out (extended by the paused time). Null while open.</summary>
    public DateTime? ResolutionDueAfterAtUtc { get; private set; }

    /// <summary>True when the pause ended because the ticket was Resolved/Closed rather than returned to work.</summary>
    public bool EndedByResolution { get; private set; }

    private TicketSlaPausePeriod() { }

    public TicketSlaPausePeriod(
        long ticketId, long ticketSlaInstanceId, SlaPauseReason reason, DateTime startedAtUtc, DateTime resolutionDueBeforeAtUtc)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException($"Reason {reason} is not a defined pause reason.", nameof(reason));
        }

        TicketId = ticketId;
        TicketSlaInstanceId = ticketSlaInstanceId;
        Reason = reason;
        StartedAtUtc = startedAtUtc;
        ResolutionDueBeforeAtUtc = resolutionDueBeforeAtUtc;
    }

    public bool IsOpen => ResumedAtUtc is null;

    /// <summary>Wall-clock length of the pause; for an open pause, measured to <paramref name="asOfUtc"/>.</summary>
    public TimeSpan DurationAsOf(DateTime asOfUtc)
    {
        var end = ResumedAtUtc ?? asOfUtc;
        return end > StartedAtUtc ? end - StartedAtUtc : TimeSpan.Zero;
    }

    /// <summary>
    /// Closes the pause. One-way and not repeatable: a second call throws, so
    /// callers test <see cref="IsOpen"/> (the application service does) and a
    /// duplicate resume is a no-op there, never a second close here.
    /// </summary>
    public void Close(DateTime resumedAtUtc, DateTime resolutionDueAfterAtUtc, bool endedByResolution)
    {
        if (ResumedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"SLA pause {TicketSlaPausePeriodId} of ticket {TicketId} was already closed at {ResumedAtUtc:O}.");
        }

        if (resumedAtUtc < StartedAtUtc)
        {
            throw new ArgumentException("A pause cannot end before it started.", nameof(resumedAtUtc));
        }

        ResumedAtUtc = resumedAtUtc;
        ResolutionDueAfterAtUtc = resolutionDueAfterAtUtc;
        EndedByResolution = endedByResolution;
    }
}
