using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/customers/…</c> endpoints — the standalone
/// customer search (Dashboard/Customer Workspace) and Customer History keyed
/// by an explicit customer identity: the exact CrmBuyerCustomerId the agent
/// selected, or a PACT/Tasleeh ticket's persisted external identity pair.
/// Ticket Details uses <see cref="TicketsApiClient.GetCustomerHistoryAsync"/>
/// instead (it derives the identity from the ticket itself, verified or
/// unverified).
/// </summary>
public sealed class CustomerHistoryApiClient(HttpClient httpClient, ILogger<CustomerHistoryApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    /// <summary>The Customer Workspace search — every integrated source, by phone number only, no intake record created.</summary>
    public Task<ApiResult<CustomerSearchResultDto>> SearchCustomersAsync(string phoneNumber, CancellationToken cancellationToken) =>
        GetAsync<CustomerSearchResultDto>(
            $"api/customers/search?phoneNumber={Uri.EscapeDataString(phoneNumber)}", cancellationToken);

    public Task<ApiResult<CustomerHistoryDto>> GetByCrmCustomerIdAsync(int crmBuyerCustomerId, int? limit, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (limit is int l) query["limit"] = l.ToString();

        return GetAsync<CustomerHistoryDto>($"api/customers/crm/{crmBuyerCustomerId}/ticket-history?{query}", cancellationToken);
    }

    /// <summary>History for an externally-verified customer (PACT/Tasleeh) — keyed by the persisted source + external customer id pair, never by name or phone.</summary>
    public Task<ApiResult<CustomerHistoryDto>> GetByExternalIdentityAsync(
        string source, string externalCustomerId, int? limit, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (limit is int l) query["limit"] = l.ToString();

        return GetAsync<CustomerHistoryDto>(
            $"api/customers/external/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(externalCustomerId)}/ticket-history?{query}",
            cancellationToken);
    }
}
