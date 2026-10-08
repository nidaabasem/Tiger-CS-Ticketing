using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// The Payment tab's only route to Collections data: TigerCS.Api's internal
/// <c>/api/collections</c> routes, called server-side as the signed-in user
/// (BearerTokenHandler). The browser never sees an Api credential, a Genesys
/// credential or the financial source — and nothing here touches SQL or EF.
/// </summary>
public sealed class CollectionsApiClient(HttpClient httpClient, ILogger<CollectionsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    private const string Base = "api/collections";
    public const int PageSize = 50;

    public Task<ApiResult<CollectionsCampaignPreviewDto>> GetCampaignPreviewAsync(string stage, DateOnly? businessDate,
        int? companyId, string? search, int page, CancellationToken cancellationToken,
        DateOnly? dateFrom = null, DateOnly? dateTo = null) =>
        GetAsync<CollectionsCampaignPreviewDto>($"{Base}/campaigns/preview?{CampaignQuery(stage, businessDate, companyId, search, dateFrom, dateTo)}&page={Id(page)}", cancellationToken);

    public Task<ApiResult<CollectionsCampaignExportDto>> GetCampaignExportAsync(string stage, string mode,
        DateOnly? businessDate, int? companyId, string? search, CancellationToken cancellationToken,
        DateOnly? dateFrom = null, DateOnly? dateTo = null) =>
        GetAsync<CollectionsCampaignExportDto>($"{Base}/campaigns/export?{CampaignQuery(stage, businessDate, companyId, search, dateFrom, dateTo)}&mode={Uri.EscapeDataString(mode)}", cancellationToken);

    private static string CampaignQuery(string stage, DateOnly? businessDate, int? companyId, string? search,
        DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = stage;
        if (dateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (businessDate is { } date) query["businessDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (companyId is { } company) query["companyId"] = Id(company);
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        return query.ToString()!;
    }

    public Task<ApiResult<PactReceivableCustomersDto>> GetReceivableCustomersAsync(
        int? companyId, string? status, string? search, int page, CancellationToken cancellationToken, int? year = null, int? month = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (year is { } y) query["year"] = Id(y);
        if (month is { } m) query["month"] = Id(m);
        if (companyId is { } company) query["companyId"] = Id(company);
        if (!string.IsNullOrWhiteSpace(status)) query["status"] = status;
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;
        query["page"] = Id(page);
        query["pageSize"] = "25";
        return GetAsync<PactReceivableCustomersDto>($"{Base}/receivables/customers?{query}", cancellationToken);
    }

    /// <summary>EDSM's payment summary, resolved server-side from the customer key (PACT customers only).</summary>
    public Task<ApiResult<CollectionsPaymentSummaryResponseDto>> GetPaymentSummaryAsync(string customerKey, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentSummaryResponseDto>($"{Base}/customers/by-key/{Uri.EscapeDataString(customerKey)}/payment-summary", cancellationToken);

    public Task<ApiResult<CollectionsPaymentSummaryResponseDto>> GetLookupPaymentSummaryAsync(
        string phoneNumber, string customerKey, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentSummaryResponseDto>($"{Base}/customer-lookup/payment-summary?phoneNumber={Uri.EscapeDataString(phoneNumber)}&customerKey={Uri.EscapeDataString(customerKey)}", cancellationToken);

    public Task<ApiResult<CollectionsOutstandingResponseDto>> GetOutstandingAsync(long crmCustomerId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsOutstandingResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/outstanding?pageSize=100", cancellationToken);

    public Task<ApiResult<CollectionsInstalmentsResponseDto>> GetInstalmentsAsync(long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsInstalmentsResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/payments?{Query(accountId, ("view", "instalments"))}", cancellationToken);

    public Task<ApiResult<CollectionsPaymentHistoryResponseDto>> GetPaymentHistoryAsync(long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsPaymentHistoryResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/payments?{Query(accountId, ("view", "history"))}", cancellationToken);

    public Task<ApiResult<CollectionsReminderHistoryResponseDto>> GetRemindersAsync(long crmCustomerId, string? accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsReminderHistoryResponseDto>($"{Base}/customers/{Id(crmCustomerId)}/reminders?{Query(accountId)}", cancellationToken);

    public Task<ApiResult<CollectionsReminderCandidatesResponseDto>> GetCandidatesAsync(
        string reminderType, long crmCustomerId, string accountId, CancellationToken cancellationToken) =>
        GetAsync<CollectionsReminderCandidatesResponseDto>(
            $"{Base}/reminders/candidates?{Query(accountId, ("reminderType", reminderType), ("crmCustomerId", Id(crmCustomerId)))}", cancellationToken);

    public Task<ApiResult<CollectionsReminderJobDto>> QueueReminderAsync(
        QueueCollectionsReminderRequestDto request, string idempotencyKey, CancellationToken cancellationToken) =>
        PostAsync<QueueCollectionsReminderRequestDto, CollectionsReminderJobDto>(
            $"{Base}/reminders", request, new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey }, cancellationToken);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Query(string? accountId, params (string Name, string Value)[] extra)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(accountId)) query["accountId"] = accountId;
        foreach (var (name, value) in extra) query[name] = value;
        query["pageSize"] = PageSize.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }
}
