extern alias TigerCsWeb;

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TigerCsWeb::TigerCS.Web.Services.Auth;

namespace TigerCS.Tests.Web;

/// <summary>
/// <see cref="BearerTokenHandler"/> is the one place a TigerCS.Web sign-in
/// becomes an <c>Authorization: Bearer</c> header on an outgoing TigerCS.Api
/// call — every protected typed client (<c>Program.cs</c>) carries it. The
/// fact Swagger can call TigerCS.Api directly with a pasted token proves
/// nothing about whether TigerCS.Web itself forwards one; this exercises the
/// handler in isolation from the rest of the DI-wired HttpClient pipeline.
/// </summary>
public sealed class BearerTokenHandlerTests
{
    private sealed class CapturingHandler : DelegatingHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CapturingHandler Inner) CreateClient(HttpContext? httpContext)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var handler = new BearerTokenHandler(accessor) { InnerHandler = new CapturingHandler() };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, (CapturingHandler)handler.InnerHandler);
    }

    [Fact]
    public async Task SignedInUser_AccessTokenClaim_IsAttachedAsBearerCredential()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TigerCsClaimTypes.AccessToken, "the-jwt-value")]))
        };
        var (client, inner) = CreateClient(httpContext);

        await client.GetAsync("api/departments");

        Assert.Equal("Bearer", inner.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("the-jwt-value", inner.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task NoHttpContext_NoAuthorizationHeaderAttached_NoException()
    {
        // AuthApiClient's own login/logout calls run before any Web session exists.
        var (client, inner) = CreateClient(httpContext: null);

        await client.GetAsync("api/departments");

        Assert.Null(inner.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task SignedInUser_NoAccessTokenClaim_NoAuthorizationHeaderAttached()
    {
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var (client, inner) = CreateClient(httpContext);

        await client.GetAsync("api/departments");

        Assert.Null(inner.LastRequest!.Headers.Authorization);
    }
}
