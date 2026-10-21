using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tiger.TigerGroupWeb.Controllers;
using Tiger.TigerGroupWeb.Genesys;
using Tiger.TigerGroupWeb.Genesys.Auth;

namespace Tiger.TigerGroupWeb.Tests;

// ---------------------------------------------------------------------------------------------------------------------------
// Focused regression tests for the Create/Update forwarding patch (TicketingGenesysService + GenesysController).
// They use a scripted fake of TigerCS Ticketing - nothing here talks to a real Ticketing API, Genesys or the TigerGroupWeb host.
// ---------------------------------------------------------------------------------------------------------------------------

public sealed record Seen(HttpMethod Method, string Path, string? Authorization, string? IdempotencyKey, string Body, string ClientName);

/// <summary>A scripted TigerCS: /api/auth/login issues T1, T2, ...; every other call goes to <see cref="Script"/>.</summary>
public sealed class FakeTigerCs : HttpMessageHandler, IHttpClientFactory
{
    public const string LoginPath = "/api/auth/login";
    private int _logins;

    public ConcurrentQueue<Seen> Calls { get; } = new();
    public int Logins => Volatile.Read(ref _logins);
    public List<string> ClientNames { get; } = [];
    public TimeSpan? TokenValidFor { get; set; } = TimeSpan.FromMinutes(60);
    public Func<int, HttpStatusCode>? LoginStatus { get; set; }
    public string? RawLoginBody { get; set; }
    public Func<CancellationToken, Task>? LoginDelay { get; set; }
    public Func<Seen, int, CancellationToken, Task<HttpResponseMessage>> Script { get; set; } =
        (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"outcome\":\"Applied\"}", Encoding.UTF8, "application/json")
        });

    public IEnumerable<Seen> TicketCalls => Calls.Where(c => c.Path != LoginPath);

    public HttpClient CreateClient(string name)
    {
        lock (ClientNames) { ClientNames.Add(name); }
        return new HttpClient(new Named(this, name), disposeHandler: false);
    }

    private sealed class Named(FakeTigerCs inner, string name) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Options.Set(new HttpRequestOptionsKey<string>("client"), name);
            return base.SendAsync(request, ct);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Options.TryGetValue(new HttpRequestOptionsKey<string>("client"), out var client);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var seen = new Seen(
            request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.Single() : null, body, client ?? "");
        Calls.Enqueue(seen);

        if (seen.Path == LoginPath)
        {
            var n = Interlocked.Increment(ref _logins);
            if (LoginDelay is not null) { await LoginDelay(ct); }
            var status = LoginStatus?.Invoke(n) ?? HttpStatusCode.OK;
            if (status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{\"detail\":\"bad credentials for svc-genesys\"}") };
            }

            if (RawLoginBody is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawLoginBody, Encoding.UTF8, "application/json") };
            }

            var expires = TokenValidFor is { } v ? $",\"expiresAtUtc\":\"{DateTime.UtcNow.Add(v):O}\"" : "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"accessToken\":\"T{n}\"{expires}}}", Encoding.UTF8, "application/json")
            };
        }

        return await Script(seen, TicketCalls.Count(), ct);
    }

    public static HttpResponseMessage Challenge() =>
        new(HttpStatusCode.Unauthorized) { Headers = { WwwAuthenticate = { new AuthenticationHeaderValue("Bearer", "error=\"invalid_token\"") } } };
}

file sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Request.Headers.Authorization.ToString() == "Bearer genesys-ok"
            ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", "ticketing.genesys")], "Test")), "Test")))
            : Task.FromResult(AuthenticateResult.NoResult());
}

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new L(this);
    public void Dispose() { }
    public string All => string.Join("\n", Lines);

    private sealed class L(CapturedLogs o) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            o.Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

public sealed class Host : IAsyncDisposable
{
    public FakeTigerCs Upstream { get; } = new();
    public CapturedLogs Logs { get; } = new();
    public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());
    public HttpClient Client { get; private set; } = null!;
    private WebApplication _app = null!;

    public static async Task<Host> StartAsync(TimeSpan? budget = null, Action<FakeTigerCs>? configure = null)
    {
        var h = new Host();
        configure?.Invoke(h.Upstream);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(h.Logs);
        builder.Services.AddControllers().AddApplicationPart(typeof(GenesysController).Assembly);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(GenesysAuthDefaults.PolicyName, p => p.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IMemoryCache>(h.Cache);
        builder.Services.AddSingleton<IHttpClientFactory>(h.Upstream);
        builder.Services.Configure<TicketingOptions>(_ => { });
        builder.Services.AddSingleton(sp => new TicketingGenesysService(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IMemoryCache>(), sp.GetRequiredService<IOptions<TicketingOptions>>())
        { ForwardBudget = budget ?? TimeSpan.FromSeconds(30) });

        h._app = builder.Build();
        h._app.UseAuthentication();
        h._app.UseAuthorization();
        h._app.MapControllers();
        await h._app.StartAsync();

        h.Client = h._app.GetTestClient();
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "genesys-ok");
        return h;
    }

    public static TicketingGenesysService Service(FakeTigerCs upstream, IMemoryCache cache, TimeSpan? budget = null) =>
        new(upstream, cache, Options.Create(new TicketingOptions())) { ForwardBudget = budget ?? TimeSpan.FromSeconds(30) };

    public Task<HttpResponseMessage> PostAsync(string path, string json) =>
        Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> PatchAsync(string path, string json) =>
        Client.PatchAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public async ValueTask DisposeAsync() { await _app.DisposeAsync(); Client.Dispose(); }
}

public class TicketingGenesysForwardingTests
{
    // Whitespace, a \u escape, an emoji, nested objects, a large-ish number: all of it must reach TigerCS byte for byte.
    private const string UpdateBody =
        "{ \"conversationId\" : \"8f4c2e1a-6b7d-4c3a-9e52-0d1f5a7b3c90\",\n  \"requestType\": { \"requestTypeId\": 17 },\n" +
        "  \"customerActive\": true,\n  \"transcript\": [ { \"sender\": \"Customer\", \"body\": \"caf\\u00e9 😀  \\\"hi\\\"\", " +
        "\"sentAtUtc\": \"2026-10-21T09:00:03Z\", \"externalMessageId\": \"msg-0001\" } ] }";

    // ------------------------------------------------------------------ pass-through

    [Fact]
    public async Task Update_ForwardsPatchBodyWithRequestTypeAndTranscriptByteForByte_WithTheServiceToken()
    {
        await using var h = await Host.StartAsync();

        var response = await h.PatchAsync("/api/genesys/tickets/123", UpdateBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(h.Upstream.TicketCalls);
        Assert.Equal(HttpMethod.Patch, call.Method);
        Assert.Equal("/api/genesys/tickets/123", call.Path);
        Assert.Equal(UpdateBody, call.Body);
        Assert.Equal("Bearer T1", call.Authorization);          // the Ticketing service token, never the Genesys one
        Assert.Null(call.IdempotencyKey);
    }

    [Fact]
    public async Task Create_ForwardsPostBodyUnchanged()
    {
        await using var h = await Host.StartAsync();
        const string body = "{ \"conversationId\":\"c-1\",  \"channel\":\"LiveChat\", \"requestType\":{\"requestTypeId\":17} }";

        var response = await h.PostAsync("/api/genesys/tickets", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(h.Upstream.TicketCalls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/genesys/tickets", call.Path);
        Assert.Equal(body, call.Body);
    }

    [Fact]
    public async Task TheGenesysTokenIsNeverForwarded()
    {
        await using var h = await Host.StartAsync();
        await h.PatchAsync("/api/genesys/tickets/1", "{\"conversationId\":\"c\"}");

        Assert.DoesNotContain(h.Upstream.Calls, c => c.Authorization?.Contains("genesys-ok") == true);
    }

    [Fact]
    public async Task UpstreamStatusBodyContentTypeProblemDetailsAndRetryAfter_ComeBackAsTigerCsGaveThem()
    {
        var problem = "{\"type\":\"https://tigercs.internal/problems/concurrency-conflict\",\"title\":\"Café — changed\",\"status\":409}";
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(problem))
            };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
            r.Headers.TryAddWithoutValidation("Retry-After", "7");
            return Task.FromResult(r);
        });

        var response = await h.PatchAsync("/api/genesys/tickets/5", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(problem, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EveryUpstreamStatusIsPreserved_AndNeverRetried(HttpStatusCode status)
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(status, response.StatusCode);
        Assert.Single(h.Upstream.TicketCalls);
    }

    // ------------------------------------------------------------------ authentication challenge and token refresh

    [Fact]
    public async Task ABearerChallenge_RefreshesTheServiceTokenOnce_AndResendsTheIdenticalBody()
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, n, _) =>
            Task.FromResult(n == 1 ? FakeTigerCs.Challenge() : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"outcome\":\"Applied\"}", Encoding.UTF8, "application/json")
            }));

        var response = await h.PatchAsync("/api/genesys/tickets/123", UpdateBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, h.Upstream.Logins);
        var calls = h.Upstream.TicketCalls.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("Bearer T1", calls[0].Authorization);
        Assert.Equal("Bearer T2", calls[1].Authorization);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal(UpdateBody, calls[1].Body);
    }

    [Fact]
    public async Task A401WithoutABearerChallenge_IsABusinessAnswer_RelayedWithoutRefreshOrRetry()
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(h.Upstream.TicketCalls);
        Assert.Equal(1, h.Upstream.Logins);
    }

    [Fact]
    public async Task AChallengeThatRepeatsAfterTheRefresh_IsNotLoopedOn()
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) => Task.FromResult(FakeTigerCs.Challenge()));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // TigerCS's own answer, relayed
        Assert.Equal(2, h.Upstream.TicketCalls.Count());                   // original + exactly one retry
        Assert.Equal(2, h.Upstream.Logins);
    }

    [Fact]
    public async Task ARefusedTokenIsOnlyDropped_IfNoNewerTokenWasInstalledMeanwhile()
    {
        var h = new Host();
        h.Upstream.Script = (_, n, _) =>
        {
            if (n == 1)
            {
                h.Cache.Set("TigerCS_Genesys_AccessToken", "T-newer", TimeSpan.FromMinutes(5));   // another call refreshed meanwhile
                return Task.FromResult(FakeTigerCs.Challenge());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        };
        var service = Host.Service(h.Upstream, h.Cache);

        using var response = await service.UpdateTicketAsync(1, "{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, h.Upstream.Logins);                                       // no needless second login
        Assert.Equal("Bearer T-newer", h.Upstream.TicketCalls.Last().Authorization);
    }

    // ------------------------------------------------------------------ timeouts, network failures, cancellation

    [Fact]
    public async Task ATimeout_Answers504_WithoutRetry_AndTheUpstreamCallWasCancelled()
    {
        var cancelled = false;
        await using var h = await Host.StartAsync(TimeSpan.FromMilliseconds(400), u => u.Script = async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await h.PatchAsync("/api/genesys/tickets/9", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("result is unknown", json.GetProperty("message").GetString());
        Assert.Single(h.Upstream.TicketCalls);            // a timeout is never replayed
        Assert.True(cancelled);
    }

    [Fact]
    public async Task ASlowLogin_IsInsideTheSameBudget()
    {
        await using var h = await Host.StartAsync(TimeSpan.FromMilliseconds(400),
            u => u.LoginDelay = ct => Task.Delay(Timeout.Infinite, ct));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Empty(h.Upstream.TicketCalls);
    }

    [Fact]
    public async Task ANetworkFailure_Answers502WithTheUnchangedMessage_AndIsNotRetried()
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) => throw new HttpRequestException("connection reset"));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("{\"message\":\"Unable to communicate with TigerCS Ticketing.\"}", await response.Content.ReadAsStringAsync());
        Assert.Single(h.Upstream.TicketCalls);
    }

    [Fact]
    public async Task ARedirectFromTicketing_IsRefused_NotFollowed()
    {
        await using var h = await Host.StartAsync(configure: u => u.Script = (_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.test/") } }));

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(h.Upstream.TicketCalls);
    }

    [Fact]
    public async Task ARejectedServiceAccount_Answers502_WithoutLeakingTheLoginAnswer()
    {
        await using var h = await Host.StartAsync(configure: u => u.LoginStatus = _ => HttpStatusCode.Unauthorized);

        var response = await h.PostAsync("/api/genesys/tickets", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("svc-genesys", text);
        Assert.Empty(h.Upstream.TicketCalls);
    }

    [Fact]
    public async Task TheCallersCancellation_StopsTheCall_AndNothingIsReplayed()
    {
        var upstream = new FakeTigerCs { Script = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); } };
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateTicketAsync(1, "{}", cts.Token));

        Assert.Single(upstream.TicketCalls);
    }

    // ------------------------------------------------------------------ token caching

    [Fact]
    public async Task ConcurrentCallsWithAnEmptyCache_MakeOneLogin()
    {
        var upstream = new FakeTigerCs { LoginDelay = ct => Task.Delay(150, ct) };   // a login slow enough for the calls to overlap
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => service.UpdateTicketAsync(i + 1, "{}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, upstream.Logins);
        Assert.All(upstream.TicketCalls, c => Assert.Equal("Bearer T1", c.Authorization));
    }

    [Fact]
    public async Task TheLoginUsesTheNoRedirectClient_AndTheTicketCallsToo()
    {
        var upstream = new FakeTigerCs();
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        (await service.CreateTicketAsync("{}")).Dispose();

        Assert.All(upstream.Calls, c => Assert.Equal(TicketingGenesysService.NoRedirectClientName, c.ClientName));
    }

    [Theory]
    [InlineData(null, TicketingGenesysService.TokenUse.Cache, 20 * 60.0)]         // no expiry in the answer: the previous 20 minutes
    [InlineData(60 * 60.0, TicketingGenesysService.TokenUse.Cache, 20 * 60.0)]    // long-lived token: never longer than before
    [InlineData(10 * 60.0, TicketingGenesysService.TokenUse.Cache, 8 * 60.0)]     // renew 2 minutes before it expires
    [InlineData(125.0, TicketingGenesysService.TokenUse.UseOnce, 0.0)]            // valid, but not worth keeping: used once, never cached
    [InlineData(61.0, TicketingGenesysService.TokenUse.UseOnce, 0.0)]
    [InlineData(130.0, TicketingGenesysService.TokenUse.Cache, 10.0)]            // exactly the 10 s minimum worth caching
    [InlineData(59.0, TicketingGenesysService.TokenUse.Reject, 0.0)]              // too close to expiry to cover a call
    [InlineData(1.0, TicketingGenesysService.TokenUse.Reject, 0.0)]
    [InlineData(0.0, TicketingGenesysService.TokenUse.Reject, 0.0)]               // expires now
    [InlineData(-30.0, TicketingGenesysService.TokenUse.Reject, 0.0)]             // already expired
    [InlineData(-86400.0, TicketingGenesysService.TokenUse.Reject, 0.0)]
    public void AnExpiredOrNearlyExpiredTokenIsNeverCachedNorUsed(double? validForSeconds, TicketingGenesysService.TokenUse expectedUse, double expectedCacheSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = validForSeconds is { } v ? now.AddSeconds(v) : (DateTimeOffset?)null;

        var decision = TicketingGenesysService.ComputeTokenLifetime(expires, now);

        Assert.Equal(expectedUse, decision.Use);
        Assert.Equal(expectedCacheSeconds, decision.CacheFor.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    [InlineData(30)]
    public async Task AnExpiredTokenFromLogin_IsRejected_NotCached_AndNotSent(int validForSeconds)
    {
        var upstream = new FakeTigerCs { TokenValidFor = TimeSpan.FromSeconds(validForSeconds) };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Host.Service(upstream, cache);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateTicketAsync(1, "{}"));

        Assert.False(cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));
        Assert.Empty(upstream.TicketCalls);      // the expired token was never put on a request
    }

    [Fact]
    public async Task AnExpiredTokenFromLogin_Answers502_ThroughTheController_WithoutLeakingTheToken()
    {
        await using var h = await Host.StartAsync(configure: u => u.TokenValidFor = TimeSpan.FromSeconds(-5));

        var response = await h.PatchAsync("/api/genesys/tickets/1", "{\"conversationId\":\"c\"}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(h.Upstream.TicketCalls);
        Assert.DoesNotContain("T1", h.Logs.All);
        Assert.False(h.Cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));
    }

    [Fact]
    public async Task ANearlyExpiredButUsableToken_IsUsedForTheCall_ButNeverCached()
    {
        var upstream = new FakeTigerCs { TokenValidFor = TimeSpan.FromSeconds(100) };   // usable (> 60 s) but < 2 min 10 s
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Host.Service(upstream, cache);

        using var first = await service.UpdateTicketAsync(1, "{}");
        Assert.False(cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));
        using var second = await service.UpdateTicketAsync(2, "{}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, upstream.Logins);                                  // not served from the cache
        Assert.Equal(["Bearer T1", "Bearer T2"], upstream.TicketCalls.Select(c => c.Authorization));
    }

    [Fact]
    public async Task AValidToken_IsCachedAndReusedWithoutAnotherLogin()
    {
        var upstream = new FakeTigerCs { TokenValidFor = TimeSpan.FromMinutes(30) };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Host.Service(upstream, cache);

        (await service.UpdateTicketAsync(1, "{}")).Dispose();
        (await service.UpdateTicketAsync(2, "{}")).Dispose();

        Assert.Equal(1, upstream.Logins);
        Assert.True(cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));
    }

    [Theory]
    [InlineData("2099-01-01T10:00:00Z")]                  // Z
    [InlineData("2099-01-01T12:00:00+02:00")]             // explicit offset: same instant
    [InlineData("2099-01-01T10:00:00")]                   // no offset: read as UTC, not as the server's local time
    public async Task TheExpiryTimestampIsReadAsAnInstantInUtc(string expiresAtUtc)
    {
        var upstream = new FakeTigerCs();
        upstream.RawLoginBody = $"{{\"accessToken\":\"T1\",\"expiresAtUtc\":\"{expiresAtUtc}\"}}";
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Host.Service(upstream, cache);

        (await service.UpdateTicketAsync(1, "{}")).Dispose();

        Assert.True(cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));   // far-future expiry: cached (capped at 20 min)
    }

    [Theory]
    [InlineData("\"2001-01-01T10:00:00Z\"")]            // a past instant: rejected
    [InlineData("\"2001-01-01T10:00:00\"")]             // past, no offset: still read as UTC and rejected
    public async Task APastExpiryTimestamp_IsRejected_InEveryFormat(string expiresJson)
    {
        var upstream = new FakeTigerCs();
        upstream.RawLoginBody = "{\"accessToken\":\"T1\",\"expiresAtUtc\":" + expiresJson + "}";
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateTicketAsync(1, "{}"));
        Assert.Empty(upstream.TicketCalls);
    }

    [Theory]
    [InlineData("\"not a date\"")]
    [InlineData("12345")]
    [InlineData("null")]
    public async Task AMissingOrUnreadableExpiry_MeansUnknown_AndKeepsThePreviousTwentyMinuteCache(string expiresJson)
    {
        var upstream = new FakeTigerCs();
        upstream.RawLoginBody = "{\"accessToken\":\"T1\",\"expiresAtUtc\":" + expiresJson + "}";
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Host.Service(upstream, cache);

        (await service.UpdateTicketAsync(1, "{}")).Dispose();

        Assert.Single(upstream.TicketCalls);
        Assert.True(cache.TryGetValue("TigerCS_Genesys_AccessToken", out _));
    }

    // ------------------------------------------------------------------ existing behaviour that must not move

    [Fact]
    public async Task OtpAndDocumentPosts_StillForwardTheCallersIdempotencyKey_AndAreNeverReplayedAfterATimeout()
    {
        var upstream = new FakeTigerCs { Script = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); } };
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SendDocumentCopyAsync("{\"x\":1}", "key-123", CancellationToken.None));

        var call = Assert.Single(upstream.TicketCalls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/genesys/documents/send-copy", call.Path);
        Assert.Equal("key-123", call.IdempotencyKey);
    }

    [Fact]
    public async Task OtpPost_StillRefreshesOnAChallengeOnly()
    {
        var upstream = new FakeTigerCs { Script = (_, n, _) => Task.FromResult(n == 1 ? FakeTigerCs.Challenge() : new HttpResponseMessage(HttpStatusCode.OK)) };
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        using var response = await service.VerifyOtpAsync("{}", null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, upstream.TicketCalls.Count());
    }

    [Fact]
    public async Task ScreenPopAndLookup_KeepTheirOwnOneShot401Retry()
    {
        var upstream = new FakeTigerCs { Script = (_, n, _) => Task.FromResult(new HttpResponseMessage(n == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)) };
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        using var response = await service.ScreenPopAsync("{\"genesysUserId\":\"u\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, upstream.TicketCalls.Count());
    }

    [Fact]
    public async Task ACollectionsRead_StillCarriesItsDeadlineHeaderPath_AndTheServiceToken()
    {
        var upstream = new FakeTigerCs();
        var service = Host.Service(upstream, new MemoryCache(new MemoryCacheOptions()));

        using var response = await service.CollectionsReadAsync("reminders/candidates", "?pageSize=5", TimeSpan.FromSeconds(10), CancellationToken.None);

        var call = Assert.Single(upstream.TicketCalls);
        Assert.Equal("/api/genesys/collections/reminders/candidates", call.Path);
        Assert.Equal("Bearer T1", call.Authorization);
    }

    // ------------------------------------------------------------------ authorization and routes

    [Fact]
    public async Task WithoutAValidGenesysBearer_NothingIsForwarded()
    {
        await using var h = await Host.StartAsync();
        h.Client.DefaultRequestHeaders.Authorization = null;

        var create = await h.PostAsync("/api/genesys/tickets", "{}");
        var update = await h.PatchAsync("/api/genesys/tickets/1", "{}");

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
        Assert.Empty(h.Upstream.Calls);
    }

    [Fact]
    public async Task MalformedJson_IsRefusedLocally_AndNothingIsForwarded()
    {
        await using var h = await Host.StartAsync();

        var response = await h.PostAsync("/api/genesys/tickets", "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(h.Upstream.Calls);
    }

    [Fact]
    public void PublicRoutesAndTheAuthorizationPolicyAreUnchanged()
    {
        var type = typeof(GenesysController);
        Assert.Equal("api/genesys", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(GenesysAuthDefaults.PolicyName, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

        var create = type.GetMethod(nameof(GenesysController.CreateTicket))!;
        Assert.Equal("tickets", create.GetCustomAttribute<HttpPostAttribute>()!.Template);
        var update = type.GetMethod(nameof(GenesysController.UpdateTicket))!;
        Assert.Equal("tickets/{ticketId:long}", update.GetCustomAttribute<HttpPatchAttribute>()!.Template);

        // No duplicate route was introduced.
        var routes = type.GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>().Select(a => $"{a.HttpMethods.Single()} {a.Template}"))
            .ToList();
        Assert.Equal(routes.Count, routes.Distinct().Count());
        Assert.Single(routes, r => r == "POST tickets");
        Assert.Single(routes, r => r == "PATCH tickets/{ticketId:long}");
    }

    // ------------------------------------------------------------------ logging

    [Fact]
    public async Task Logs_NeverContainCustomerDataConversationTextTokensOrCredentials()
    {
        const string phone = "+971500000001";
        const string secretText = "My unit number is 1506 and my card ends 4242";
        var body = "{\"conversationId\":\"c-99\",\"customerPhone\":\"" + phone + "\",\"transcript\":[{\"sender\":\"Customer\",\"body\":\"" + secretText + "\"}]}";

        await using var ok = await Host.StartAsync();
        await ok.PostAsync("/api/genesys/tickets", body);
        await ok.PatchAsync("/api/genesys/tickets/7", body);

        await using var failing = await Host.StartAsync(configure: u => u.Script = (_, _, _) =>
            throw new HttpRequestException($"failed: Bearer T1 for {phone} {secretText}"));
        await failing.PatchAsync("/api/genesys/tickets/7", body);

        await using var slow = await Host.StartAsync(TimeSpan.FromMilliseconds(250), u => u.Script = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        await slow.PatchAsync("/api/genesys/tickets/7", body);

        foreach (var logs in new[] { ok.Logs, failing.Logs, slow.Logs })
        {
            var text = logs.All;
            Assert.NotEmpty(logs.Lines);
            Assert.DoesNotContain(phone, text);
            Assert.DoesNotContain(secretText, text);
            Assert.DoesNotContain("c-99", text);
            Assert.DoesNotContain("T1", text);
            Assert.DoesNotContain("genesys-ok", text);
            Assert.DoesNotContain("PASSWORD-MUST-NEVER-BE-LOGGED", text);
        }

        Assert.Contains("ticket update", failing.Logs.All);
        Assert.Contains("HttpRequestException", failing.Logs.All);   // the type, not the message
        Assert.Contains("timed out", slow.Logs.All);
        Assert.Contains("200", ok.Logs.All);
    }
}
