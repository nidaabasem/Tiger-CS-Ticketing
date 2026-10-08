using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The approved SLA pause/resume rules (ISSUE-018a–d, SLA-Architecture.md §6)
/// as pure functions, so they can be asserted without a repository.
///
/// <list type="bullet">
///   <item><description>(a) The Critical SLA never pauses, whatever the status.</description></item>
///   <item><description>(b) A non-Critical Resolution SLA pauses while the ticket is Pending Customer and resumes when it returns to work.</description></item>
///   <item><description>(c) Pending Third Party is retired, but a legacy ticket in it pauses the same way (approved B) rather than crashing or silently running.</description></item>
///   <item><description>(d) The First Response SLA never pauses — and in particular cannot pause once the first human response is recorded.</description></item>
/// </list>
///
/// <para>
/// Not decided here (listed, not guessed): whether any <i>other</i> pending
/// reason (pending-internal) pauses, and the per-request-type
/// <c>PausesOnPendingInternal</c> flag, which is deliberately ignored.
/// </para>
/// </summary>
public static class SlaPauseRules
{
    /// <summary>The First Response clock never pauses (ISSUE-018d, fixed) — a constant so a test can pin it.</summary>
    public const bool FirstResponseCanPause = false;

    /// <summary>The statuses in which a pause-eligible Resolution clock is stopped.</summary>
    public static bool IsPausingStatus(TicketStatus status) =>
        status is TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty;

    public static SlaPauseReason ReasonFor(TicketStatus status) => status switch
    {
        TicketStatus.PendingCustomer => SlaPauseReason.PendingCustomer,
        TicketStatus.PendingThirdParty => SlaPauseReason.PendingThirdPartyLegacy,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only a Pending status pauses the SLA clock.")
    };

    /// <summary>
    /// Whether the Resolution clock of a period at <paramref name="priorityId"/>
    /// pauses while the ticket is in <paramref name="status"/>.
    /// </summary>
    /// <param name="priorityId">The SLA period's priority (Critical never pauses).</param>
    /// <param name="status">The ticket's current status.</param>
    /// <param name="requestTypePausesOnPendingCustomer">
    /// The per-request-type override: <c>true</c>/<c>false</c> is explicit
    /// configuration, <c>null</c> inherits the approved global rule (b). It
    /// concerns Pending Customer only; Critical is never pausable even when
    /// it says <c>true</c>.
    /// </param>
    public static bool ResolutionPauses(byte priorityId, TicketStatus status, bool? requestTypePausesOnPendingCustomer)
    {
        if (!IsPausingStatus(status))
        {
            return false;
        }

        if (priorityId == (byte)PriorityLevel.Critical)
        {
            return false;
        }

        if (status == TicketStatus.PendingCustomer && requestTypePausesOnPendingCustomer is { } overridden)
        {
            return overridden;
        }

        return true;
    }
}
