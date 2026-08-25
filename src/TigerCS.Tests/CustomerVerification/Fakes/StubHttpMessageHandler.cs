namespace TigerCS.Tests.CustomerVerification.Fakes;

/// <summary>
/// A programmable <see cref="HttpMessageHandler"/> stub for typed-HttpClient
/// gateway tests — no mocking library is referenced anywhere in this test
/// project (see TigerCS.Tests.csproj), so HTTP-level tests construct the
/// <see cref="HttpClient"/> directly over this handler rather than going
/// through DI/HttpClientFactory.
/// </summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    /// <summary>The most recent request this handler received — for asserting headers/URI without a separate capture mechanism.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        CallCount++;
        return await responder(request, cancellationToken);
    }
}
