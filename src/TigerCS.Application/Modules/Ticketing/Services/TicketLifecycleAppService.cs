using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Core ticket lifecycle: status change (§3.7), resolve (§3.9), close
/// (§3.10) — three deliberately distinct operations, per ISSUE-022's
/// approved Resolve/Department-Employee vs. Close/CS-layer split — and, as
/// of the Customer Workspace phase, reopen (§3.11, FR-RES-04): the CS-layer
/// exit from Resolved/Closed back to InProgress, within ISSUE-011's
/// configurable window (<see cref="ReopenPolicy"/>), archiving — never
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
    IWorkflowTemplateRepository workflowTemplateRepository)
{
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
    /// Reopen (MVP-API-Contracts.md §3.11, FR-RES-04). Follows
    /// <see cref="CloseAsync"/>'s shape exactly: role gate → pre-transaction
    /// eligibility guards → RowVersion → one transaction carrying the domain
    /// transition, the archived resolution, the status-history row (with the
    /// caller's reason as its note), and the audit entry, all under one
    /// correlation id. The window check (ISSUE-011 — <see cref="ReopenPolicy"/>,
    /// 7 days configurable, measured from the current resolution's
    /// ResolvedAtUtc) runs here, not in the domain: the domain owns the
    /// status rule, the service owns the clock/config-dependent business
    /// rule, same division as every other lifecycle guard above.
    ///
    /// <para>
    /// No new SLA period is opened on reopen — MVP-API-Contracts.md §3.11
    /// flags "reopen restarts the resolution SLA clock" as an explicit
    /// business-rule <c>[ASSUMPTION]</c>, not a requirement, so the sticky
    /// SlaState and the closed SLA instance are left untouched until that
    /// rule is actually decided.
    /// </para>
    /// </summary>
    public async Task<TicketMutationResult> ReopenAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ReopenTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // ISSUE-022: Reopen is CS-layer, cross-department — same authority
        // shape as Close (TicketRoleSets.Reopen), with the ADR-0024 System
        // Administrator override applied by the gate, never inline.
        if (!AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.Reopen.Contains)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        if (ticket.TicketStatus is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        // Workflow/Automation phase 2 — a request type may switch Reopen off
        // entirely. This gate only ever narrows: where reopen stays allowed
        // (or the ticket has no request type), the existing ReopenPolicy
        // below remains the final enforcement point, exactly as before.
        var capabilities = await ResolveCapabilitiesAsync(ticket, cancellationToken);
        if (capabilities is { CanReopen: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
        }

        // A Resolved/Closed ticket always has a current resolution; a
        // missing one would be data damage — treated as not eligible rather
        // than crashing, since there is no outcome to archive.
        var currentResolution = await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken);
        if (currentResolution is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!reopenPolicy.IsWithinWindow(currentResolution.ResolvedAtUtc, now))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ReopenWindowExpired);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var oldStatus = ticket.TicketStatus;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.Reopen();
        }
        catch (TicketNotEligibleForReopenException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotEligibleForReopen);
        }

        currentResolution.Archive();

        var correlationId = Guid.NewGuid();
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)TicketStatus.InProgress,
                callerEmployeeId, actorIsSystem: false, note: request.Reason, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Reopen", "Ticket", ticketId.ToString(),
            beforeValue: $"{oldStatus};ResolutionOutcome={currentResolution.ResolutionOutcome}",
            afterValue: $"{TicketStatus.InProgress};ReopenCount={ticket.ReopenCount}",
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

    /// <summary>
    /// The ticket's effective workflow capabilities, or null when the ticket
    /// carries no request type — null means "no workflow configuration
    /// applies", never "everything forbidden": enforcement in this service
    /// only narrows the existing status machine where configuration exists.
    /// </summary>
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

        var template = await workflowTemplateRepository.GetByIdAsync(requestType.WorkflowTemplateId, cancellationToken);
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
