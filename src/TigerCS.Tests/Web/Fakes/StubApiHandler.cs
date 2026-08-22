using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TigerCS.Tests.Web.Fakes;

/// <summary>
/// Stands in for TigerCS.Api so a web-tier test can drive an exact status code
/// and body.
///
/// <para>
/// This exists alongside - not instead of - the real-API harness. The real API
/// is the right thing to test authorization and the ticket workflow against,
/// and <see cref="TigerCsWebWithApiFactory"/> does exactly that. But the API
/// cannot be made to answer <c>502</c> on demand, and reproducing a genuine
/// <c>409</c> concurrency race or a <c>410</c> expired session end-to-end is
/// slow and flaky. Those are properties of <b>this</b> tier's error handling,
/// so they are driven directly.
/// </para>
///
/// <para>
/// Every response shape below is copied from the API's own DTO records, which
/// this project references - so a DTO change breaks these tests at compile
/// time rather than letting them assert against a stale shape.
/// </para>
/// </summary>
public sealed class StubApiHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly List<Route> _routes = [];

    /// <summary>Every request this handler saw, in order.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>One outbound call, as the API would have received it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The absolute path.</param>
    /// <param name="Query">The query string, including the leading question mark.</param>
    /// <param name="Body">The request body, when there was one.</param>
    /// <param name="Authorization">The Authorization header value, when one was sent.</param>
    /// <param name="CorrelationId">The X-Correlation-ID header value, when one was sent.</param>
    public sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string? Query,
        string? Body,
        string? Authorization,
        string? CorrelationId);

    /// <summary>Registers a response for the first request matching method and path prefix.</summary>
    public StubApiHandler On(HttpMethod method, string pathPrefix, HttpStatusCode status, object? body = null)
    {
        _routes.Add(new Route(method, pathPrefix, status, body, Once: false));
        return this;
    }

    /// <summary>
    /// Registers a response consumed by a single matching request.
    ///
    /// <para>
    /// A one-shot route wins over a standing one for the same method and path,
    /// so a test can set up a general fixture and then override one call
    /// without having to rebuild the fixture around it.
    /// </para>
    /// </summary>
    public StubApiHandler Once(HttpMethod method, string pathPrefix, HttpStatusCode status, object? body = null)
    {
        _routes.Add(new Route(method, pathPrefix, status, body, Once: true));
        return this;
    }

    /// <summary>Registers an RFC 7807 problem response with the given problem-type slug.</summary>
    public StubApiHandler OnProblem(
        HttpMethod method, string pathPrefix, HttpStatusCode status, string problemSlug, string title)
    {
        return On(method, pathPrefix, status, new
        {
            type = $"https://tigercs.internal/problems/{problemSlug}",
            title,
            status = (int)status
        });
    }

    /// <summary>How many requests were made to a path prefix.</summary>
    public int CountFor(HttpMethod method, string pathPrefix) =>
        Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new CapturedRequest(
            request.Method,
            path,
            query,
            body,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("X-Correlation-ID", out var correlation)
                ? correlation.FirstOrDefault()
                : null));

        bool Matches(Route r) =>
            r.Method == request.Method
            && path.StartsWith(r.PathPrefix, StringComparison.Ordinal)
            && !r.Consumed;

        // One-shot routes first, so an override registered after a standing
        // route still takes effect.
        var route = _routes.FirstOrDefault(r => r.Once && Matches(r))
            ?? _routes.FirstOrDefault(Matches);

        if (route is null)
        {
            // An unregistered call is a test defect, and a silent 404 would
            // make it look like an application behaviour instead.
            return new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent(
                    $"StubApiHandler has no route for {request.Method} {path}", Encoding.UTF8, "text/plain")
            };
        }

        if (route.Once)
        {
            route.Consumed = true;
        }

        var response = new HttpResponseMessage(route.Status);
        if (route.Body is not null)
        {
            response.Content = JsonContent.Create(route.Body, route.Body.GetType(), options: SerializerOptions);
        }

        return response;
    }

    private sealed record Route(
        HttpMethod Method, string PathPrefix, HttpStatusCode Status, object? Body, bool Once)
    {
        public bool Consumed { get; set; }
    }
}
