using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Shared request/response handling for every TigerCS.Api client: JSON
/// (de)serialization with the Api's own wire shape, status-code-to-<see
/// cref="ApiOutcome"/> mapping, and network-failure containment so a page
/// can always render an error state instead of throwing.
/// </summary>
/// <remarks>
/// Every non-success outcome — a mapped HTTP failure or a caught
/// exception — is logged here so a Development run always has a
/// diagnosable trail behind whatever generic message the page shows.
/// Only the HTTP method, the relative endpoint, the status code, the
/// response's own "detail"/"title" (never a raw response body), and the
/// exception's type/message are logged — never the Authorization header,
/// the bearer token, cookies, or request/response bodies that could carry
/// credentials.
/// </remarks>
public abstract class ApiClientBase(HttpClient httpClient, ILogger logger)
{
    protected HttpClient Http { get; } = httpClient;
    private ILogger Logger { get; } = logger;

    protected async Task<ApiResult<TResponse>> GetAsync<TResponse>(string requestUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(requestUri, cancellationToken);
            return await ToResultAsync<TResponse>(HttpMethod.Get, requestUri, response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(HttpMethod.Get, requestUri, ex);
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(HttpMethod.Get, requestUri, ex);
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    protected async Task<ApiResult<TResponse>> PostAsync<TRequest, TResponse>(
        string requestUri, TRequest body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(requestUri, body, ApiJson.Options, cancellationToken);
            return await ToResultAsync<TResponse>(HttpMethod.Post, requestUri, response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(HttpMethod.Post, requestUri, ex);
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(HttpMethod.Post, requestUri, ex);
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

            var (outcome, detail) = await DescribeFailureAsync(HttpMethod.Post, requestUri, response, cancellationToken);
            return ApiResult.Failure(outcome, detail);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(HttpMethod.Post, requestUri, ex);
            return ApiResult.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(HttpMethod.Post, requestUri, ex);
            return ApiResult.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    protected Task<ApiResult<TResponse>> PutAsync<TRequest, TResponse>(
        string requestUri, TRequest body, CancellationToken cancellationToken) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Put, requestUri, body, cancellationToken);

    protected Task<ApiResult<TResponse>> PatchAsync<TRequest, TResponse>(
        string requestUri, TRequest body, CancellationToken cancellationToken) =>
        SendJsonAsync<TRequest, TResponse>(HttpMethod.Patch, requestUri, body, cancellationToken);

    protected Task<ApiResult<TResponse>> DeleteAsync<TResponse>(string requestUri, CancellationToken cancellationToken) =>
        SendJsonAsync<object?, TResponse>(HttpMethod.Delete, requestUri, null, cancellationToken);

    protected async Task<ApiResult> DeleteAsync(string requestUri, CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync<object?, object?>(HttpMethod.Delete, requestUri, null, cancellationToken);
        return result.IsSuccess ? ApiResult.Success() : ApiResult.Failure(result.Outcome, result.Detail);
    }

    /// <summary>One implementation for the verbs that carry an optional JSON body and return a JSON body (PUT/PATCH/DELETE); GET/POST keep their original shape above.</summary>
    private async Task<ApiResult<TResponse>> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method, string requestUri, TRequest? body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: ApiJson.Options);
            }

            using var response = await Http.SendAsync(request, cancellationToken);
            return await ToResultAsync<TResponse>(method, requestUri, response, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(method, requestUri, ex);
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(method, requestUri, ex);
            return ApiResult<TResponse>.Failure(ApiOutcome.Unreachable, "The request timed out.");
        }
    }

    private async Task<ApiResult<TResponse>> ToResultAsync<TResponse>(
        HttpMethod method, string requestUri, HttpResponseMessage response, CancellationToken cancellationToken)
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

        var (outcome, detail) = await DescribeFailureAsync(method, requestUri, response, cancellationToken);
        return ApiResult<TResponse>.Failure(outcome, detail);
    }

    private async Task<(ApiOutcome Outcome, string? Detail)> DescribeFailureAsync(
        HttpMethod method, string requestUri, HttpResponseMessage response, CancellationToken cancellationToken)
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
            if (document.RootElement.TryGetProperty("errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Object)
            {
                // ValidationProblemDetails: every message from every key, so
                // an administration form can show the whole validation summary.
                var messages = errorsEl.EnumerateObject()
                    .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array
                        ? p.Value.EnumerateArray().Select(e => e.GetString())
                        : [p.Value.GetString()])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
                if (messages.Count > 0)
                {
                    detail = string.Join(" ", messages);
                }
            }

            if (detail is null && document.RootElement.TryGetProperty("detail", out var detailEl))
            {
                detail = detailEl.GetString();
            }
            else if (detail is null && document.RootElement.TryGetProperty("title", out var titleEl))
            {
                detail = titleEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Empty or non-JSON body — the mapped outcome still carries the status code's meaning.
        }

        Logger.LogWarning(
            "TigerCS.Api call failed: {HttpMethod} {RequestUri} -> {StatusCode} ({Outcome}). Detail: {Detail}",
            method, requestUri, (int)response.StatusCode, outcome, detail ?? "(none)");

        return (outcome, detail);
    }

    private void LogUnreachable(HttpMethod method, string requestUri, Exception ex) =>
        Logger.LogError(
            "TigerCS.Api call threw {ExceptionType} calling {HttpMethod} {RequestUri}: {ExceptionMessage}",
            ex.GetType().Name, method, requestUri, ex.Message);
}
