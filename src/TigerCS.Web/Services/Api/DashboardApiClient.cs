using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/dashboard</c> endpoint — the operational KPI
/// counts and Tickets Requiring Attention rows, computed server-side over
/// the caller's own visible-department scope.
/// </summary>
public sealed class DashboardApiClient(HttpClient httpClient, ILogger<DashboardApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<DashboardSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken) =>
        GetAsync<DashboardSummaryDto>("api/dashboard", cancellationToken);
}
