using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Shared request/response handling for every TigerCS.Api client: JSON
/// (de)serialization with the Api's own wire shape, status-code-to-<see
/// cref="ApiOutcome"/> mapping, and network-failure containment so a page
/// can always render an error state instead of throwing.
/// </summary>
public abstract class ApiClientBase(HttpClient httpClient)
{
    protected HttpClient Http { get; } = httpClient;

    protected async Task<ApiResult<TResponse>> GetAsync<TResponse>(string requestUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(requestUri, cancellationToken);
            return await ToResultAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    protected async Task<ApiResult<TResponse>> PostAsync<TRequest, TResponse>(
        string requestUri, TRequest body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(requestUri, body, ApiJson.Options, cancellationToken);
            return await ToResultAsync<TResponse>(response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    protected async Task<ApiResult> PostAsync<TRequest>(string requestUri, TRequest body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(requestUri, body, ApiJson.Options, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return ApiResult.Success();
            }

            var (outcome, detail) = await DescribeFailureAsync(response, cancellationToken);
            return ApiResult.Failure(outcome, detail);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    private static async Task<ApiResult<TResponse>> ToResultAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return ApiResult<TResponse>.Success(default!);
            }

            var value = await response.Content.ReadFromJsonAsync<TResponse>(ApiJson.Options, cancellationToken);
            return ApiResult<TResponse>.Success(value!);
        }

        var (outcome, detail) = await DescribeFailureAsync(response, cancellationToken);
        return ApiResult<TResponse>.Failure(outcome, detail);
    }

    private static async Task<(ApiOutcome Outcome, string? Detail)> DescribeFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var outcome = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ApiOutcome.ValidationError,
            HttpStatusCode.Unauthorized => ApiOutcome.Unauthorized,
            HttpStatusCode.Forbidden => ApiOutcome.Forbidden,
            HttpStatusCode.NotFound => ApiOutcome.NotFound,
            HttpStatusCode.Conflict => ApiOutcome.Conflict,
            HttpStatusCode.Locked => ApiOutcome.Locked,
            HttpStatusCode.UnprocessableEntity => ApiOutcome.UnprocessableEntity,
            HttpStatusCode.BadGateway => ApiOutcome.BadGateway,
            _ => ApiOutcome.Unknown
        };

        string? detail = null;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("detail", out var detailEl))
            {
                detail = detailEl.GetString();
            }
            else if (document.RootElement.TryGetProperty("title", out var titleEl))
            {
                detail = titleEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Empty or non-JSON body — the mapped outcome still carries the status code's meaning.
        }

        return (outcome, detail);
    }
}
