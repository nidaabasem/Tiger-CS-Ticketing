using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Core ticket lifecycle: status change (§3.7), resolve (§3.9), close
/// (§3.10) — three deliberately distinct operations, per ISSUE-022's
/// approved Resolve/Department-Employee vs. Close/CS-layer split — and, as
/// of the Customer Workspace phase, reopen (§3.11, FR-RES-04): the Agent's
/// exit from Closed back to InProgress, within ISSUE-011's configurable
/// window (<see cref="ReopenPolicy"/>), re-routing the ticket to a chosen
/// department and opening a new Resolution SLA cycle, while archiving — never
/// deleting — the prior resolution.
/// </summary>
public sealed class TicketLifecycleAppService(
    ITicketRepository ticketRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    SlaBreachProcessor breachProcessor,
    TimeProvider timeProvider,
    ReopenPolicy reopenPolicy,
    ITicketPendingRecordRepository pendingRecordRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IOutboxWriter outboxWriter,
    IDepartmentRepository departmentRepository,
    IDepartmentWorkflowSettingsRepository departmentWorkflowSettingsRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    TicketAutoAssignmentService autoAssignmentService,
    SlaDueDateService slaDueDateService)
{
    /// <summary>Matches the <c>TicketStatusHistory.Note</c> column, so a long reason is never lost to a database truncation error mid-transaction.</summary>
    public const int ReopenReasonMaxLength = 1000;

    public async Task<TicketMutationResult> ChangeStatusAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ChangeStatusRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (!Enum.TryParse<TicketStatus>(request.NewStatus, ignoreCase: true, out var newStatus))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }

        if (!await IsCurrentOwnerOrDepartmentAuthorityAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): rejected before any
        // transaction/write — see TicketAssignmentAppService.AssignAsync's
        // identical remark.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        // Workflow/Automation phase 2 — structured pending. Entering a
        // Pending status always requires a reason (a ticket is never pending
        // without a recorded why), and, where the ticket carries a request
        // type, the target Pending kind must be allowed by its workflow
        // configuration. The configuration can only narrow the existing
        // status machine — a ticket with no request type keeps the exact
        // pre-phase-2 behavior.
        var targetPendingKind = newStatus switch
        {
            TicketStatus.PendingCustomer => PendingKind.Customer,
            TicketStatus.PendingThirdParty => PendingKind.InternalOrThirdParty,
            _ => (PendingKind?)null
        };

        if (targetPendingKind is not null)
        {
            if (string.IsNullOrWhiteSpace(request.PendingReason))
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.PendingReasonRequired);
            }

            var capabilities = await ResolveCapabilitiesAsync(ticket, cancellationToken);
            var pendingAllowed = targetPendingKind == PendingKind.Customer
                ? capabilities?.CanGoPendingCustomer
                : capabilities?.CanGoPendingInternal;
            if (pendingAllowed is false)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.ChangeStatus(newStatus);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (InvalidTicketStatusTransitionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }
        catch (TicketNotAssignedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketNotAssigned);
        }

        var correlationId = Guid.NewGuid();

        // The pending record and the resume are written in the same
        // transaction as the status change itself, under the same
        // correlation id, so "went pending"/"resumed" is one auditable event
        // with its structured reason — not a status flip plus a detached
        // note.
        if (targetPendingKind is { } enteringKind)
        {
            await pendingRecordRepository.AddAsync(
                new TicketPendingRecord(
                    ticketId, enteringKind, request.PendingReason!, oldStatus, callerEmployeeId, now, correlationId),
                cancellationToken);
        }
        else if (oldStatus is TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty)
        {
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(callerEmployeeId, now);
        }

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)newStatus,
                callerEmployeeId, actorIsSystem: false, note: request.PendingReason, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "ChangeStatus", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(),
            afterValue: targetPendingKind is not null ? $"{newStatus};PendingReason={request.PendingReason}" : newStatus.ToString(),
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    public async Task<TicketMutationResult> ResolveAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ResolveTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (!Enum.TryParse<ResolutionOutcome>(request.ResolutionOutcome, ignoreCase: true, out var outcome))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.InvalidStatusTransition);
        }

        if (!await IsResolveAuthorizedAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): rejected before any
        // transaction/write.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        if (outcome == ResolutionOutcome.Duplicate)
        {
            var duplicateTarget = request.DuplicateOfTicketId is { } targetId
                ? await ticketRepository.GetByIdAsync(targetId, cancellationToken)
                : null;

            if (duplicateTarget is null || duplicateTarget.ResolutionOutcome == (byte)ResolutionOutcome.Duplicate)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.DuplicateChainNotAllowed);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Resolve(outcome, request.DuplicateOfTicketId);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotEligibleForResolutionException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForResolution);
        }

        // Resolving directly out of a Pending status ends that pending
        // period — the pause window must close so the record never dangles
        // open on a Resolved ticket.
        if (oldStatus is TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty)
        {
            var openPending = await pendingRecordRepository.GetOpenAsync(ticketId, cancellationToken);
            openPending?.Resume(callerEmployeeId, now);
        }

        await ticketResolutionRepository.AddAsync(
            new TicketResolution(
                ticketId, outcome, request.ResolutionNote, request.ReasonCode, request.DuplicateOfTicketId,
                callerEmployeeId, now),
            cancellationToken);

        var correlationId = Guid.NewGuid();
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Resolved,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.ResolutionOutcome, oldValue: null, (byte)outcome,
                callerEmployeeId, actorIsSystem: false, request.ResolutionNote, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Resolve", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(), afterValue: $"ResolutionOutcome={outcome}", correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketResolved, OutboxEventTypes.TicketResolvedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        // Resolution is the Resolution SLA's achievement event
        // (SLA-Architecture.md §2 — closure deliberately is not), so this is
        // where a late resolution is finalized as a breach. Both clocks are
        // evaluated: a ticket resolved without a First Human Response ever
        // being recorded has missed that target too, and once the ticket
        // reaches Closed nothing may touch its SLA state again, so this is
        // the last honest moment to record it.
        //
        // Runs through the same processor and the same idempotency key as
        // the scheduled job and the sweep, so a deadline a job already
        // flagged is not re-recorded here.
        foreach (var deadlineType in new[] { SlaDeadlineType.FirstResponse, SlaDeadlineType.Resolution })
        {
            await breachProcessor.ProcessDeadlineAsync(ticket, deadlineType, now, correlationId, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // Reachable only since this method began finalizing breach flags:
            // a scheduled deadline job can claim the same breach idempotency
            // key (or the one-auto-escalation-per-ticket index) between this
            // request's read and its commit. The whole transaction rolls
            // back, so the resolution is not half-applied — the caller
            // re-reads and retries, exactly as for a lost RowVersion race.
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    public async Task<TicketMutationResult> CloseAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CloseTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        if (!AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.Close.Contains)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): closing an
        // already-Closed ticket is this condition, not NotYetResolved
        // (checked next) — a Closed ticket always has a current resolution,
        // so without this explicit check first it would otherwise fall
        // through to the resolution lookup below. Rejected before any
        // transaction/write.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        var currentResolution = await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken);
        if (currentResolution is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotYetResolved);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Close();
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketNotYetResolvedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotYetResolved);
        }

        var correlationId = Guid.NewGuid();
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.Closed,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Close", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(), afterValue: TicketStatus.Closed.ToString(), correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketClosed, OutboxEventTypes.TicketClosedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Reopen (MVP-API-Contracts.md §3.11, FR-RES-04), implementing the
    /// approved business rule. A <b>Closed</b> ticket — and only a Closed one,
    /// closed as Resolved — returns to InProgress in a department the
    /// reopening agent names, unowned, with a fresh Resolution SLA cycle.
    ///
    /// <para>
    /// <b>Authorization is two-part.</b> The role gate is CS Agent
    /// (<see cref="TicketRoleSets.Reopen"/>) — Supervisor and CS Manager no
    /// longer qualify merely by holding Close — and it is followed by a
    /// resource-level check that the caller could see this ticket at all
    /// (<see cref="TicketVisibilityRule"/>), so enumerating ticket ids reaches
    /// nothing the agent was not already entitled to. Both run through
    /// <see cref="AuthorizationGate"/>, so ADR-0024's System Administrator
    /// override applies as it does to every other operation.
    /// </para>
    ///
    /// <para>
    /// <b>Concurrency is checked before eligibility</b>, deliberately
    /// inverting the order this method used to run in. Two agents reopening at
    /// once both hold the pre-reopen RowVersion; the loser used to find the
    /// ticket already InProgress and be told "not eligible for reopen", which
    /// is true but misleading — nothing is wrong with the ticket, their copy
    /// is stale. Comparing the token first answers that race with the
    /// concurrency conflict it actually is, and the optimistic check at
    /// SaveChanges still covers the narrower window between this read and the
    /// commit. Either way the second request writes nothing: no second cycle,
    /// no second event, no second email.
    /// </para>
    ///
    /// <para>
    /// <b>Everything commits together</b> — the domain transition, the
    /// archived resolution, the new SLA cycle, the re-run of the existing
    /// assignment automation, the lifecycle history, the typed Reopened event
    /// Ticket Details renders, the audit entries and the customer-email Outbox
    /// row — under one correlation id, or nothing does.
    /// </para>
    /// </summary>
    public async Task<TicketMutationResult> ReopenAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ReopenTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Approved rule: the Agent reopens. The role set is consulted through
        // the gate, never inline, so the ADR-0024 override stays in one place.
        if (!AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.Reopen.Contains)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // ...and the resource-level half: an agent may only reopen a ticket
        // they have access to under the existing visibility rules.
        if (!await TicketVisibilityRule.CanViewDepartmentAsync(
                userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Required input, enforced HERE rather than only in model validation:
        // the controller is not the only possible caller, and a reopen with no
        // recorded why is exactly what the approved rule set out to prevent.
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ReopenReasonRequired);
        }

        if (request.TargetDepartmentId <= 0)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentRequired);
        }

        // See this method's remarks: a stale token is a concurrency conflict,
        // and answering it as one has to happen before the state-based checks
        // that a winning concurrent reopen would otherwise trip.
        if (!ticket.RowVersion.SequenceEqual(request.RowVersion))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        if (ticket.TicketStatus is not TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        if (ticket.ResolutionOutcome != (byte)ResolutionOutcome.Resolved)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ResolutionOutcomeNotReopenable);
        }

        // Workflow/Automation phase 2 — a request type may switch Reopen off
        // entirely. This gate only ever narrows: where reopen stays allowed
        // (or the ticket has no request type), the rules below remain the
        // final enforcement point, exactly as before.
        var capabilities = await ResolveCapabilitiesAsync(ticket, cancellationToken);
        if (capabilities is { CanReopen: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
        }

        // ISSUE-011's window, measured from the moment the ticket was CLOSED —
        // read from the lifecycle history Close itself wrote, because a Closed
        // ticket carries no closure timestamp column and the resolution
        // timestamp is a different (earlier) moment. A Closed ticket with no
        // such row is data damage: treated as not eligible rather than
        // silently substituting some other timestamp.
        var closedAt = await statusHistoryRepository.GetLatestTransitionIntoAsync(
            ticketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, cancellationToken);
        if (closedAt is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!reopenPolicy.IsWithinWindow(closedAt.OccurredAtUtc, now))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ReopenWindowExpired);
        }

        // A Closed ticket always has a current resolution; a missing one would
        // be data damage — treated as not eligible rather than crashing, since
        // there is no outcome to archive.
        var currentResolution = await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken);
        if (currentResolution is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        // Routing validation, reusing Transfer's existing department rules
        // rather than inventing a second set (TicketAssignmentAppService.TransferAsync).
        var targetDepartment = await departmentRepository.GetByIdAsync(request.TargetDepartmentId, cancellationToken);
        if (targetDepartment is null || !targetDepartment.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentInactive);
        }

        // ...including the source department's own narrowing setting, but only
        // where the reopen actually moves the ticket out of it. Reopening into
        // the same department is not a transfer, so it is neither blocked by
        // that setting nor rejected as AlreadyInTargetDepartment: naming the
        // closing department is a legitimate reopen, and the owner is cleared
        // and the automation re-run either way.
        if (request.TargetDepartmentId != ticket.CurrentDepartmentId)
        {
            var sourceSettings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
                ticket.CurrentDepartmentId, cancellationToken);
            if (sourceSettings is { AllowTransferToOtherDepartments: false })
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
            }
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var previousStatus = ticket.TicketStatus;
        var previousDepartmentId = ticket.CurrentDepartmentId;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        var previousSlaState = ticket.SlaState;
        var reason = Truncate(request.Reason.Trim(), ReopenReasonMaxLength);

        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Reopen(request.TargetDepartmentId);
        }
        catch (TicketNotEligibleForReopenException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }
        catch (TicketResolutionOutcomeNotReopenableException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ResolutionOutcomeNotReopenable);
        }

        ticket.RestartSlaClockForReopen();
        currentResolution.Archive();

        // The new Resolution cycle, before the assignment automation so the
        // deadline is available for the activity record below. First Response
        // is carried across untouched — see SlaDueDateService.
        var reopenCycle = await slaDueDateService.StartReopenResolutionCycleAsync(
            ticket, now, callerEmployeeId, correlationId, cancellationToken);

        // The ONE assignment engine, re-evaluated against the department that
        // now owns the work — the same call Transfer makes, for the same
        // reason. Every non-assignable case leaves the ticket in that
        // department's queue, audited.
        var assignment = await autoAssignmentService.ApplyAsync(
            ticket, now, correlationId, AutoAssignmentTrigger.DepartmentTransfer, cancellationToken);

        // Lifecycle history: the status dimension, carrying the agent's reason
        // as its note — the row Ticket Details now reads back through
        // GET /api/tickets/{ticketId}/history.
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)previousStatus, (byte)TicketStatus.InProgress,
                callerEmployeeId, actorIsSystem: false, note: reason, correlationId, now),
            cancellationToken);

        // ...and the SLA dimension, but only when the new cycle actually moved
        // it (Met → Running). A breached clock stays breached, so no row.
        if (ticket.SlaState != previousSlaState)
        {
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticketId, TicketStatusDimension.SlaState, (byte)previousSlaState, (byte)ticket.SlaState,
                    callerEmployeeId, actorIsSystem: false,
                    note: "New Resolution SLA cycle opened on reopen.", correlationId, now),
                cancellationToken);
        }

        // The typed event, carrying the facts the reason alone cannot express —
        // which department it moved between, where ownership landed, and the
        // new deadline — so the Activity line reads in full without a new
        // activity store. Formatting lives in one place (ReopenActivityFacts).
        var facts = new ReopenActivityFacts(
            previousDepartmentId,
            ticket.CurrentDepartmentId,
            previousOwnerEmployeeId,
            ticket.CurrentOwnerEmployeeId,
            reopenCycle?.ResolutionDueAtUtc,
            ticket.ReopenCount);

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticketId, WorkflowEventType.Reopened, now, callerEmployeeId,
                ticketApprovalId: null, facts.Format(), correlationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Reopen", "Ticket", ticketId.ToString(),
            beforeValue:
                $"{previousStatus};ResolutionOutcome={currentResolution.ResolutionOutcome}"
                + $";DepartmentId={previousDepartmentId}"
                + $";AssignedEmployeeId={previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"{TicketStatus.InProgress};ReopenCount={ticket.ReopenCount}"
                + $";DepartmentId={ticket.CurrentDepartmentId}"
                + $";AssignedEmployeeId={ticket.CurrentOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}"
                + $";AutoAssignment={assignment.Outcome}"
                + $";ResolutionSlaDueAtUtc={(reopenCycle is { } cycle ? cycle.ResolutionDueAtUtc.ToString("O") : "None")}"
                + $";Reason={reason}",
            correlationId, cancellationToken);

        await EnqueueLifecycleEventAsync(
            OutboxEventTypes.TicketReopened, OutboxEventTypes.TicketReopenedVersion,
            ticket, callerEmployeeId, correlationId, now, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// The ticket's effective workflow capabilities, or null when the ticket
    /// carries no request type — null means "no workflow configuration
    /// applies", never "everything forbidden": enforcement in this service
    /// only narrows the existing status machine where configuration exists.
    /// </summary>
    /// <summary>
    /// Records a customer-facing lifecycle event in the transactional Outbox
    /// (ADR-0013) — the same mechanism <c>TicketCreationAppService</c> uses
    /// for <c>TicketCreated</c>. Added to the caller's unit of work, so the
    /// event commits with the state change or not at all: a rolled-back
    /// resolve/close/reopen leaves no event behind, and nothing is sent
    /// from inside the request (NFR-REL-01). The customer email itself is
    /// rendered and delivered later by the Outbox dispatcher, so an SMTP
    /// failure can never fail this operation.
    /// </summary>
    private async Task EnqueueLifecycleEventAsync(
        string eventType,
        int eventVersion,
        Ticket ticket,
        Guid callerEmployeeId,
        Guid correlationId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var payload = new TicketLifecycleEventPayload(ticket.TicketId, eventVersion, ticket.ReopenCount);

        var message = await outboxWriter.WriteAsync(
            eventType,
            payload.ToJson(),
            correlationId,
            OutboxEventTypes.LifecycleIdempotencyKeyFor(eventType, ticket.TicketId, eventVersion, ticket.ReopenCount),
            nowUtc,
            cancellationToken);

        if (message is null)
        {
            // Already enqueued for this occurrence (a retried request); the
            // first row stands.
            return;
        }

        await auditWriter.WriteAsync(
            callerEmployeeId,
            NotificationAuditActions.NotificationQueued,
            NotificationAuditActions.OutboxMessageEntityType,
            message.OutboxMessageId.ToString(),
            beforeValue: null,
            afterValue: $"EventType={eventType};TicketId={ticket.TicketId};Status=Pending",
            correlationId,
            cancellationToken);
    }

    private async Task<WorkflowCapabilities?> ResolveCapabilitiesAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.RequestTypeId is not { } requestTypeId)
        {
            return null;
        }

        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return null;
        }

        // The ticket's PINNED version is authoritative (Administration /
        // Workflow Designer phase): publishing a newer version never changes
        // what an existing ticket may do. A legacy ticket that carries a
        // request type but no pinned version (classified before versioning,
        // and not backfilled) falls back to the workflow's currently
        // Published version — the same resolution it always had.
        var template = ticket.WorkflowTemplateId is { } pinnedVersionId
            ? await workflowTemplateRepository.GetByIdAsync(pinnedVersionId, cancellationToken)
            : await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
        return template is null ? null : WorkflowCapabilities.Resolve(template, requestType);
    }

    private Task<bool> IsCurrentOwnerOrDepartmentAuthorityAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            if (callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains))
            {
                return true;
            }

            return callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
        });

    /// <summary>ISSUE-022: Resolve is Department Employee/Head only. A Department Employee must be the ticket's current owner (the one who actually worked it); a Department Head may resolve any ticket in a department they belong to. Both are permission rules, so both run under the ADR-0024 override — the ticket's own eligibility for resolution (<see cref="Ticket.Resolve"/>) is separate, and is not.</summary>
    private Task<bool> IsResolveAuthorizedAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (!callerRoles.Any(TicketRoleSets.Resolve.Contains))
            {
                return false;
            }

            if (callerRoles.Contains(Roles.DepartmentHead))
            {
                return await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
            }

            return ticket.CurrentOwnerEmployeeId == callerEmployeeId;
        });
}
