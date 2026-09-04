using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketApprovalRepository(TigerCsDbContext dbContext) : ITicketApprovalRepository
{
    public Task<TicketApproval?> GetByIdAsync(long ticketApprovalId, CancellationToken cancellationToken = default) =>
        dbContext.TicketApprovals.FirstOrDefaultAsync(a => a.TicketApprovalId == ticketApprovalId, cancellationToken);

    public Task<TicketApproval?> GetPendingAsync(
        long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        dbContext.TicketApprovals.FirstOrDefaultAsync(
            a => a.TicketId == ticketId && a.ApprovalType == approvalType && a.Status == ApprovalStatus.Pending,
            cancellationToken);

    public Task<TicketApproval?> GetCurrentAsync(
        long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        dbContext.TicketApprovals.FirstOrDefaultAsync(
            a => a.TicketId == ticketId && a.ApprovalType == approvalType && a.IsCurrent, cancellationToken);

    public async Task<IReadOnlyList<TicketApproval>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketApprovals
            .Where(a => a.TicketId == ticketId)
            .OrderBy(a => a.RequestedAtUtc)
            .ThenBy(a => a.TicketApprovalId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketApproval approval, CancellationToken cancellationToken = default) =>
        await dbContext.TicketApprovals.AddAsync(approval, cancellationToken);
}

public sealed class TicketWorkflowEventRepository(TigerCsDbContext dbContext) : ITicketWorkflowEventRepository
{
    public Task<TicketWorkflowEvent?> GetFirstAsync(
        long ticketId, WorkflowEventType eventType, CancellationToken cancellationToken = default) =>
        dbContext.TicketWorkflowEvents
            .Where(e => e.TicketId == ticketId && e.EventType == eventType)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.TicketWorkflowEventId)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<TicketWorkflowEvent?> GetLatestAsync(
        long ticketId, IReadOnlyCollection<WorkflowEventType> eventTypes, CancellationToken cancellationToken = default) =>
        dbContext.TicketWorkflowEvents
            .Where(e => e.TicketId == ticketId && eventTypes.Contains(e.EventType))
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.TicketWorkflowEventId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketWorkflowEvent>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketWorkflowEvents
            .Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.TicketWorkflowEventId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketWorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
        await dbContext.TicketWorkflowEvents.AddAsync(workflowEvent, cancellationToken);
}
