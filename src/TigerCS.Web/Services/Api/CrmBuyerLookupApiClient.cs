using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's real CRM Buyer Lookup endpoint —
/// <c>GET api/crm/buyers?phoneNumber={phoneNumber}</c> (<c>CrmController</c> →
/// <c>CrmBuyerLookupAppService</c> → <c>CrmBuyerHttpGateway</c> → the legacy
/// CRM's own <c>GetBuyerByPhone</c>). This is the only CRM lookup the New
/// Ticket wizard's phone search calls — never the generic CRM/PACT/Tasleeh
/// <c>CustomerLookupApiClient</c>, and never CRM directly: the browser never
/// sees <c>Crm:SecretKey</c>, which stays server-to-server inside
/// <c>CrmBuyerHttpGateway</c>.
/// </summary>
public sealed class CrmBuyerLookupApiClient(HttpClient httpClient, ILogger<CrmBuyerLookupApiClient> logger)
    : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<IReadOnlyList<CrmBuyerMatchDto>>> SearchByPhoneAsync(
        string phoneNumber, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<CrmBuyerMatchDto>>($"api/crm/buyers?phoneNumber={Uri.EscapeDataString(phoneNumber)}", cancellationToken);
}
