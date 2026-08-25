using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TigerCS.Tests.Web.Fakes;

/// <summary>One request captured by <see cref="FakeApiHandler"/>, for assertions on exactly what a Web page sent TigerCS.Api.</summary>
public sealed record RecordedRequest(HttpMethod Method, string RequestUri, string? Body);

/// <summary>
/// Stands in for TigerCS.Api inside a TigerCS.Web typed <see cref="HttpClient"/> under test — the same
/// role the app's own <c>Fakes/</c> repositories play for app-service tests elsewhere in this project,
/// just at the HTTP boundary instead of the repository boundary.
/// </summary>
public sealed class FakeApiHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body, options: JsonOptions) };
}
