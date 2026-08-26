using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/categories</c> endpoint.</summary>
public sealed class CategoriesApiClient(HttpClient httpClient, ILogger<CategoriesApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<IReadOnlyCollection<CategoryDto>>> GetCategoriesAsync(int? departmentId, CancellationToken cancellationToken)
    {
        if (departmentId is null)
        {
            return GetAsync<IReadOnlyCollection<CategoryDto>>("api/categories", cancellationToken);
        }

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["departmentId"] = departmentId.Value.ToString();
        return GetAsync<IReadOnlyCollection<CategoryDto>>($"api/categories?{query}", cancellationToken);
    }
}
