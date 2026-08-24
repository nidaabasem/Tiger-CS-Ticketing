using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/categories</c> endpoint.</summary>
public sealed class CategoriesApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<IReadOnlyList<CategoryResponseDto>>> ListActiveAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<CategoryResponseDto>>("api/categories", cancellationToken);
}
