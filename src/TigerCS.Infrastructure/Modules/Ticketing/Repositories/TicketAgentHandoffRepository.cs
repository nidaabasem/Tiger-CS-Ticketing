using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketAgentHandoffRepository(TigerCsDbContext dbContext) : ITicketAgentHandoffRepository
{
    public Task<TicketAgentHandoff?> GetByIdAsync(long ticketAgentHandoffId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAgentHandoffs.FirstOrDefaultAsync(
            h => h.TicketAgentHandoffId == ticketAgentHandoffId, cancellationToken);

    public Task<TicketAgentHandoff?> GetOpenByInteractionIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAgentHandoffs.FirstOrDefaultAsync(
            h => h.TicketInteractionId == ticketInteractionId && h.ResolvedAtUtc == null, cancellationToken);

    public Task<TicketAgentHandoff?> GetByExternalWorkItemIdAsync(string externalWorkItemId, CancellationToken cancellationToken = default) =>
        dbContext.TicketAgentHandoffs.FirstOrDefaultAsync(
            h => h.ExternalWorkItemId == externalWorkItemId, cancellationToken);

    public async Task<IReadOnlyList<TicketAgentHandoff>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAgentHandoffs
            .Where(h => h.TicketId == ticketId)
            .OrderByDescending(h => h.RequestedAtUtc)
            .ThenByDescending(h => h.TicketAgentHandoffId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The agent work list, joined to its ticket and its interaction in ONE
    /// query — the context an agent needs to triage (who is waiting, on which
    /// channel, since when, and whether the live session already ended)
    /// without a round trip per row.
    /// </summary>
    /// <summary>
    /// One pass over the unresolved, waiting rows — served by
    /// <c>IX_TicketAgentHandoffs_OpenByDepartment</c>, whose filter
    /// (<c>ResolvedAtUtc IS NULL</c>) and leading columns
    /// (<c>DepartmentId, RequestedAtUtc</c>) are exactly this predicate and
    /// this ordering.
    /// </summary>
    public async Task<AwaitingHumanAgentSnapshot> GetAwaitingHumanAgentSnapshotAsync(
        IReadOnlyCollection<int>? visibleDepartmentIds, CancellationToken cancellationToken = default)
    {
        var waiting = dbContext.TicketAgentHandoffs
            .Where(h => h.ResolvedAtUtc == null && h.Status == AgentHandoffStatus.WaitingForAgent);

        if (visibleDepartmentIds is not null)
        {
            waiting = waiting.Where(h => visibleDepartmentIds.Contains(h.DepartmentId));
        }

        var count = await waiting.CountAsync(cancellationToken);
        var oldest = count == 0
            ? (DateTime?)null
            : await waiting.MinAsync(h => h.RequestedAtUtc, cancellationToken);

        return new AwaitingHumanAgentSnapshot(count, oldest);
    }

    public async Task<AgentHandoffQueryResult> SearchAsync(AgentHandoffQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = dbContext.TicketAgentHandoffs.AsQueryable();

        if (!query.IncludeResolved)
        {
            filtered = filtered.Where(h => h.ResolvedAtUtc == null);
        }

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(h => query.VisibleDepartmentIds.Contains(h.DepartmentId));
        }

        if (query.DepartmentId is { } departmentId)
        {
            filtered = filtered.Where(h => h.DepartmentId == departmentId);
        }

        if (query.ChannelId is { } channelId)
        {
            filtered = filtered.Where(h => h.ChannelId == channelId);
        }

        if (query.AssignedEmployeeId is { } assignedEmployeeId)
        {
            filtered = filtered.Where(h => h.AssignedEmployeeId == assignedEmployeeId);
        }

        if (query.UnassignedOnly)
        {
            filtered = filtered.Where(h => h.AssignedEmployeeId == null);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        var items = await filtered
            // Longest wait first: the customer who has been waiting for a
            // human the longest is the one the list must surface.
            .OrderBy(h => h.RequestedAtUtc)
            .ThenBy(h => h.TicketAgentHandoffId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Join(dbContext.Tickets, h => h.TicketId, t => t.TicketId, (h, t) => new { h, t })
            .Join(dbContext.TicketInteractions, x => x.h.TicketInteractionId, i => i.TicketInteractionId,
                (x, i) => new AgentHandoffListRow(
                    x.h,
                    x.t.TicketNumber,
                    x.t.RequestSummary,
                    x.t.TicketStatus.ToString(),
                    x.t.CategoryId != null,
                    i.CustomerName,
                    i.CustomerPhone,
                    i.GenesysConversationId,
                    i.EndedAtUtc != null))
            .ToListAsync(cancellationToken);

        return new AgentHandoffQueryResult(items, totalCount);
    }

    public async Task AddAsync(TicketAgentHandoff handoff, CancellationToken cancellationToken = default) =>
        await dbContext.TicketAgentHandoffs.AddAsync(handoff, cancellationToken);
}
