using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Ticket queue/list and detail (MVP-API-Contracts.md §3.2/§3.3, this
/// increment's item 1). Department visibility is resolved server-side from
/// the caller's roles/department membership — never from a client-supplied
/// department filter alone — per Security-Architecture.md §3 and
/// Solution-Analysis.md §4.1's View column.
/// </summary>
public sealed class TicketQueryAppService(
    ITicketRepository ticketRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository)
{
    public async Task<TicketListResultDto> GetQueueAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        TicketListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var query = new TicketQuery(
            visibleDepartmentIds,
            request.DepartmentId,
            request.CategoryId,
            request.PriorityId,
            ParseEnum<TicketStatus>(request.TicketStatus),
            ParseEnum<CrmVerificationStatus>(request.VerificationStatus),
            request.OwnerEmployeeId,
            request.Search,
            string.Equals(request.SortBy, "priority", StringComparison.OrdinalIgnoreCase) ? TicketSortBy.Priority : TicketSortBy.CreatedAtUtc,
            !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase),
            page,
            pageSize);

        var result = await ticketRepository.SearchAsync(query, cancellationToken);

        return new TicketListResultDto(result.Items.Select(ToSummaryDto).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<TicketQueryResultDto<TicketDetailDto>> GetDetailAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketDetailDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await CanViewDepartmentAsync(callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketQueryResultDto<TicketDetailDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        return TicketQueryResultDto<TicketDetailDto>.Success(ToDetailDto(ticket));
    }

    /// <summary>Null means "no restriction" (cross-department view role, or the ADR-0024 override) — never client-supplied, always resolved from the caller's own roles/department membership.</summary>
    internal async Task<IReadOnlyCollection<int>?> ResolveVisibleDepartmentIdsAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken)
    {
        if (AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains)))
        {
            return null;
        }

        var assignments = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
        return assignments.Select(a => a.DepartmentId).ToList();
    }

    internal Task<bool> CanViewDepartmentAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int departmentId, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains)
            || await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken));

    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : null;

    private static TicketSummaryDto ToSummaryDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.CurrentDepartmentId,
        ticket.CurrentOwnerEmployeeId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.RequestSummary,
        ticket.CreatedAtUtc);

    internal static TicketDetailDto ToDetailDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.OriginatingDepartmentId,
        ticket.CurrentDepartmentId,
        ticket.CurrentOwnerEmployeeId,
        ticket.UnitReferenceId,
        ticket.ContactReferenceId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.EscalationLevel.ToString(),
        ticket.SlaState.ToString(),
        ticket.ResolutionOutcome,
        ticket.DuplicateOfTicketId,
        ticket.RequestSummary,
        ticket.ReopenCount,
        ticket.CreatedAtUtc,
        Convert.ToBase64String(ticket.RowVersion),
        ticket.CrmBuyerCustomerId,
        ticket.CrmBuyerLeadId,
        ticket.CrmBuyerUnitId,
        ticket.CrmBuyerProjectId,
        ticket.CrmBuyerCustomerName,
        ticket.CrmBuyerProjectName,
        ticket.CrmBuyerUnitNumber,
        ticket.ManualProjectName,
        ticket.ManualUnitNumber,
        ticket.CustomerVerificationSource,
        ticket.ExternalCustomerId,
        ticket.ExternalUnitId);
}
