using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// One module's tile on the Administration overview: what it manages (from
/// <see cref="AdminModules"/>) and how much of it is configured. Counts are
/// display only — nothing here decides what the module contains.
/// </summary>
/// <param name="Module">The module this card opens.</param>
/// <param name="ActiveCount">Records offered for new work, or <c>null</c> when the count could not be loaded.</param>
/// <param name="InactiveCount">Records kept for history but no longer offered.</param>
/// <param name="AttentionLabel">The one thing about this module that needs a decision, if anything does.</param>
public sealed record AdminModuleCard(AdminModule Module, int? ActiveCount, int? InactiveCount, string? AttentionLabel);

public sealed class IndexModel(AdminApiClient adminApi) : AdminPageModel
{
    public IReadOnlyList<AdminModuleCard> Cards { get; private set; } = [];

    /// <summary>Configuration records across every module, active and inactive.</summary>
    public int TotalConfigured { get; private set; }

    public int TotalActive { get; private set; }

    public int TotalInactive { get; private set; }

    /// <summary>Active records that cannot be used yet because no workflow version is published behind them.</summary>
    public int NeedsAttention { get; private set; }

    /// <summary>True when every module answered — a summary built from a partial answer would understate the totals.</summary>
    public bool SummaryIsComplete { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Each module is asked once, for everything it has; active and
        // inactive are counted here rather than in a second round trip.
        var activeUsers = adminApi.GetUsersAsync(null, includeInactive: false, 1, 1, cancellationToken);
        var allUsers = adminApi.GetUsersAsync(null, includeInactive: true, 1, 1, cancellationToken);
        var departments = adminApi.GetDepartmentsAsync(includeInactive: true, cancellationToken);
        var requestTypes = adminApi.GetRequestTypesAsync(null, includeInactive: true, cancellationToken);
        var workflows = adminApi.GetWorkflowsAsync(includeInactive: true, cancellationToken);
        var channels = adminApi.GetChannelsAsync(includeInactive: true, cancellationToken);
        await Task.WhenAll(activeUsers, allUsers, departments, requestTypes, workflows, channels);

        var activeUserCount = activeUsers.Result.IsSuccess ? activeUsers.Result.Value?.TotalCount : null;
        var totalUserCount = allUsers.Result.IsSuccess ? allUsers.Result.Value?.TotalCount : null;
        var departmentList = departments.Result.IsSuccess ? departments.Result.Value : null;
        var requestTypeList = requestTypes.Result.IsSuccess ? requestTypes.Result.Value : null;
        var workflowList = workflows.Result.IsSuccess ? workflows.Result.Value : null;
        var channelList = channels.Result.IsSuccess ? channels.Result.Value : null;

        // A workflow with no published version, and a request type pointing at
        // one, cannot carry a new ticket yet. That is the only thing on this
        // page that asks the viewer to do something.
        var draftOnlyWorkflows = workflowList?.Count(w => w.IsActive && w.ActiveVersionNumber is null) ?? 0;
        var blockedRequestTypes = requestTypeList?.Count(r => r.IsActive && r.ActiveVersionNumber is null) ?? 0;

        Cards =
        [
            new(AdminModules.Users, activeUserCount,
                totalUserCount is { } total && activeUserCount is { } active ? total - active : null, null),
            new(AdminModules.Departments, departmentList?.Count(d => d.IsActive), departmentList?.Count(d => !d.IsActive), null),
            new(AdminModules.RequestTypes, requestTypeList?.Count(r => r.IsActive), requestTypeList?.Count(r => !r.IsActive),
                blockedRequestTypes == 0 ? null : $"{blockedRequestTypes} without a published workflow"),
            new(AdminModules.Workflows, workflowList?.Count(w => w.IsActive), workflowList?.Count(w => !w.IsActive),
                draftOnlyWorkflows == 0 ? null : $"{draftOnlyWorkflows} never published"),
            new(AdminModules.Channels, channelList?.Count(c => c.IsActive), channelList?.Count(c => !c.IsActive), null)
        ];

        SummaryIsComplete = Cards.All(c => c.ActiveCount is not null && c.InactiveCount is not null);
        TotalActive = Cards.Sum(c => c.ActiveCount ?? 0);
        TotalInactive = Cards.Sum(c => c.InactiveCount ?? 0);
        TotalConfigured = TotalActive + TotalInactive;
        NeedsAttention = draftOnlyWorkflows + blockedRequestTypes;
    }
}
