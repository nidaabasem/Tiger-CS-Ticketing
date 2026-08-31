using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/customers/crm/{crmCustomerId}/ticket-history</c>
/// endpoint — Customer History for a CRM-verified customer, keyed by the
/// exact CrmBuyerCustomerId the agent selected. Used by the New Ticket
/// wizard's Step 3 preview only; Ticket Details uses
/// <see cref="TicketsApiClient.GetCustomerHistoryAsync"/> instead (it derives
/// the identity from the ticket itself, verified or unverified).
/// </summary>
public sealed class CustomerHistoryApiClient(HttpClient httpClient, ILogger<CustomerHistoryApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<CustomerHistoryDto>> GetByCrmCustomerIdAsync(int crmBuyerCustomerId, int? limit, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (limit is int l) query["limit"] = l.ToString();

        return GetAsync<CustomerHistoryDto>($"api/customers/crm/{crmBuyerCustomerId}/ticket-history?{query}", cancellationToken);
    }
}
