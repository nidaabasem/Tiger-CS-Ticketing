using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
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
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ReopenPolicy reopenPolicy,
    TimeProvider timeProvider,
    IRequestTypeRepository? requestTypeRepository = null,
    IWorkflowTemplateRepository? workflowTemplateRepository = null,
    IWorkflowRepository? workflowRepository = null,
    ITicketInteractionRepository? interactionRepository = null,
    IChannelRepository? channelRepository = null)
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

        // Dashboard drill-down (Dashboard Phase 1): the Pending Approval
        // filter needs the caller's approver scope — resolved here from
        // their own roles and memberships, never from the request.
        ApprovalApproverScope? approverScope = null;
        if (request.PendingApproval == true)
        {
            var memberships = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
            approverScope = ApprovalApproverScope.Resolve(
                callerEmployeeId, callerRoles, memberships.Select(m => m.DepartmentId),
                TicketApprovalAppService.DepartmentTargetDefaultApproverRoles);
        }

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
            pageSize,
            ChannelId: request.ChannelId,
            RequestTypeId: request.RequestTypeId,
            ActiveOnly: request.ActiveOnly == true,
            InDepartmentQueue: request.InDepartmentQueue == true,
            SlaBreached: request.SlaBreached == true,
            DueToday: request.DueToday == true,
            BacklogAge: ParseEnum<BacklogAgeBucket>(request.BacklogAge),
            PendingApprovalFor: approverScope,
            // Calendar days are UTC days — the dashboard's existing "today" convention.
            CreatedFromUtc: request.CreatedFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CreatedToUtc: request.CreatedTo?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NowUtc: timeProvider.GetUtcNow().UtcDateTime);

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

        // Reopen display eligibility (FR-RES-04/ISSUE-011) rides on the
        // detail read so the UI never re-derives the rule: the same
        // ReopenPolicy that gates TicketLifecycleAppService.ReopenAsync
        // computes the flag here. Lifecycle only — roles and the caller's
        // access to the ticket are enforced at the Reopen endpoint, never
        // predicted on a read.
        var currentResolution = ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            ? await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken)
            : null;

        // The window runs from closure, so the closure moment comes from the
        // lifecycle history Close wrote — the same source ReopenAsync reads,
        // so the button and the action can never disagree about the deadline.
        var closedAt = ticket.TicketStatus is TicketStatus.Closed
            ? (await statusHistoryRepository.GetLatestTransitionIntoAsync(
                ticketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, cancellationToken))?.OccurredAtUtc
            : null;

        var detail = ToDetailDto(ticket) with
        {
            ResolvedAtUtc = currentResolution?.ResolvedAtUtc,
            ClosedAtUtc = closedAt,
            IsReopenEligible = reopenPolicy.IsReopenEligible(
                ticket.TicketStatus, ticket.ResolutionOutcome, closedAt, timeProvider.GetUtcNow().UtcDateTime)
        };

        // Workflow identity (Administration / Workflow Designer phase): the
        // request type's name and the PINNED version's workflow name and
        // number — read-only display facts, resolved by name so no page ever
        // shows a raw configuration id.
        if (ticket.RequestTypeId is { } requestTypeId && requestTypeRepository is not null)
        {
            var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            detail = detail with { RequestTypeName = requestType?.Name };
        }

        if (ticket.WorkflowTemplateId is { } versionId && workflowTemplateRepository is not null)
        {
            var version = await workflowTemplateRepository.GetByIdAsync(versionId, cancellationToken);
            if (version is not null)
            {
                var workflow = workflowRepository is null ? null : await workflowRepository.GetByIdAsync(version.WorkflowId, cancellationToken);
                detail = detail with
                {
                    WorkflowName = workflow?.Name ?? version.Name,
                    WorkflowVersionNumber = version.VersionNumber
                };
            }
        }

        // Originating channel (Channel Management phase): the channel the
        // ticket ENTERED the system on, read from its originating
        // interaction and resolved by name — including a channel that has
        // since been deactivated, so history keeps showing it. Later
        // interactions on other channels never change it.
        if (interactionRepository is not null)
        {
            var originating = await interactionRepository.GetOriginatingAsync(ticketId, cancellationToken);
            if (originating is not null)
            {
                var channel = channelRepository is null ? null : await channelRepository.GetByIdAsync(originating.ChannelId, cancellationToken);
                detail = detail with
                {
                    OriginatingChannelId = originating.ChannelId,
                    OriginatingChannelName = channel?.Name
                };
            }
        }

        return TicketQueryResultDto<TicketDetailDto>.Success(detail);
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

    /// <summary>
    /// The ticket's lifecycle history (ADR-0018's append-only
    /// <c>TicketStatusHistory</c>), behind the same department-visibility
    /// check as the detail read — history is as sensitive as the ticket it
    /// describes.
    ///
    /// <para>
    /// Added with the approved Reopen rule, which requires the reopen reason
    /// to be visible in Ticket Details: the reason was already being written
    /// to this table and had no read path, so exposing the table was the fix
    /// rather than a parallel activity store. Every other dimension change
    /// comes with it, which is why the Activity feed can finally show status,
    /// verification and SLA transitions at all.
    /// </para>
    /// </summary>
    public async Task<TicketQueryResultDto<TicketLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketLifecycleHistoryDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await CanViewDepartmentAsync(callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketQueryResultDto<TicketLifecycleHistoryDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var entries = await statusHistoryRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        return TicketQueryResultDto<TicketLifecycleHistoryDto>.Success(
            new TicketLifecycleHistoryDto([.. entries.Select(ToHistoryEntryDto)]));
    }

    /// <summary>Renders the stored bytes as their dimension's enum names, so no client re-implements ADR-0008's mappings.</summary>
    private static TicketStatusHistoryEntryDto ToHistoryEntryDto(TicketStatusHistory entry) => new(
        entry.Dimension.ToString(),
        DescribeValue(entry.Dimension, entry.OldValue),
        DescribeValue(entry.Dimension, entry.NewValue) ?? entry.NewValue.ToString(),
        entry.ActorEmployeeId,
        entry.ActorIsSystem,
        entry.Note,
        entry.CorrelationId,
        entry.OccurredAtUtc);

    private static string? DescribeValue(TicketStatusDimension dimension, byte? value) => value switch
    {
        null => null,
        { } raw => dimension switch
        {
            TicketStatusDimension.TicketStatus => NameOf<TicketStatus>(raw),
            TicketStatusDimension.VerificationStatus => NameOf<CrmVerificationStatus>(raw),
            TicketStatusDimension.EscalationLevel => NameOf<EscalationLevel>(raw),
            TicketStatusDimension.SlaState => NameOf<SlaState>(raw),
            TicketStatusDimension.ResolutionOutcome => NameOf<ResolutionOutcome>(raw),
            _ => raw.ToString()
        }
    };

    /// <summary>Falls back to the raw byte rather than throwing: a value written by a newer version must still render in an older reader.</summary>
    private static string NameOf<TEnum>(byte value) where TEnum : struct, Enum =>
        Enum.IsDefined(typeof(TEnum), value) ? ((TEnum)Enum.ToObject(typeof(TEnum), value)).ToString() : value.ToString();

    internal Task<bool> CanViewDepartmentAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int departmentId, CancellationToken cancellationToken) =>
        TicketVisibilityRule.CanViewDepartmentAsync(
            userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, departmentId, cancellationToken);

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
        ticket.ExternalUnitId,
        RequestTypeId: ticket.RequestTypeId,
        WorkflowTemplateId: ticket.WorkflowTemplateId,
        IsClassified: ticket.IsClassified);
}
