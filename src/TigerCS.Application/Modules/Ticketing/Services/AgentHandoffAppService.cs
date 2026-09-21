using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The agent-facing half of pending human work: seeing what is waiting, and
/// taking it, finishing it, or standing it down.
///
/// <para>
/// <b>Deliberately not a routing engine.</b> Nothing here decides who should
/// get the work, dials anyone, sends a WhatsApp message or resumes a chat.
/// Genesys owns channel delivery, queue routing, agent availability, live
/// transfer and outbound execution. This service owns the business-visible
/// state of the work: who is waiting, since when, on which channel, for which
/// ticket, and whether a human has picked it up.
/// </para>
///
/// <para>
/// <b>Nothing here touches the ticket.</b> Completing human work is not
/// resolving a case: the customer whose chat was answered may still have an
/// NOC workflow running for days. The ticket keeps following the existing
/// TigerCS lifecycle, and the two statuses are read side by side.
/// </para>
///
/// <para>
/// <b>Every mutation is idempotent.</b> Starting work already in progress,
/// or completing work already completed, is answered rather than duplicated —
/// a double-clicked button and a redelivered event behave the same way.
/// </para>
/// </summary>
public sealed class AgentHandoffAppService(
    ITicketAgentHandoffRepository handoffRepository,
    TicketQueryAppService ticketQueryAppService,
    ITicketRepository ticketRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The agent work list — every customer interaction waiting for a human,
    /// on every channel. Scoped by exactly the same visible-department rule
    /// as the ticket queue, so an agent never sees work they could not open.
    /// </summary>
    public async Task<AgentHandoffListResultDto> ListAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        AgentHandoffListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var result = await handoffRepository.SearchAsync(
            new AgentHandoffQuery(
                visibleDepartmentIds, request.DepartmentId, request.ChannelId, request.AssignedEmployeeId,
                request.UnassignedOnly, request.IncludeResolved, page, pageSize),
            cancellationToken);

        return new AgentHandoffListResultDto(
            result.Items.Select(ToDto).ToList(), result.TotalCount, page, pageSize);
    }

    /// <summary>A ticket's handoff history, for Ticket Details. Newest request first.</summary>
    public Task<IReadOnlyList<TicketAgentHandoff>> ListByTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
        handoffRepository.ListByTicketIdAsync(ticketId, cancellationToken);

    /// <summary>
    /// A human agent <b>accepts</b> pending work: claims it exclusively, takes
    /// ownership of the ticket behind it, and starts the work — all in one
    /// transaction, or none of it.
    ///
    /// <para>
    /// <b>Exclusive.</b> <see cref="TicketAgentHandoff.ClaimBy"/> refuses a
    /// second agent with the holder's identity, so the loser is told who has
    /// it instead of receiving a success carrying someone else's name. The
    /// same agent repeating the call is idempotent. The handoff's
    /// <c>RowVersion</c> closes the narrower window where two requests both
    /// read an unclaimed row and interleave their commits.
    /// </para>
    ///
    /// <para>
    /// <b>The ticket side is deliberately conditional</b>, because two existing
    /// rules constrain it and neither may be bypassed through this door:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Ownership moves only when the ticket is <i>unowned</i> and the
    ///     accepting agent is an active member of the ticket's current
    ///     department. MVP-API-Contracts.md §3.5 — enforced by
    ///     <c>TicketAssignmentAppService.AssignAsync</c> as
    ///     <c>EmployeeNotInDepartment</c> — requires an assignee to belong to
    ///     that department, and a CS-layer agent handling a conversation
    ///     cross-department does not always. Stealing a case from its current
    ///     owner is not accepting a conversation either, so an owned ticket
    ///     keeps its owner.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Open → InProgress</c> runs only when the ticket has an owner
    ///     afterwards, because <see cref="Ticket.ChangeStatus"/> throws
    ///     <c>TicketNotAssignedException</c> otherwise. That is the guard
    ///     behind confirmed decision 8 — "the ticket remains Open if it has no
    ///     owner" — and it is the domain's rule, not this method's.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>First Response is deliberately NOT recorded here</b> (confirmed
    /// decision 10). Accepting is not replying: the customer has heard nothing
    /// yet. <c>Ticket.FirstHumanResponseAtUtc</c> is satisfied by the first
    /// actual human message — see
    /// <c>GenesysConversationEndAppService</c>'s transcript path. Acceptance
    /// time is the end of the separate Human Wait operational metric
    /// (<c>RequestedAtUtc</c> → <c>AssignedAtUtc</c>), which is not an SLA.
    /// </para>
    /// </summary>
    public async Task<AgentHandoffResult> AcceptAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketAgentHandoffId,
        CancellationToken cancellationToken = default)
    {
        var handoff = await handoffRepository.GetByIdAsync(ticketAgentHandoffId, cancellationToken);
        if (handoff is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        // Same authority as every other action on this work list: ordinary
        // work in a department the caller can already see, carrying ADR-0024's
        // override through AuthorizationGate.
        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, handoff.DepartmentId, cancellationToken))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.Forbidden);
        }

        var ticket = await ticketRepository.GetByIdAsync(handoff.TicketId, cancellationToken);
        if (ticket is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var beforeStatus = handoff.Status;
        var beforeTicketStatus = ticket.TicketStatus;
        var beforeOwner = ticket.CurrentOwnerEmployeeId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        bool claimed;
        try
        {
            claimed = handoff.ClaimBy(callerEmployeeId, now);
        }
        catch (AgentHandoffAlreadyResolvedException)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.AlreadyResolved);
        }
        catch (AgentHandoffAlreadyClaimedException alreadyClaimed)
        {
            return AgentHandoffResult.AlreadyClaimed(alreadyClaimed.HolderEmployeeId, alreadyClaimed.ClaimedAtUtc);
        }

        // Closed-ticket immutability: the work item is still claimable (it may
        // have outlived the closure) but nothing on a Closed ticket moves.
        var ticketIsMutable = ticket.TicketStatus != TicketStatus.Closed;

        var assignedTicket = false;
        if (ticketIsMutable
            && ticket.CurrentOwnerEmployeeId is null
            && await userDepartmentAssignmentRepository.ExistsAsync(
                callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
            currentAssignment?.MarkSuperseded();

            ticket.AssignTo(callerEmployeeId);
            await ticketAssignmentRepository.AddAsync(
                new TicketAssignment(ticket.TicketId, callerEmployeeId, ticket.CurrentDepartmentId, now, callerEmployeeId),
                cancellationToken);
            assignedTicket = true;
        }

        var movedToInProgress = false;
        if (ticketIsMutable
            && ticket.TicketStatus == TicketStatus.Open
            && ticket.CurrentOwnerEmployeeId is not null)
        {
            // The domain's own transition table and its owner guard decide
            // this; a failure is reported, never worked around.
            try
            {
                ticket.ChangeStatus(TicketStatus.InProgress);
                movedToInProgress = true;
            }
            catch (TicketNotAssignedException)
            {
                movedToInProgress = false;
            }
            catch (InvalidTicketStatusTransitionException)
            {
                movedToInProgress = false;
            }

            if (movedToInProgress)
            {
                await statusHistoryRepository.AddAsync(
                    new TicketStatusHistory(
                        ticket.TicketId, TicketStatusDimension.TicketStatus,
                        (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                        callerEmployeeId, actorIsSystem: false,
                        note: "A human agent accepted the pending customer interaction.",
                        correlationId, now),
                    cancellationToken);
            }
        }

        // Ticket Activity: only when this call actually performed the claim,
        // so a repeated accept by the holder does not append a second line.
        if (claimed)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    handoff.TicketId, WorkflowEventType.HandoffStarted, now, callerEmployeeId,
                    ticketApprovalId: null,
                    note: FormatWaitNote(handoff),
                    correlationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, "AcceptAgentHandoff", nameof(TicketAgentHandoff),
            handoff.TicketAgentHandoffId.ToString(),
            beforeValue:
                $"Status={beforeStatus};TicketStatus={beforeTicketStatus};"
                + $"AssignedEmployeeId={beforeOwner?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"Status={handoff.Status};TicketStatus={ticket.TicketStatus};"
                + $"AssignedEmployeeId={ticket.CurrentOwnerEmployeeId?.ToString() ?? "DepartmentQueue"};"
                + $"ClaimPerformed={claimed};TicketAssigned={assignedTicket};MovedToInProgress={movedToInProgress};"
                + $"HumanWaitSeconds={(handoff.AssignedAtUtc is { } at ? (at - handoff.RequestedAtUtc).TotalSeconds : 0):F0}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            // Either the ticket's RowVersion or the handoff's moved between
            // this request's read and its commit. Nothing was written.
            return AgentHandoffResult.Failure(AgentHandoffOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return AgentHandoffResult.Success(ToDto(handoff));
    }

    /// <summary>An agent begins handling the work — which also takes it, if nobody had.</summary>
    public Task<AgentHandoffResult> StartAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketAgentHandoffId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            callerEmployeeId, callerRoles, ticketAgentHandoffId, "StartAgentHandoff",
            (handoff, now) => handoff.Start(callerEmployeeId, now),
            cancellationToken);

    /// <summary>
    /// The human work is done. <b>The ticket is untouched</b> — it stays open,
    /// in progress, or wherever the existing workflow has it.
    /// </summary>
    public Task<AgentHandoffResult> CompleteAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketAgentHandoffId,
        CompleteAgentHandoffRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MutateAsync(
            callerEmployeeId, callerRoles, ticketAgentHandoffId, "CompleteAgentHandoff",
            (handoff, now) => handoff.Complete(callerEmployeeId, now, request.ResolutionNote),
            cancellationToken);
    }

    /// <summary>The work is no longer needed. A reason is required — pending customer work is never dropped silently.</summary>
    public Task<AgentHandoffResult> CancelAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketAgentHandoffId,
        CancelAgentHandoffRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Task.FromResult(AgentHandoffResult.Failure(AgentHandoffOutcome.ReasonRequired));
        }

        return MutateAsync(
            callerEmployeeId, callerRoles, ticketAgentHandoffId, "CancelAgentHandoff",
            (handoff, now) => handoff.Cancel(now, request.Reason),
            cancellationToken);
    }

    /// <summary>How long the customer waited for a person — the Human Wait metric, in the Activity line that ends it.</summary>
    private static string FormatWaitNote(TicketAgentHandoff handoff)
    {
        var waitedFor = (handoff.AssignedAtUtc ?? handoff.RequestedAtUtc) - handoff.RequestedAtUtc;
        return $"Accepted after {FormatDuration(waitedFor)} waiting for a human agent.";
    }

    /// <summary>Human-readable duration for an Activity line — never a raw TimeSpan.</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalMinutes < 1
            ? $"{duration.TotalSeconds:F0}s"
            : duration.TotalHours < 1
                ? $"{duration.TotalMinutes:F0}m"
                : duration.TotalDays < 1
                    ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private async Task<AgentHandoffResult> MutateAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketAgentHandoffId,
        string auditAction,
        Action<TicketAgentHandoff, DateTime> mutate,
        CancellationToken cancellationToken)
    {
        var handoff = await handoffRepository.GetByIdAsync(ticketAgentHandoffId, cancellationToken);
        if (handoff is null)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.NotFound);
        }

        // Acting on pending work is ordinary work in a department you can
        // already see — the same authority the ticket queue grants, never a
        // new privilege tier.
        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, handoff.DepartmentId, cancellationToken))
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.Forbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var beforeStatus = handoff.Status;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            mutate(handoff, now);
        }
        catch (AgentHandoffAlreadyResolvedException)
        {
            return AgentHandoffResult.Failure(AgentHandoffOutcome.AlreadyResolved);
        }

        // Ticket Activity, for the transitions that have a typed event. Start
        // is deliberately absent: the accept path (AcceptAsync) is what a
        // human pressing the button goes through, and it emits HandoffStarted
        // itself — emitting here too would double-count.
        var activityEvent = handoff.Status switch
        {
            AgentHandoffStatus.Completed => WorkflowEventType.HandoffCompleted,
            AgentHandoffStatus.Cancelled => WorkflowEventType.HandoffCancelled,
            _ => (WorkflowEventType?)null
        };

        if (activityEvent is { } eventType && beforeStatus != handoff.Status)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    handoff.TicketId, eventType, now, callerEmployeeId,
                    ticketApprovalId: null, handoff.ResolutionNote, correlationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, auditAction, nameof(TicketAgentHandoff), handoff.TicketAgentHandoffId.ToString(),
            beforeValue: $"Status={beforeStatus}",
            afterValue: $"Status={handoff.Status};AssignedEmployeeId={handoff.AssignedEmployeeId?.ToString() ?? "(none)"}",
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return AgentHandoffResult.Success(ToDto(handoff));
    }

    /// <summary>The list projection, with the ticket/interaction context the row carries.</summary>
    internal static AgentHandoffDto ToDto(AgentHandoffListRow row) =>
        ToDto(row.Handoff, row.TicketNumber, row.RequestSummary, row.TicketStatus, row.TicketIsClassified,
            row.CustomerName, row.CustomerPhone, row.GenesysConversationId, row.InteractionEnded);

    /// <summary>The single-handoff projection. The ticket/interaction context is not re-read for a mutation response — the action changed the work item, not the ticket.</summary>
    internal static AgentHandoffDto ToDto(TicketAgentHandoff handoff) =>
        ToDto(handoff, ticketNumber: string.Empty, requestSummary: string.Empty, ticketStatus: string.Empty,
            ticketIsClassified: false, customerName: null, customerPhone: null,
            genesysConversationId: null, interactionEnded: false);

    private static AgentHandoffDto ToDto(
        TicketAgentHandoff handoff, string ticketNumber, string requestSummary, string ticketStatus,
        bool ticketIsClassified, string? customerName, string? customerPhone,
        string? genesysConversationId, bool interactionEnded) =>
        new(handoff.TicketAgentHandoffId,
            handoff.TicketId,
            ticketNumber,
            handoff.TicketInteractionId,
            genesysConversationId,
            handoff.DepartmentId,
            handoff.ChannelId,
            handoff.Status.ToString(),
            handoff.Mode?.ToString(),
            handoff.RequestReason,
            handoff.RequestedAtUtc,
            handoff.AssignedEmployeeId,
            handoff.GenesysAgentId,
            handoff.AssignedAtUtc,
            handoff.StartedAtUtc,
            handoff.CompletedAtUtc,
            handoff.ResolvedAtUtc,
            handoff.ResolutionNote,
            customerName,
            customerPhone,
            requestSummary,
            ticketStatus,
            ticketIsClassified,
            interactionEnded,
            handoff.ExternalWorkItemId);
}
