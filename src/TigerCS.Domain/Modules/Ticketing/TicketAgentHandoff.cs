namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Where a piece of pending human work stands. <b>TigerCS-owned values</b> —
/// deliberately not Genesys-native status names, because none has been
/// confirmed and Ticketing must not become a mirror of another system's
/// vocabulary.
/// </summary>
public enum AgentHandoffStatus : byte
{
    /// <summary>
    /// No human handoff is required for this interaction. <b>No row ever
    /// stores this</b> — a <see cref="TicketAgentHandoff"/> exists only once
    /// human involvement has actually been requested. It is the value the
    /// ticket-level view reports when no handoff row exists, so "does this
    /// ticket need a human?" has an answer either way.
    /// </summary>
    NotRequired = 1,

    /// <summary>A human is needed and none has taken it — the state the agent work list is built on.</summary>
    WaitingForAgent = 2,

    /// <summary>An agent is named (by Genesys routing, or by taking it in TigerCS) but work has not started.</summary>
    Assigned = 3,

    /// <summary>An agent is actively handling it.</summary>
    InProgress = 4,

    /// <summary>The human work is done. <b>This says nothing about the ticket</b>, which continues under the existing lifecycle.</summary>
    Completed = 5,

    /// <summary>The human work is no longer needed (a duplicate request, the customer resolved it themselves, the business case went away).</summary>
    Cancelled = 6
}

/// <summary>
/// How the human is expected to continue the interaction.
///
/// <para>
/// <b>Callback is one mode, not the concept.</b> Modelling all pending human
/// work as a callback would be phone-shaped thinking: a website chat, a
/// WhatsApp thread and a social-media message each continue differently, and
/// several of them never involve dialling anyone.
/// </para>
///
/// <para>
/// <b>Never inferred from the channel.</b> Which mode applies to which
/// channel is Genesys' behaviour to state, and it has not stated it — so the
/// mode is whatever the caller supplies, normalized into these values, and
/// stays <c>null</c> when nothing was supplied. TigerCS does not guess that
/// "phone means callback"; it records what it is told. Executing any of these
/// is Genesys' job — see <see cref="TicketAgentHandoff"/>.
/// </para>
/// </summary>
public enum AgentHandoffMode : byte
{
    /// <summary>Reach the customer back on a voice call (typically phone, but never assumed from the channel).</summary>
    Callback = 1,

    /// <summary>Resume the same chat conversation, where the platform supports resuming it.</summary>
    ContinueChat = 2,

    /// <summary>Reply in the channel the customer used (a WhatsApp thread, a social-media message).</summary>
    ReplyInChannel = 3,

    /// <summary>A human takes over an interaction a bot or virtual agent was handling.</summary>
    HumanTakeover = 4
}

/// <summary>
/// One piece of <b>pending human work</b> raised from a customer interaction:
/// something needs a human agent, on any channel.
///
/// <para>
/// <b>Why this is not "a callback".</b> Genesys carries phone, website chat,
/// chatbot, WhatsApp, social media and whatever it adds next. On every one of
/// them the same thing can happen — the routing or the virtual agent decides
/// a human is needed, and no human is immediately available. The business
/// requirement is identical across channels (do not lose the inquiry; make it
/// visible to agents); only the <see cref="Mode"/> of continuing differs, and
/// a callback is just one of those modes.
/// </para>
///
/// <para>
/// <b>Genesys owns the channel; TigerCS owns the business state.</b> Nothing
/// here dials a number, sends a WhatsApp message, resumes a chat, or routes to
/// an agent. Genesys does all of that. This row exists so the work is visible
/// in TigerCS, attached to its ticket and its context, and so the business can
/// see how long a customer has been waiting for a human. TigerCS is not a
/// second routing engine.
/// </para>
///
/// <para>
/// <b>Separate from the ticket's own lifecycle.</b> Completing the human work
/// does not close, resolve or otherwise touch the <see cref="Ticket"/> — a
/// customer whose chat was handled may still have an NOC workflow running for
/// days. The two statuses are read together and never conflated.
/// </para>
///
/// <para>
/// <b>Separate from the interaction's lifecycle too.</b> A customer who closes
/// the browser while waiting ends the <see cref="TicketInteraction"/>, and the
/// pending work deliberately survives that: the business case did not go away
/// because the live session did. That independence is exactly why this is its
/// own row rather than fields on the interaction, which is a settled audit
/// record once ended.
/// </para>
///
/// <para>
/// <b>At most one open handoff per interaction.</b> <see cref="ResolvedAtUtc"/>
/// is null while the work is outstanding, and a filtered unique index on
/// (interaction, unresolved) is the database-level half of "a retried Genesys
/// handoff event never creates a second work item" — the same shape as the
/// conversation-id uniqueness behind "one inquiry, one ticket".
/// </para>
/// </summary>
public class TicketAgentHandoff
{
    public const int RequestReasonMaxLength = 500;
    public const int ResolutionNoteMaxLength = 500;
    public const int ExternalIdMaxLength = 64;

    public long TicketAgentHandoffId { get; private set; }

    public long TicketId { get; private set; }

    /// <summary>The interaction this arose from — and, through it, the Genesys conversation, so the agent can open the transcript that led here.</summary>
    public long TicketInteractionId { get; private set; }

    /// <summary>The department the work belongs to, copied at request time from the ticket's current department so the work list can be scoped without joining.</summary>
    public int DepartmentId { get; private set; }

    /// <summary>The channel the customer used — what the agent needs to know to continue, and why a single "callback" model would not do.</summary>
    public byte ChannelId { get; private set; }

    public AgentHandoffStatus Status { get; private set; }

    /// <summary>How the human is expected to continue, when the caller said. Null means "not stated" — never a channel-derived guess.</summary>
    public AgentHandoffMode? Mode { get; private set; }

    /// <summary>Why a human was needed, as reported (a virtual agent's escalation reason, a routing note). Free text: no vocabulary for this has been confirmed. Null when not supplied.</summary>
    public string? RequestReason { get; private set; }

    /// <summary>Genesys' own identifier for this routing task / work item, when it supplies one. Unique across the table when present; external identifier only, never a foreign key.</summary>
    public string? ExternalWorkItemId { get; private set; }

    public DateTime RequestedAtUtc { get; private set; }

    /// <summary>The TigerCS employee handling it, when known. Null while waiting, and null when Genesys names an agent TigerCS cannot map to an employee.</summary>
    public Guid? AssignedEmployeeId { get; private set; }

    /// <summary>Genesys' own agent identifier, when supplied — recorded verbatim even if no TigerCS employee matches it.</summary>
    public string? GenesysAgentId { get; private set; }

    public DateTime? AssignedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }

    /// <summary>When the work reached <see cref="AgentHandoffStatus.Completed"/> — distinct from <see cref="ResolvedAtUtc"/>, which a cancellation also sets.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>When the work stopped being outstanding, by either completion or cancellation. Null exactly while it is still pending — the filtered unique index depends on that.</summary>
    public DateTime? ResolvedAtUtc { get; private set; }

    /// <summary>What the agent recorded on finishing or cancelling. Required on cancellation — work is never dropped without a why.</summary>
    public string? ResolutionNote { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Whether the work is still outstanding — the agent work list's filter, and the invariant the database enforces one of per interaction.</summary>
    public bool IsOpen => ResolvedAtUtc is null;

    /// <summary>True for every stored row: a handoff exists only because a human was asked for. Present so the ticket-level projection and this row answer the same question the same way.</summary>
    public bool RequiresHumanAgent => Status != AgentHandoffStatus.NotRequired;

    private TicketAgentHandoff() { }

    /// <summary>
    /// Records that an interaction needs a human. The status depends on
    /// whether one is already taking it: Genesys performing a live transfer
    /// reports the agent, and the work is <see cref="AgentHandoffStatus.Assigned"/>
    /// from the start; nobody available means
    /// <see cref="AgentHandoffStatus.WaitingForAgent"/> and the work list.
    /// Either way it is <b>the same ticket and the same interaction</b> — a
    /// handoff never creates a ticket.
    /// </summary>
    public TicketAgentHandoff(
        long ticketId,
        long ticketInteractionId,
        int departmentId,
        byte channelId,
        DateTime requestedAtUtc,
        DateTime createdAtUtc,
        AgentHandoffMode? mode = null,
        string? requestReason = null,
        string? externalWorkItemId = null,
        Guid? assignedEmployeeId = null,
        string? genesysAgentId = null,
        DateTime? assignedAtUtc = null)
    {
        if (departmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(departmentId), "DepartmentId is required — pending human work always belongs to a department.");
        }

        if (channelId == 0)
        {
            throw new ArgumentException("ChannelId is required.", nameof(channelId));
        }

        if (mode is { } suppliedMode && !Enum.IsDefined(suppliedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), $"Mode {suppliedMode} is not a defined handoff mode.");
        }

        TicketId = ticketId;
        TicketInteractionId = ticketInteractionId;
        DepartmentId = departmentId;
        ChannelId = channelId;
        RequestedAtUtc = requestedAtUtc;
        CreatedAtUtc = createdAtUtc;
        Mode = mode;
        RequestReason = Truncate(requestReason, RequestReasonMaxLength);
        ExternalWorkItemId = Truncate(externalWorkItemId, ExternalIdMaxLength);
        GenesysAgentId = Truncate(genesysAgentId, ExternalIdMaxLength);

        var agentIsKnown = assignedEmployeeId is not null || GenesysAgentId is not null;
        AssignedEmployeeId = assignedEmployeeId;
        AssignedAtUtc = agentIsKnown ? assignedAtUtc ?? requestedAtUtc : null;
        Status = agentIsKnown ? AgentHandoffStatus.Assigned : AgentHandoffStatus.WaitingForAgent;
    }

    /// <summary>
    /// Names (or renames) the agent handling the work — whether Genesys
    /// routed it or an agent took it in TigerCS.
    ///
    /// <para>
    /// <b>Idempotent by design.</b> Re-reporting the same agent changes
    /// nothing and does not move <see cref="AssignedAtUtc"/>, so a redelivered
    /// Genesys assignment event is harmless. Assigning work already in
    /// progress is allowed (a real reassignment mid-handling) and does not
    /// rewind <see cref="Status"/>.
    /// </para>
    /// </summary>
    public void AssignTo(Guid? employeeId, string? genesysAgentId, DateTime assignedAtUtc)
    {
        EnsureOpen();

        if (employeeId is null && string.IsNullOrWhiteSpace(genesysAgentId))
        {
            throw new ArgumentException(
                "An assignment must name someone — a TigerCS employee, a Genesys agent id, or both.", nameof(employeeId));
        }

        var normalizedGenesysAgentId = Truncate(genesysAgentId, ExternalIdMaxLength);
        var sameAgent = AssignedEmployeeId == employeeId
            && string.Equals(GenesysAgentId, normalizedGenesysAgentId, StringComparison.Ordinal);

        if (sameAgent && Status is AgentHandoffStatus.Assigned or AgentHandoffStatus.InProgress)
        {
            return;
        }

        AssignedEmployeeId = employeeId;
        if (normalizedGenesysAgentId is not null)
        {
            GenesysAgentId = normalizedGenesysAgentId;
        }

        AssignedAtUtc = assignedAtUtc;

        // Never rewind: reassigning work someone is already handling keeps it
        // in progress.
        if (Status == AgentHandoffStatus.WaitingForAgent)
        {
            Status = AgentHandoffStatus.Assigned;
        }
    }

    /// <summary>
    /// The agent has begun handling the work. Idempotent: calling it again
    /// does not move <see cref="StartedAtUtc"/>.
    /// </summary>
    public void Start(Guid employeeId, DateTime startedAtUtc)
    {
        EnsureOpen();

        if (Status == AgentHandoffStatus.InProgress)
        {
            return;
        }

        // Starting is also taking it: an agent who picks unassigned work off
        // the list becomes its assignee, without a separate claim step.
        AssignedEmployeeId ??= employeeId;
        AssignedAtUtc ??= startedAtUtc;
        StartedAtUtc = startedAtUtc;
        Status = AgentHandoffStatus.InProgress;
    }

    /// <summary>
    /// The human work is finished. <b>Deliberately touches nothing on the
    /// ticket</b> — the ticket keeps following the existing TigerCS workflow,
    /// and the two statuses are read side by side.
    /// </summary>
    public void Complete(Guid employeeId, DateTime completedAtUtc, string? resolutionNote)
    {
        EnsureOpen();

        AssignedEmployeeId ??= employeeId;
        AssignedAtUtc ??= completedAtUtc;
        StartedAtUtc ??= completedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ResolvedAtUtc = completedAtUtc;
        ResolutionNote = Truncate(resolutionNote, ResolutionNoteMaxLength);
        Status = AgentHandoffStatus.Completed;
    }

    /// <summary>
    /// The human work is no longer needed. A reason is required: pending
    /// customer work is never dropped without a recorded why — the same rule
    /// <see cref="TicketPendingRecord"/> applies to a pending ticket.
    /// </summary>
    public void Cancel(DateTime cancelledAtUtc, string reason)
    {
        EnsureOpen();

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A reason is required — pending customer work is never cancelled without a recorded why.", nameof(reason));
        }

        ResolvedAtUtc = cancelledAtUtc;
        ResolutionNote = Truncate(reason, ResolutionNoteMaxLength);
        Status = AgentHandoffStatus.Cancelled;
    }

    private void EnsureOpen()
    {
        if (ResolvedAtUtc is { } resolvedAt)
        {
            throw new AgentHandoffAlreadyResolvedException(TicketAgentHandoffId, Status, resolvedAt);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

/// <summary>
/// The handoff is already Completed or Cancelled, so it accepts no further
/// assignment, start, completion or cancellation. Callers treat a redelivered
/// terminal event as "already processed" rather than as an error.
/// </summary>
public sealed class AgentHandoffAlreadyResolvedException(long ticketAgentHandoffId, AgentHandoffStatus status, DateTime resolvedAtUtc)
    : TicketException($"Agent handoff {ticketAgentHandoffId} was already {status} at {resolvedAtUtc:O} and accepts no further changes.")
{
    public long TicketAgentHandoffId { get; } = ticketAgentHandoffId;
    public AgentHandoffStatus Status { get; } = status;
    public DateTime ResolvedAtUtc { get; } = resolvedAtUtc;
}
