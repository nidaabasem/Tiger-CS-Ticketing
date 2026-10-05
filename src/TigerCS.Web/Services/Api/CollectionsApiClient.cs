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
