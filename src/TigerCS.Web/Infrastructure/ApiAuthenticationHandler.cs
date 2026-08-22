using System.Net.Http.Headers;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// Attaches the signed-in user's bearer token and the request's correlation id
/// to every outbound API call.
///
/// <para>
/// Doing this in one delegating handler rather than in each typed client is
/// what makes "no call is ever made unauthenticated by accident" a property of
/// the pipeline instead of a habit. It also keeps the token read to a single
/// place: no typed client sees the token value at all.
/// </para>
/// </summary>
public sealed class ApiAuthenticationHandler(
    AccessTokenAccessor accessTokenAccessor,
    CorrelationIdAccessor correlationIdAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = accessTokenAccessor.GetAccessToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!request.Headers.Contains(CorrelationIdAccessor.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(
                CorrelationIdAccessor.HeaderName, correlationIdAccessor.GetCorrelationId());
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
