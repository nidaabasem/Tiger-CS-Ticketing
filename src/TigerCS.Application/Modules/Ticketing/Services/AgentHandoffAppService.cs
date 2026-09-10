using TigerCS.Application.Abstractions;
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

        await auditWriter.WriteAsync(
            callerEmployeeId, auditAction, nameof(TicketAgentHandoff), handoff.TicketAgentHandoffId.ToString(),
            beforeValue: $"Status={beforeStatus}",
            afterValue: $"Status={handoff.Status};AssignedEmployeeId={handoff.AssignedEmployeeId?.ToString() ?? "(none)"}",
            Guid.NewGuid(), cancellationToken);

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
