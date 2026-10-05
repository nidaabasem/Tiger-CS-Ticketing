using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// The Payment tab's only route to Collections data: TigerCS.Api's
/// <c>api/genesys/collections</c> routes, called server-side as the signed-in
/// user (BearerTokenHandler). The browser never sees an Api credential, a
/// Genesys credential or a financial source — and nothing here touches SQL.
/// </summary>
public sealed class CollectionsApiClient(HttpClient httpClient, ILogger<CollectionsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    private const string Base = "api/genesys/collections";

    public Task<ApiResult<CollectionsOutstandingResponseDto>> GetOutstandingAsync(string crmCustomerId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsOutstandingResponseDto>($"{Base}/customers/{Uri.EscapeDataString(crmCustomerId)}/outstanding", cancellationToken);

    public Task<ApiResult<CollectionsPaymentsResponseDto>> GetPaymentsAsync(
        string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(accountId)) query["accountId"] = accountId;
        query["includeUnposted"] = "true";
        query["page"] = page.ToString(CultureInfo.InvariantCulture);
        query["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture);
        return GetAsync<CollectionsPaymentsResponseDto>($"{Base}/customers/{Uri.EscapeDataString(crmCustomerId)}/payments?{query}", cancellationToken);
    }

    public Task<ApiResult<CollectionsReminderListResultDto>> GetRemindersAsync(
        string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(accountId)) query["accountId"] = accountId;
        query["page"] = page.ToString(CultureInfo.InvariantCulture);
        query["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture);
        return GetAsync<CollectionsReminderListResultDto>($"{Base}/customers/{Uri.EscapeDataString(crmCustomerId)}/reminders?{query}", cancellationToken);
    }

    public Task<ApiResult<CreateCollectionsReminderResponseDto>> SendReminderAsync(
        CreateCollectionsReminderRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateCollectionsReminderRequestDto, CreateCollectionsReminderResponseDto>($"{Base}/reminders", request, cancellationToken);
}
