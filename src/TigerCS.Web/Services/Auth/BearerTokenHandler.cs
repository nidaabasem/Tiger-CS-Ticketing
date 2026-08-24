using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// Reads the TigerCS.Api access token out of the signed-in user's own
/// claims (stored server-side inside the encrypted, HttpOnly session
/// cookie — never exposed to the browser) and attaches it as a Bearer
/// credential on every outgoing call to the Api.
/// </summary>
public sealed class BearerTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = httpContextAccessor.HttpContext?.User.FindFirst(TigerCsClaimTypes.AccessToken)?.Value;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
