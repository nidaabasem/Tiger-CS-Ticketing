using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Real HTTP-backed <see cref="IPactCustomerLookupGateway"/> — calls PACT's
/// <c>GET v1/contracts/{mobile}</c> for the customer's contracts/units and,
/// only when that response carries no customer type,
/// <c>GET v1/contracts/{mobile}/customer-type</c> for it. Registered as a
/// typed <see cref="HttpClient"/> (<c>IntegrationsServiceCollectionExtensions</c>)
/// with its base address bound from <see cref="PactApiOptions.BaseUrl"/> —
/// the same shape as <c>CrmBuyerHttpGateway</c>, deliberately.
///
/// <para>
/// <b>Every failure mode maps to a <see cref="PactCustomerLookupResult"/>
/// outcome — this gateway never throws for an expected PACT response.</b>
/// Timeouts, DNS/connection failures, an unconfigured base address or API
/// key, a 400/401/403/5xx status, and a 200 body that doesn't parse as the
/// documented contract all collapse to a non-Success outcome rather than an
/// unhandled exception — PACT lookup must never crash the caller, and its
/// caller (<c>CustomerLookupAppService</c>) reports any non-Success outcome
/// as a Failed/NotFound source without ever blocking New Ticket creation.
/// A failure of the secondary customer-type call is even softer: the lookup
/// still succeeds, with <c>CustomerType</c> left null.
/// </para>
///
/// <para>
/// <b>The X-API-KEY value is never logged.</b> Same discipline as
/// <c>CrmBuyerHttpGateway</c>'s X-SECRET-KEY: log lines name the failure and
/// the masked mobile number only (Security-Architecture.md §11's PII rule),
/// and the key never appears in a format string or an exception message this
/// gateway constructs.
/// </para>
///
/// <para>
/// <b>The mobile number is sent exactly as the agent entered it</b> —
/// trimmed and percent-encoded into the path, nothing more. This reuses the
/// codebase's existing phone-handling convention (see
/// <c>IIntakeRecordRepository</c>'s remarks: no phone-normalization
/// convention exists to apply, and <c>IntakeRecord.PhoneNumber</c> is
/// preserved verbatim), exactly as <c>CrmBuyerHttpGateway</c> does for CRM.
/// </para>
/// </summary>
public sealed class PactCustomerHttpGateway(
    HttpClient httpClient, IOptions<PactApiOptions> options, ILogger<PactCustomerHttpGateway> logger)
    : IPactCustomerLookupGateway
{
    private const string ApiKeyHeaderName = "X-API-KEY";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mobileNumber);
        mobileNumber = mobileNumber.Trim();

        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError(
                "PactApi:ApiKey is not configured — cannot call PACT contracts lookup for {MaskedMobileNumber}. "
                + "See docs/DEV-SETUP.md for how to configure it via user-secrets/environment variable.",
                Mask(mobileNumber));
            return PactCustomerLookupResult.Unavailable("PactApi:ApiKey is not configured.");
        }

        using var request = CreateRequest($"v1/contracts/{Uri.EscapeDataString(mobileNumber)}", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed — distinct from the caller's own
            // cancellationToken being cancelled, which is left to propagate.
            logger.LogWarning(ex, "PACT contracts lookup timed out for {MaskedMobileNumber}.", Mask(mobileNumber));
            return PactCustomerLookupResult.Unavailable("PACT request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // InvalidOperationException covers an unconfigured PactApi:BaseUrl
            // (typed HttpClient with no BaseAddress rejects a relative URI).
            logger.LogWarning(ex, "PACT contracts lookup could not be reached for {MaskedMobileNumber}.", Mask(mobileNumber));
            return PactCustomerLookupResult.Unavailable("PACT could not be reached.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ParseSuccessResponseAsync(response, mobileNumber, apiKey, cancellationToken);
                case HttpStatusCode.NotFound:
                    // PACT answered and simply has no customer on file — a
                    // data-not-found result, never an error.
                    return PactCustomerLookupResult.NotFound();
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    logger.LogWarning(
                        "PACT contracts lookup returned {StatusCode} — check PactApi:ApiKey.", (int)response.StatusCode);
                    return PactCustomerLookupResult.Unauthorized("PACT rejected the configured API key.");
                case HttpStatusCode.BadRequest:
                    logger.LogWarning(
                        "PACT contracts lookup returned 400 Bad Request for {MaskedMobileNumber}.", Mask(mobileNumber));
                    return PactCustomerLookupResult.InvalidResponse("PACT rejected the request (400).");
                default:
                    logger.LogWarning(
                        "PACT contracts lookup returned unexpected status {StatusCode} for {MaskedMobileNumber}.",
                        (int)response.StatusCode, Mask(mobileNumber));
                    return PactCustomerLookupResult.Unavailable($"PACT returned unexpected status {(int)response.StatusCode}.");
            }
        }
    }

    private async Task<PactCustomerLookupResult> ParseSuccessResponseAsync(
        HttpResponseMessage response, string mobileNumber, string apiKey, CancellationToken cancellationToken)
    {
        PactContractsHttpResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<PactContractsHttpResponse>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException covers a non-JSON content type (e.g. an
            // HTML error page instead of a JSON body).
            logger.LogWarning(ex, "PACT contracts lookup returned a malformed response for {MaskedMobileNumber}.", Mask(mobileNumber));
            return PactCustomerLookupResult.InvalidResponse("PACT returned a malformed response body.");
        }

        if (payload is null)
        {
            return PactCustomerLookupResult.InvalidResponse("PACT returned an empty response body.");
        }

        var contracts = MapContracts(payload.Contracts);
        var tenantId = FirstNonBlank(payload.TenantId, payload.Contracts?.Select(c => c.TenantId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)));
        if (tenantId is null && string.IsNullOrWhiteSpace(payload.TenantName) && contracts.Count == 0)
        {
            // A 200 that carries no customer identity and no contracts is
            // PACT's way of answering "nothing on file" with an empty body.
            return PactCustomerLookupResult.NotFound();
        }

        // "Retrieve customer type when required": only when the contracts
        // response didn't already carry one. Its failure never degrades the
        // main result — the type just stays null.
        var customerType = FirstNonBlank(payload.CustomerType)
            ?? await TryGetCustomerTypeAsync(mobileNumber, apiKey, cancellationToken);

        var match = new PactCustomerMatchDto(
            // The mobile number itself is the identifier PACT was searched
            // by — used only when PACT sent no tenant id of its own, so a
            // match is never dropped for lacking one.
            tenantId ?? mobileNumber,
            FirstNonBlank(payload.TenantName),
            FirstNonBlank(payload.Mobile) ?? mobileNumber,
            FirstNonBlank(payload.Email),
            customerType,
            contracts);
        return PactCustomerLookupResult.Success([match]);
    }

    private static IReadOnlyList<PactContractDto> MapContracts(List<PactContractHttpDto>? contracts) =>
        contracts is null
            ? []
            : contracts
                .Select(contract => new
                {
                    Contract = contract,
                    ExternalUnitId = FirstNonBlank(contract.UnitCode, contract.ContractNumber, contract.UnitNumber)
                })
                // A row with no unit code, contract number, or unit number
                // identifies nothing — dropped rather than fabricating an id.
                .Where(row => row.ExternalUnitId is not null)
                .Select(row => new PactContractDto(
                    row.ExternalUnitId!,
                    FirstNonBlank(row.Contract.ContractNumber),
                    FirstNonBlank(row.Contract.UnitNumber),
                    FirstNonBlank(row.Contract.ProjectName),
                    FirstNonBlank(row.Contract.UnitType)))
                .ToList();

    /// <summary>
    /// Best-effort secondary call — any failure (unreachable, non-200,
    /// malformed body) is logged and swallowed so the already-successful
    /// contracts lookup is never degraded by it.
    /// </summary>
    private async Task<string?> TryGetCustomerTypeAsync(string mobileNumber, string apiKey, CancellationToken cancellationToken)
    {
        using var request = CreateRequest($"v1/contracts/{Uri.EscapeDataString(mobileNumber)}/customer-type", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning(
                    "PACT customer-type lookup returned status {StatusCode} for {MaskedMobileNumber} — customer type left empty.",
                    (int)response.StatusCode, Mask(mobileNumber));
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<PactCustomerTypeHttpResponse>(JsonOptions, cancellationToken);
            return FirstNonBlank(payload?.CustomerType);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or JsonException or NotSupportedException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                ex, "PACT customer-type lookup failed for {MaskedMobileNumber} — customer type left empty.", Mask(mobileNumber));
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(string endpoint, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
        return request;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    /// <summary>Security-Architecture.md §11's masking discipline, applied to a mobile number for log lines — enough to correlate, never enough to identify.</summary>
    private static string Mask(string mobileNumber) =>
        mobileNumber.Length <= 4 ? "***" : $"***{mobileNumber[^4..]}";
}
