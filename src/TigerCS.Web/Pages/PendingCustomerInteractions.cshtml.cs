using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>One row of the agent work list, with the department and channel resolved to names.</summary>
public sealed record PendingInteractionRow(AgentHandoffDto Handoff, string? DepartmentName, string? ChannelName);

/// <summary>
/// Customer interactions waiting for a human agent — on every channel.
///
/// <para>
/// <b>Deliberately not a "Callback List".</b> Most of what lands here is not a
/// phone call: website chats a bot could not finish, WhatsApp threads,
/// social-media messages, virtual-agent handovers. A callback is one possible
/// follow-up mode, shown as such in its own column.
/// </para>
///
/// <para>
/// The actions are Open Ticket, Start Handling, Complete and Cancel. There is
/// deliberately <b>no</b> "Call" or "Reply on WhatsApp" button: Genesys owns
/// channel delivery and no supported action for it has been confirmed, so
/// offering one would be a promise this system cannot keep.
/// </para>
/// </summary>
public sealed class PendingCustomerInteractionsModel(
    PendingCustomerInteractionsApiClient handoffsApiClient,
    ChannelsApiClient channelsApiClient,
    TicketNameResolver nameResolver) : PageModel
{
    public byte? ChannelId { get; set; }
    public int? DepartmentId { get; set; }
    public bool MineOnly { get; set; }
    public bool UnassignedOnly { get; set; }
    public bool IncludeResolved { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    public ApiOutcome Outcome { get; private set; } = ApiOutcome.Success;

    /// <summary>Set when an action failed — shown above the list rather than swallowed.</summary>
    public string? ActionError { get; private set; }

    public IReadOnlyList<PendingInteractionRow> Rows { get; private set; } = [];
    public IReadOnlyList<ChannelDto> Channels { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public TicketNameResolver NameResolver => nameResolver;
    public CurrentUser? Viewer { get; private set; }

    /// <summary>How many are still waiting for anyone to take them — the number that matters most on this page.</summary>
    public int? WaitingCount { get; private set; }

    public Task OnGetAsync(
        byte? channelId, int? departmentId, bool mineOnly, bool unassignedOnly, bool includeResolved,
        int page, int pageSize, CancellationToken cancellationToken) =>
        LoadAsync(channelId, departmentId, mineOnly, unassignedOnly, includeResolved, page, pageSize, cancellationToken);

    public async Task<IActionResult> OnPostStartAsync(long handoffId, CancellationToken cancellationToken)
    {
        var result = await handoffsApiClient.StartAsync(handoffId, cancellationToken);
        return await AfterActionAsync(result.IsSuccess ? null : "The interaction could not be started.", cancellationToken);
    }

    public async Task<IActionResult> OnPostCompleteAsync(long handoffId, string? resolutionNote, CancellationToken cancellationToken)
    {
        var result = await handoffsApiClient.CompleteAsync(
            handoffId, new CompleteAgentHandoffRequestDto(resolutionNote), cancellationToken);
        return await AfterActionAsync(result.IsSuccess ? null : "The interaction could not be completed.", cancellationToken);
    }

    public async Task<IActionResult> OnPostCancelAsync(long handoffId, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return await AfterActionAsync("A reason is required to cancel pending customer work.", cancellationToken);
        }

        var result = await handoffsApiClient.CancelAsync(
            handoffId, new CancelAgentHandoffRequestDto(reason), cancellationToken);
        return await AfterActionAsync(result.IsSuccess ? null : "The interaction could not be cancelled.", cancellationToken);
    }

    private async Task<IActionResult> AfterActionAsync(string? error, CancellationToken cancellationToken)
    {
        ActionError = error;
        await LoadAsync(null, null, mineOnly: false, unassignedOnly: false, includeResolved: false, 1, 20, cancellationToken);
        return Page();
    }

    private async Task LoadAsync(
        byte? channelId, int? departmentId, bool mineOnly, bool unassignedOnly, bool includeResolved,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);

        ChannelId = channelId;
        DepartmentId = departmentId;
        MineOnly = mineOnly;
        UnassignedOnly = unassignedOnly;
        IncludeResolved = includeResolved;
        PageNumber = page < 1 ? 1 : page;
        PageSize = pageSize is < 1 or > 100 ? 20 : pageSize;

        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var channelsTask = channelsApiClient.GetChannelsAsync(activeOnly: false, cancellationToken);
        var waitingTask = handoffsApiClient.ListAsync(
            new AgentHandoffListRequestDto(UnassignedOnly: true, Page: 1, PageSize: 1), cancellationToken);

        var result = await handoffsApiClient.ListAsync(
            new AgentHandoffListRequestDto(
                DepartmentId, ChannelId,
                mineOnly ? Viewer?.EmployeeId : null,
                UnassignedOnly, IncludeResolved, PageNumber, PageSize),
            cancellationToken);

        Outcome = result.Outcome;

        if (result.IsSuccess && result.Value is not null)
        {
            TotalCount = result.Value.TotalCount;
            var channelsResult = await channelsTask;
            Channels = channelsResult.IsSuccess ? channelsResult.Value ?? [] : [];

            Rows = result.Value.Items
                .Select(h => new PendingInteractionRow(
                    h,
                    nameResolver.TryGetDepartmentName(h.DepartmentId),
                    Channels.FirstOrDefault(c => c.ChannelId == h.ChannelId)?.Name))
                .ToList();
        }
        else
        {
            _ = await channelsTask;
        }

        var waiting = await waitingTask;
        WaitingCount = waiting.IsSuccess ? waiting.Value?.TotalCount : null;
    }
}
