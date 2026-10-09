using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The real Genesys HTTP client and token provider against a scripted handler. No network, no real credentials.</summary>
public sealed class GenesysHttpTests
{
    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-secret-value-DO-NOT-LOG";
    private static readonly DateTime Start = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private const string ListId = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
        public int TokenSeq;
        public int TokenLifetime = 1800;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"token-{++TokenSeq}\",\"token_type\":\"bearer\",\"expires_in\":{TokenLifetime}}}");
            return Responses.Count > 0 ? Responses.Dequeue()(request) : Json(HttpStatusCode.OK, "[{\"id\":\"c-1\"},{\"id\":\"c-2\"}]");
        }

        public IEnumerable<(HttpRequestMessage Request, string Body)> Token => Requests.Where(r => r.Request.RequestUri!.AbsolutePath == "/oauth/token");
        public IEnumerable<(HttpRequestMessage Request, string Body)> Upload => Requests.Where(r => r.Request.RequestUri!.AbsolutePath != "/oauth/token");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception?.ToString());
    }

    private static GenesysOutboundOptions Options() => new() { Enabled = true, ClientId = ClientId, ClientSecret = ClientSecret };

    private static (GenesysOutboundHttpClient Client, GenesysTokenProvider Tokens, Handler Handler, FakeTimeProvider Time, CapturingLogger<GenesysTokenProvider> TokenLog, CapturingLogger<GenesysOutboundHttpClient> ClientLog)
        Build(GenesysOutboundOptions? options = null)
    {
        options ??= Options();
        var handler = new Handler();
        var time = new FakeTimeProvider(Start);
        var tokenLog = new CapturingLogger<GenesysTokenProvider>();
        var clientLog = new CapturingLogger<GenesysOutboundHttpClient>();
        var tokens = new GenesysTokenProvider(new HttpClient(handler), options, time, tokenLog);
        return (new GenesysOutboundHttpClient(new HttpClient(handler), tokens, options, clientLog), tokens, handler, time, tokenLog, clientLog);
    }

    private static GenesysContactPayload Contact(int n = 1, string listId = ListId) => new(listId, $"+97150000{n:D4}", $"Customer {n}", $"c{n}@example.test",
        "Current Month", "1234.50", "2026-10-20", true);

    // ------------------------------------------------------------ token

    [Fact]
    public async Task TokenRequest_IsServerSideBasicAuthWithClientCredentialsGrant()
    {
        var (_, tokens, handler, _, _, _) = Build();
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));
        var (request, body) = Assert.Single(handler.Token);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://login.mypurecloud.de/oauth/token", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal($"{ClientId}:{ClientSecret}", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("grant_type=client_credentials", body);
    }

    [Fact]
    public async Task TokenIsCachedUntilItsExpiry_ThenRenewed()
    {
        var (_, tokens, handler, time, _, _) = Build();
        handler.TokenLifetime = 600;
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));          // still valid: no second request
        Assert.Single(handler.Token);
        time.Advance(TimeSpan.FromSeconds(300));                                               // 600 s elapsed; the 60 s safety margin has passed
        Assert.Equal("token-2", await tokens.GetTokenAsync(CancellationToken.None));
        Assert.Equal(2, handler.Token.Count());
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneTokenRequest()
    {
        var (_, tokens, handler, _, _, _) = Build();
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tokens.GetTokenAsync(CancellationToken.None)));
        Assert.All(all, t => Assert.Equal("token-1", t));
        Assert.Single(handler.Token);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("id", " ")]
    public async Task MissingCredentials_FailWithoutAnyNetworkCall(string? id, string? secret)
    {
        var (client, _, handler, _, _, _) = Build(new GenesysOutboundOptions { Enabled = true, ClientId = id, ClientSecret = secret });
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SecretsAndTokensNeverAppearInLogsOrErrors()
    {
        var (client, _, handler, _, tokenLog, clientLog) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom token-1\"}"));
        var failed = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        var (badClient, _, badHandler, _, badTokenLog, _) = Build();
        badHandler.TokenSeq = 0;
        var rejected = new GenesysOutboundHttpClient(new HttpClient(new RefusingTokenHandler()),
            new GenesysTokenProvider(new HttpClient(new RefusingTokenHandler()), Options(), TimeProvider.System, badTokenLog), Options(), NullLogger<GenesysOutboundHttpClient>.Instance);
        var refused = await rejected.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);

        var everything = string.Join('\n', tokenLog.Lines.Concat(clientLog.Lines).Concat(badTokenLog.Lines)) + failed.Error + refused.Error;
        Assert.DoesNotContain(ClientSecret, everything);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")), everything);
        Assert.DoesNotContain("token-1", everything);
        Assert.Equal(GenesysUploadOutcome.Rejected, refused.Outcome);
    }

    private sealed class RefusingTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    // ------------------------------------------------------------ request shape

    [Fact]
    public async Task UploadRequest_HasTheExactUrlAndBodyShape()
    {
        var (client, _, handler, _, _, _) = Build();
        var result = await client.UploadContactsAsync(ListId, [Contact(1), Contact(2)], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Equal(["c-1", "c-2"], result.ContactIds);

        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.mypurecloud.de/api/v2/outbound/contactlists/{ListId}/contacts", request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "token-1"), request.Headers.Authorization);

        using var json = JsonDocument.Parse(body);
        var contacts = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            Assert.Equal(["contactListId", "data", "callable"], contact.EnumerateObject().Select(p => p.Name));
            Assert.Equal(ListId, contact.GetProperty("contactListId").GetString());           // body id == URL id
            Assert.True(contact.GetProperty("callable").GetBoolean());
            var data = contact.GetProperty("data");
            Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], data.EnumerateObject().Select(p => p.Name));
            Assert.All(data.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));   // every value is a string
        }
        var first = contacts[0].GetProperty("data");
        Assert.Equal("+971500000001", first.GetProperty("Phone").GetString());
        Assert.Equal("Customer 1", first.GetProperty("CustomerName").GetString());
        Assert.Equal("c1@example.test", first.GetProperty("Email Address").GetString());
        Assert.Equal("Current Month", first.GetProperty("ReminderType").GetString());
        Assert.Equal("1234.50", first.GetProperty("AmountDue").GetString());
        Assert.Equal("2026-10-20", first.GetProperty("DueDate").GetString());
    }

    [Fact]
    public async Task ABodyListIdThatDiffersFromTheUrlIsRefusedBeforeAnyRequest()
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UploadContactsAsync(ListId, [Contact(1), Contact(2, "3a91c06e-47ab-4a5e-a720-004bd5cf5bba")], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task MoreThan1000OrZeroContactsIsNeverSent(int count)
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.UploadContactsAsync(ListId, Enumerable.Range(1, count).Select(n => Contact(n)).ToList(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Exactly1000ContactsIsAllowed()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(1, 1000).Select(n => $"{{\"id\":\"c{n}\"}}")) + "]"));
        var result = await client.UploadContactsAsync(ListId, Enumerable.Range(1, 1000).Select(n => Contact(n)).ToList(), CancellationToken.None);
        Assert.Equal(1000, result.ContactIds.Count);
    }

    // ------------------------------------------------------------ outcomes

    [Fact]
    public async Task ARejectedToken_IsRefreshedOnce_AndTheRequestRetriedSafely()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Equal(2, handler.Token.Count());
        Assert.Equal(["token-1", "token-2"], handler.Upload.Select(r => r.Request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task ASecond401_IsADefiniteRejection()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(2, handler.Upload.Count());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ClientErrors_AreDefiniteRejections_AndNeverEchoTheResponseBody(HttpStatusCode status)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(status, "{\"message\":\"Customer 1 +971500000001 is invalid\"}"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Equal((int)status, result.HttpStatus);
        Assert.DoesNotContain("+971500000001", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrors_AreAnUnknownOutcome_NotAFailure(HttpStatusCode status)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(status));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Unknown, result.Outcome);
        Assert.Single(handler.Upload);                                   // and it was not retried
    }

    [Fact]
    public async Task ATimeoutAfterSending_IsUnknown_AndNotRetried()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Unknown, result.Outcome);
        Assert.Single(handler.Upload);
    }

    [Fact]
    public async Task AConnectionFailureBeforeSending_IsAKnownNonDelivery()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task AConnectionDroppedMidRequest_IsUnknown()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset"));
        Assert.Equal(GenesysUploadOutcome.Unknown, (await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task AnUnreadableSuccessBody_StillCountsAsAccepted_WithoutIds()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "not json"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Empty(result.ContactIds);          // the dispatch service then marks the unmatched contacts as unconfirmed
    }
}
