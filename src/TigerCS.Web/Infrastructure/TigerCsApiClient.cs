using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// The one place an HTTP call to TigerCS.Api is actually made.
///
/// <para>
/// Every typed client goes through here, so the rules that must hold for all
/// of them hold in one place: a non-success status becomes an
/// <see cref="ApiProblem"/> rather than an exception, a transport failure
/// becomes a 502-shaped problem rather than a stack trace on a page, and
/// nothing that could carry a token or a response body reaches a log.
/// </para>
///
/// <para>
/// <b>Logging discipline.</b> Only the method, the path, the status code and
/// the correlation id are logged. Not the request body (it can carry a
/// password on the login path), not the response body (it can carry customer
/// contact data), and not any header (the Authorization header carries the
/// access token).
/// </para>
/// </summary>
public sealed class TigerCsApiClient(
    HttpClient httpClient,
    CorrelationIdAccessor correlationIdAccessor,
    ILogger<TigerCsApiClient> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    public Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken cancellationToken) =>
        SendAsync<T>(
            new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body, options: SerializerOptions)
            },
            cancellationToken);

    public Task<ApiResult> PostAsync(string path, object? body, CancellationToken cancellationToken) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = body is null ? null : JsonContent.Create(body, options: SerializerOptions)
            },
            cancellationToken);

    /// <summary>
    /// Sends a request whose success body is one of two shapes. Used by the
    /// provisional-ticket path, where 201 carries a ticket and 200 carries an
    /// intake record - two different types, both success (ISSUE-006).
    /// </summary>
    public async Task<ApiResult<OneOf<TCreated, TOk>>> PostEitherAsync<TCreated, TOk>(
        string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: SerializerOptions)
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return ApiResult<OneOf<TCreated, TOk>>.Failure(Unreachable(request, exception));
        }

        using (response)
        {
            LogOutcome(request, response.StatusCode);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = await ReadAsync<TCreated>(response, cancellationToken);
                return created is null
                    ? ApiResult<OneOf<TCreated, TOk>>.Failure(Malformed(response.StatusCode))
                    : ApiResult<OneOf<TCreated, TOk>>.Success(OneOf<TCreated, TOk>.FromFirst(created));
            }

            if (response.IsSuccessStatusCode)
            {
                var ok = await ReadAsync<TOk>(response, cancellationToken);
                return ok is null
                    ? ApiResult<OneOf<TCreated, TOk>>.Failure(Malformed(response.StatusCode))
                    : ApiResult<OneOf<TCreated, TOk>>.Success(OneOf<TCreated, TOk>.FromSecond(ok));
            }

            return ApiResult<OneOf<TCreated, TOk>>.Failure(await ReadProblemAsync(response, cancellationToken));
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                  && !cancellationToken.IsCancellationRequested)
            {
                return ApiResult<T>.Failure(Unreachable(request, exception));
            }

            using (response)
            {
                LogOutcome(request, response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    return ApiResult<T>.Failure(await ReadProblemAsync(response, cancellationToken));
                }

                var value = await ReadAsync<T>(response, cancellationToken);
                return value is null
                    ? ApiResult<T>.Failure(Malformed(response.StatusCode))
                    : ApiResult<T>.Success(value);
            }
        }
    }

    private async Task<ApiResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                  && !cancellationToken.IsCancellationRequested)
            {
                return ApiResult.Failure(Unreachable(request, exception));
            }

            using (response)
            {
                LogOutcome(request, response.StatusCode);

                return response.IsSuccessStatusCode
                    ? ApiResult.Success()
                    : ApiResult.Failure(await ReadProblemAsync(response, cancellationToken));
            }
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private static async Task<ApiProblem> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(SerializerOptions, cancellationToken);
            if (problem is not null)
            {
                // The status line is authoritative: a body's own "status" can be
                // absent, and Forbid()/NotFound() send no body at all.
                return new ApiProblem
                {
                    ProblemType = problem.ProblemType,
                    Title = problem.Title,
                    Detail = problem.Detail,
                    Status = (int)response.StatusCode,
                    Errors = problem.Errors,
                    StatusCode = response.StatusCode
                };
            }
        }
        catch (JsonException)
        {
            // An empty or non-JSON error body is normal for 403/404 here.
        }
        catch (NotSupportedException)
        {
            // Same.
        }

        return ApiProblem.FromStatus(response.StatusCode);
    }

    private static ApiProblem Malformed(HttpStatusCode statusCode) => new()
    {
        StatusCode = statusCode,
        Status = (int)statusCode,
        Title = "Unexpected response",
        Detail = "The service returned a response this application could not read."
    };

    private ApiProblem Unreachable(HttpRequestMessage request, Exception exception)
    {
        // The exception's message is logged for operators but never surfaced:
        // it can name internal hosts and ports.
        logger.LogError(
            exception,
            "TigerCS.Api unreachable for {Method} {Path}. CorrelationId={CorrelationId}",
            request.Method.Method,
            request.RequestUri?.AbsolutePath,
            correlationIdAccessor.GetCorrelationId());

        return new ApiProblem
        {
            StatusCode = HttpStatusCode.BadGateway,
            Status = (int)HttpStatusCode.BadGateway,
            ProblemType = "https://tigercs.internal/problems/api-unreachable",
            Title = "Service unavailable",
            Detail = "The ticketing service could not be reached. Please try again shortly."
        };
    }

    private void LogOutcome(HttpRequestMessage request, HttpStatusCode statusCode) =>
        logger.Log(
            statusCode >= HttpStatusCode.InternalServerError ? LogLevel.Warning : LogLevel.Debug,
            "TigerCS.Api {Method} {Path} -> {StatusCode}. CorrelationId={CorrelationId}",
            request.Method.Method,
            request.RequestUri?.AbsolutePath,
            (int)statusCode,
            correlationIdAccessor.GetCorrelationId());
}
