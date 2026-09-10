using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/pending-customer-interactions</c> endpoints —
/// the agent work list for customer interactions waiting on a human, on every
/// channel.
/// </summary>
public sealed class PendingCustomerInteractionsApiClient(
    HttpClient httpClient, ILogger<PendingCustomerInteractionsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<AgentHandoffListResultDto>> ListAsync(
        AgentHandoffListRequestDto request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString();
        if (request.ChannelId is byte channelId) query["channelId"] = channelId.ToString();
        if (request.AssignedEmployeeId is Guid assignedEmployeeId) query["assignedEmployeeId"] = assignedEmployeeId.ToString();
        if (request.UnassignedOnly) query["unassignedOnly"] = "true";
        if (request.IncludeResolved) query["includeResolved"] = "true";
        query["page"] = request.Page.ToString();
        query["pageSize"] = request.PageSize.ToString();

        return GetAsync<AgentHandoffListResultDto>($"api/pending-customer-interactions?{query}", cancellationToken);
    }

    public Task<ApiResult<AgentHandoffDto>> StartAsync(long handoffId, CancellationToken cancellationToken) =>
        PostAsync<object, AgentHandoffDto>($"api/pending-customer-interactions/{handoffId}/start", new { }, cancellationToken);

    public Task<ApiResult<AgentHandoffDto>> CompleteAsync(
        long handoffId, CompleteAgentHandoffRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CompleteAgentHandoffRequestDto, AgentHandoffDto>(
            $"api/pending-customer-interactions/{handoffId}/complete", request, cancellationToken);

    public Task<ApiResult<AgentHandoffDto>> CancelAsync(
        long handoffId, CancelAgentHandoffRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CancelAgentHandoffRequestDto, AgentHandoffDto>(
            $"api/pending-customer-interactions/{handoffId}/cancel", request, cancellationToken);
}
