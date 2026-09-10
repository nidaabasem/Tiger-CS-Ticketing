using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>
/// In-memory pending human work.
///
/// <para>
/// <b>Mirrors the database's two filtered unique indexes.</b>
/// <c>UX_TicketAgentHandoffs_OpenPerInteraction</c> (at most one unresolved
/// handoff per interaction) and
/// <c>UX_TicketAgentHandoffs_ExternalWorkItemId</c> are both enforced here, as
/// a <see cref="DuplicateWriteException"/> — the same signal
/// <c>TicketingUnitOfWork</c> translates a real SQL 2601/2627 into. Without
/// that, an idempotency test could pass against the fake while the real
/// database rejected the same write.
/// </para>
/// </summary>
public sealed class FakeTicketAgentHandoffRepository : ITicketAgentHandoffRepository
{
    private readonly List<TicketAgentHandoff> _handoffs = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every work item, in insertion order.</summary>
    public IReadOnlyList<TicketAgentHandoff> All => _handoffs;

    /// <summary>Supplies the ticket/interaction context the real repository joins to. Optional: a test that only asserts on the work item need not set them.</summary>
    public FakeTicketRepository? Tickets { get; set; }
    public FakeTicketInteractionRepository? Interactions { get; set; }

    public Task<TicketAgentHandoff?> GetByIdAsync(long ticketAgentHandoffId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_handoffs.FirstOrDefault(h => h.TicketAgentHandoffId == ticketAgentHandoffId));

    public Task<TicketAgentHandoff?> GetOpenByInteractionIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_handoffs.FirstOrDefault(h => h.TicketInteractionId == ticketInteractionId && h.IsOpen));

    public Task<TicketAgentHandoff?> GetByExternalWorkItemIdAsync(string externalWorkItemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_handoffs.FirstOrDefault(h =>
            string.Equals(h.ExternalWorkItemId, externalWorkItemId, StringComparison.Ordinal)));

    public Task<IReadOnlyList<TicketAgentHandoff>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketAgentHandoff>>(
            _handoffs.Where(h => h.TicketId == ticketId)
                .OrderByDescending(h => h.RequestedAtUtc)
                .ThenByDescending(h => h.TicketAgentHandoffId)
                .ToList());

    public Task<AgentHandoffQueryResult> SearchAsync(AgentHandoffQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = _handoffs.AsEnumerable();

        if (!query.IncludeResolved)
        {
            filtered = filtered.Where(h => h.IsOpen);
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
            filtered = filtered.Where(h => h.AssignedEmployeeId is null);
        }

        var all = filtered.ToList();

        // Longest wait first — the same order the real repository uses.
        var page = all
            .OrderBy(h => h.RequestedAtUtc)
            .ThenBy(h => h.TicketAgentHandoffId)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(h =>
            {
                var ticket = Tickets?.All.FirstOrDefault(t => t.TicketId == h.TicketId);
                var interaction = Interactions?.All.FirstOrDefault(i => i.TicketInteractionId == h.TicketInteractionId);
                return new AgentHandoffListRow(
                    h,
                    ticket?.TicketNumber ?? string.Empty,
                    ticket?.RequestSummary ?? string.Empty,
                    ticket?.TicketStatus.ToString() ?? string.Empty,
                    ticket?.IsClassified ?? false,
                    interaction?.CustomerName,
                    interaction?.CustomerPhone,
                    interaction?.GenesysConversationId,
                    interaction?.IsEnded ?? false);
            })
            .ToList();

        return Task.FromResult(new AgentHandoffQueryResult(page, all.Count));
    }

    public Task AddAsync(TicketAgentHandoff handoff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        // UX_TicketAgentHandoffs_OpenPerInteraction.
        if (handoff.IsOpen && _handoffs.Any(h => h.TicketInteractionId == handoff.TicketInteractionId && h.IsOpen))
        {
            throw new DuplicateWriteException(
                new InvalidOperationException(
                    $"UX_TicketAgentHandoffs_OpenPerInteraction: interaction {handoff.TicketInteractionId} already has outstanding human work."));
        }

        // UX_TicketAgentHandoffs_ExternalWorkItemId.
        if (handoff.ExternalWorkItemId is { } workItemId
            && _handoffs.Any(h => string.Equals(h.ExternalWorkItemId, workItemId, StringComparison.Ordinal)))
        {
            throw new DuplicateWriteException(
                new InvalidOperationException(
                    $"UX_TicketAgentHandoffs_ExternalWorkItemId: '{workItemId}' is already recorded."));
        }

        typeof(TicketAgentHandoff).GetProperty(nameof(TicketAgentHandoff.TicketAgentHandoffId))!
            .SetValue(handoff, _nextId++);
        _handoffs.Add(handoff);
        return Task.CompletedTask;
    }
}
