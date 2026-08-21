using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Core ticket lifecycle (this increment's item 4): status change (§3.7),
/// resolve (§3.9), and close (§3.10) — three deliberately distinct
/// operations, per ISSUE-022's approved Resolve/Department-Employee vs.
/// Close/CS-layer split. Reopen (§3.11) is not implemented — it is not
/// explicitly named in MVP-Implementation-Backlog.md S-16's acceptance
/// criteria/test requirements/Definition of Done (only Resolve/Close are),
/// so per this increment's own instruction ("do not implement Reopen unless
/// explicitly included in the approved MVP backlog"), it is left out and
/// flagged in the PR description rather than silently built or silently
/// assumed out.
/// </summary>
public sealed class TicketLifecycleAppService(
    ITicketRepository ticketRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
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
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.TicketStatus, (byte)oldStatus, (byte)newStatus,
                callerEmployeeId, actorIsSystem: false, note: null, correlationId, now),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "ChangeStatus", "Ticket", ticketId.ToString(),
            beforeValue: oldStatus.ToString(), afterValue: newStatus.ToString(), correlationId, cancellationToken);

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

        if (!callerRoles.Any(TicketRoleSets.Close.Contains))
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

    private async Task<bool> IsCurrentOwnerOrDepartmentAuthorityAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken)
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
    }

    /// <summary>ISSUE-022: Resolve is Department Employee/Head only. A Department Employee must be the ticket's current owner (the one who actually worked it); a Department Head may resolve any ticket in a department they belong to.</summary>
    private async Task<bool> IsResolveAuthorizedAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken)
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
    }
}
