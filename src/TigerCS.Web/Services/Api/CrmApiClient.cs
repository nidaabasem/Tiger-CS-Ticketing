using System.Web;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/crm</c> endpoints (CS Agent/CS Supervisor only — the Api rejects other roles with 403).</summary>
public sealed class CrmApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<IReadOnlyList<UnitVerificationResponseDto>>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["unitNumber"] = unitNumber;
        if (!string.IsNullOrWhiteSpace(propertyName)) query["propertyName"] = propertyName;

        return GetAsync<IReadOnlyList<UnitVerificationResponseDto>>($"api/crm/units/search?{query}", cancellationToken);
    }

    public Task<ApiResult<IReadOnlyList<ContactVerificationResponseDto>>> GetContactsAsync(
        string crmUnitId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<ContactVerificationResponseDto>>($"api/crm/units/{Uri.EscapeDataString(crmUnitId)}/contacts", cancellationToken);
}
