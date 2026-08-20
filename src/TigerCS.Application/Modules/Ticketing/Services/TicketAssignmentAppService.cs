using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Assignment (MVP-API-Contracts.md §3.5) and department transfer (§3.6) —
/// this increment's items 2 and 3. Both write their history/audit and the
/// domain state change in one real transaction (item 2/3's "atomically").
/// </summary>
public sealed class TicketAssignmentAppService(
    ITicketRepository ticketRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    IDepartmentRepository departmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<TicketMutationResult> AssignAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        AssignTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        var isSelfClaim = ticket.CurrentOwnerEmployeeId is null && request.AssignedEmployeeId == callerEmployeeId;

        var authorized = isSelfClaim
            ? await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)
                || callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains)
            : callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains)
                || (callerRoles.Contains(Roles.DepartmentHead)
                    && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken));

        if (!authorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // MVP-API-Contracts.md §3.5's own validation: AssignedEmployeeId
        // must be an active member of the ticket's CurrentDepartmentId —
        // checked against the department the assignment target actually
        // needs to belong to, never trusted from the caller's own claims.
        if (!await userDepartmentAssignmentRepository.ExistsAsync(request.AssignedEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.EmployeeNotInDepartment);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticketId, cancellationToken);
        currentAssignment?.MarkSuperseded();

        ticket.AssignTo(request.AssignedEmployeeId);

        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticketId, request.AssignedEmployeeId, ticket.CurrentDepartmentId, now, callerEmployeeId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Assign", "Ticket", ticketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(), afterValue: request.AssignedEmployeeId.ToString(),
            Guid.NewGuid(), cancellationToken);

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

    public async Task<TicketMutationResult> TransferAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        TransferTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // MVP-API-Contracts.md §3.6: "Supervisor+ in the current department"
        // — Department Head is scoped to their own department; Supervisor/
        // CS Manager/GM/Chairman/SysAdmin act cross-department.
        var authorized = callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains)
            || (callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken));

        if (!authorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        if (request.TargetDepartmentId == ticket.CurrentDepartmentId)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyInTargetDepartment);
        }

        var targetDepartment = await departmentRepository.GetByIdAsync(request.TargetDepartmentId, cancellationToken);
        if (targetDepartment is null || !targetDepartment.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentInactive);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var previousDepartmentId = ticket.CurrentDepartmentId;
        ticket.TransferToDepartment(request.TargetDepartmentId);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Transfer", "Ticket", ticketId.ToString(),
            beforeValue: $"DepartmentId={previousDepartmentId}",
            afterValue: $"DepartmentId={request.TargetDepartmentId};Reason={request.Reason}",
            Guid.NewGuid(), cancellationToken);

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
}
