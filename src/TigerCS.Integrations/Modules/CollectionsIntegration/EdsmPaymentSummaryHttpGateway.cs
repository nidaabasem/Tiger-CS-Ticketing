using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Integrations.Modules.PactIntegration;

namespace TigerCS.Integrations.Modules.CollectionsIntegration;

/// <summary>
/// The real <see cref="IEdsmPaymentSummaryGateway"/>: PACT's
/// <c>GET v1/reports/payment-summary?CompanyId=&amp;TenantId=</c>, on the same
/// <c>PactApi</c> base URL and <c>X-API-KEY</c> as
/// <see cref="PactCustomerHttpGateway"/>, and with the same failure discipline:
/// every expected failure is an outcome, the key is never logged.
///
/// <para>
/// <b>Envelope.</b> The supplied <c>PaymentsSummaryOutputModel</c> is the
/// payload, not necessarily the whole body, and no real response has been
/// seen. PACT's <c>v1/contracts</c> wraps its payload in <c>data</c>, so a
/// <c>data</c> object is accepted, and so is the bare object. A <c>data</c>
/// array (several summaries — meaning unknown), a null <c>data</c>, or an
/// object with none of the five fields is <see cref="EdsmPaymentSummaryOutcome.InvalidResponse"/>.
/// Which shape was seen is returned in <see cref="EdsmPaymentSummaryResult.Envelope"/>.
/// </para>
///
/// <para>
/// <b>Naming.</b> Matched case-insensitively — the model is PascalCase and
/// ASP.NET Core emits camelCase by default; both bind. Each field must be a
/// JSON string, as the model declares; any other token is Unreadable.
/// </para>
/// </summary>
public sealed class EdsmPaymentSummaryHttpGateway(
    HttpClient httpClient,
    IOptions<PactApiOptions> pactOptions,
    IOptions<CollectionsSourceOptions> sourceOptions,
    ILogger<EdsmPaymentSummaryHttpGateway> logger) : IEdsmPaymentSummaryGateway
{
    private const string ApiKeyHeaderName = "X-API-KEY";

    public string SourceName => "Pact";

    public async Task<EdsmPaymentSummaryResult> GetPaymentSummaryAsync(int companyId, long tenantId, CancellationToken cancellationToken = default)
    {
        var apiKey = pactOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("PactApi:ApiKey is not configured — cannot call EDSM payment summary.");
            return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable, "PactApi:ApiKey is not configured.");
        }

        var path = string.Create(CultureInfo.InvariantCulture,
            $"v1/reports/payment-summary?CompanyId={companyId}&TenantId={tenantId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "EDSM payment summary timed out for company {CompanyId}, tenant {TenantId}.", companyId, tenantId);
            return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable, "EDSM payment summary timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // InvalidOperationException: no PactApi:BaseUrl, so a relative URI cannot be sent.
            logger.LogWarning(ex, "EDSM payment summary could not be reached for company {CompanyId}, tenant {TenantId}.", companyId, tenantId);
            return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable, "EDSM payment summary could not be reached.");
        }

        using (response)
        {
            logger.LogDebug("EDSM payment summary: GET {AbsoluteRequestUri} -> HTTP {StatusCode}.",
                response.RequestMessage?.RequestUri, (int)response.StatusCode);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return Parse(body, sourceOptions.Value.PaymentSummaryAmountFormat);
                case HttpStatusCode.NotFound:
                    return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.NotFound,
                        "EDSM has no payment summary for this company and tenant (or the route is wrong — see the Debug URL).");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    logger.LogWarning("EDSM payment summary returned {StatusCode} — check PactApi:ApiKey.", (int)response.StatusCode);
                    return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unauthorized, "PACT rejected the configured API key.");
                case HttpStatusCode.BadRequest:
                    logger.LogWarning("EDSM payment summary returned 400 for company {CompanyId}, tenant {TenantId}.", companyId, tenantId);
                    return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.InvalidResponse, "EDSM rejected the request (400).");
                default:
                    logger.LogWarning("EDSM payment summary returned unexpected status {StatusCode}.", (int)response.StatusCode);
                    return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable,
                        $"EDSM payment summary returned unexpected status {(int)response.StatusCode}.");
            }
        }
    }

    private static readonly string[] FieldNames = ["TotalAmount", "PaidAmount", "DueAmount", "OutstandingAmount", "LateFines"];

    /// <summary>Body → result. Public so the envelope and field rules are tested without HTTP.</summary>
    public static EdsmPaymentSummaryResult Parse(string body, EdsmAmountFormat format)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.InvalidResponse, "EDSM returned a body that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.InvalidResponse,
                    $"EDSM returned a JSON {root.ValueKind}, not a payment summary object.");
            }

            JsonElement payload;
            string envelope;
            if (TryGetProperty(root, "data", out var data))
            {
                if (data.ValueKind != JsonValueKind.Object)
                {
                    return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.InvalidResponse,
                        $"EDSM's 'data' is a JSON {data.ValueKind}; only a single summary object is understood.");
                }

                payload = data;
                envelope = "data";
            }
            else
            {
                payload = root;
                envelope = "bare";
            }

            if (!FieldNames.Any(name => TryGetProperty(payload, name, out _)))
            {
                return EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.InvalidResponse,
                    "EDSM's response has none of the payment-summary fields.");
            }

            return EdsmPaymentSummaryResult.Success(new EdsmPaymentSummary(
                Field(payload, "TotalAmount", format),
                Field(payload, "PaidAmount", format),
                Field(payload, "DueAmount", format),
                Field(payload, "OutstandingAmount", format),
                Field(payload, "LateFines", format)), envelope);
        }
    }

    private static EdsmAmount Field(JsonElement payload, string name, EdsmAmountFormat format)
    {
        if (!TryGetProperty(payload, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return EdsmAmount.Missing;
        }

        return value.ValueKind == JsonValueKind.String
            ? EdsmAmountParser.Parse(value.GetString(), format)
            : new EdsmAmount(EdsmAmountStatus.Unreadable, null, value.GetRawText());
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>Every real environment until <c>CollectionsSource:PaymentSummaryProvider</c> is set to "Pact".</summary>
public sealed class UnavailableEdsmPaymentSummaryGateway : IEdsmPaymentSummaryGateway
{
    public string SourceName => "Unavailable";

    public Task<EdsmPaymentSummaryResult> GetPaymentSummaryAsync(int companyId, long tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable,
            "The EDSM payment summary is not enabled in this environment (CollectionsSource:PaymentSummaryProvider)."));
}

/// <summary>
/// Development/Testing only (refused elsewhere by <see cref="CollectionsSourceSafety"/>).
/// Goes through <see cref="EdsmPaymentSummaryHttpGateway.Parse"/> so fixture
/// bodies exercise the same envelope and amount rules as real ones. Not EDSM data.
/// </summary>
public sealed class FixtureEdsmPaymentSummaryGateway(IOptions<CollectionsSourceOptions> sourceOptions) : IEdsmPaymentSummaryGateway
{
    /// <summary>(companyId, tenantId) → body. Tenant 3001 is MockPactGateway's fixture customer.</summary>
    private static readonly IReadOnlyDictionary<(int, long), string> Bodies = new Dictionary<(int, long), string>
    {
        [(1, 3001)] = """{"data":{"totalAmount":"1250000.00","paidAmount":"812500.00","dueAmount":"62500.00","outstandingAmount":"437500.00","lateFines":"1500.00"}}""",
        [(2, 3001)] = """{"data":{"totalAmount":"90000.00","paidAmount":"","dueAmount":"1,500.00","outstandingAmount":null,"lateFines":"0.00"}}"""
    };

    public string SourceName => "Fixture";

    public Task<EdsmPaymentSummaryResult> GetPaymentSummaryAsync(int companyId, long tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Bodies.TryGetValue((companyId, tenantId), out var body)
            ? EdsmPaymentSummaryHttpGateway.Parse(body, sourceOptions.Value.PaymentSummaryAmountFormat)
            : EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.NotFound, "No fixture summary for this company and tenant."));
}
