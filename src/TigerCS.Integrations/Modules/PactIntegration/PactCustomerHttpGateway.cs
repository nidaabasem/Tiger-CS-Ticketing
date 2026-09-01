using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Real HTTP-backed <see cref="IPactCustomerLookupGateway"/> — calls PACT's
/// <c>GET v1/contracts/{mobile}</c>, whose real body is a flat <c>data</c>
/// array of per-contract rows (see <see cref="PactContractsHttpResponse"/>'s
/// remarks): rows are grouped by <c>tenantID</c> so one customer match
/// carries ALL of that tenant's contracts/units — never just the first row,
/// and never an auto-selected one. The contracts response's own
/// <c>customerBuyerType</c> is the authoritative customer type;
/// <c>GET v1/contracts/{mobile}/customer-type</c> is called only as a
/// fallback when every row for a tenant came back with it null/absent (the
/// integration spec requires the customer type to accompany a match when
/// PACT can supply one). Registered as a
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
/// <b>The mobile number is normalized for PACT only</b> — trimmed, with
/// every '+' removed, before it goes into either request path
/// (<see cref="NormalizePactPhone"/>): PACT does not expect the '+' prefix,
/// and sending "+971501234567" instead of "971501234567" makes an existing
/// customer come back not-found. This normalization lives here, at the PACT
/// integration boundary, and nowhere else: the caller's value is untouched
/// (<c>IntakeRecord.PhoneNumber</c> stays verbatim per
/// <c>IIntakeRecordRepository</c>'s remarks), and <c>CrmBuyerHttpGateway</c>
/// keeps sending CRM the number exactly as entered.
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
        mobileNumber = NormalizePactPhone(mobileNumber);
        if (mobileNumber.Length == 0)
        {
            // The input was only '+'/whitespace — nothing searchable remains,
            // and an empty path segment would call a different PACT route.
            return PactCustomerLookupResult.NotFound("Mobile number contained no searchable characters after PACT normalization.");
        }

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
            // Debug-level diagnostic: the ABSOLUTE request URL (revealing
            // BaseUrl resolution problems — e.g. a path prefix silently
            // dropped by relative-URI resolution) and the raw status. The
            // URL contains the full normalized mobile number on purpose:
            // diagnosing a PACT number-format mismatch requires seeing the
            // exact value sent, and Debug is off at the default production
            // log level — every Warning+ line above/below still masks.
            logger.LogDebug(
                "PACT contracts lookup: GET {AbsoluteRequestUri} -> HTTP {StatusCode}.",
                response.RequestMessage?.RequestUri, (int)response.StatusCode);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ParseSuccessResponseAsync(response, mobileNumber, apiKey, cancellationToken);
                case HttpStatusCode.NotFound:
                    // PACT answered and simply has no customer on file — a
                    // data-not-found result, never an error. NOTE for
                    // diagnosis: a wrong PactApi:BaseUrl (unknown route)
                    // also produces a 404 and lands here — the Debug line
                    // above shows which of the two it is via the absolute URL.
                    logger.LogDebug(
                        "PACT contracts lookup answered 404 for {MaskedMobileNumber} — no customer on file, OR an unknown route (check the absolute URL in the previous Debug line).",
                        Mask(mobileNumber));
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

        if (payload?.Data is null)
        {
            return PactCustomerLookupResult.InvalidResponse(
                "PACT returned a body without the documented 'data' array.");
        }

        if (payload.Data.Count == 0)
        {
            // An empty data array is PACT answering "nothing on file" — a
            // data-not-found result, never an error. Distinct from the 404
            // case in the Debug trail: 200-with-empty-data means the route
            // was right and PACT genuinely matched no customer for the
            // number AS SENT — the primary signal of a number-format
            // mismatch when the customer is known to exist.
            logger.LogDebug(
                "PACT contracts lookup answered 200 with an EMPTY data array for {MaskedMobileNumber} — the route resolved but no customer matched the number as sent.",
                Mask(mobileNumber));
            return PactCustomerLookupResult.NotFound();
        }

        logger.LogDebug(
            "PACT contracts lookup answered 200 with {RowCount} contract row(s) for {MaskedMobileNumber}.",
            payload.Data.Count, Mask(mobileNumber));

        // The real body is one flat row per contract; the same tenant appears
        // once per contract. Group by tenantID — the primary external PACT
        // customer/tenant identifier — so each customer match carries ALL of
        // that tenant's contracts/units and nothing is ever auto-selected.
        // A row PACT sent without a tenantID falls back to the searched
        // mobile number as the group key rather than being dropped.
        var matches = payload.Data
            .GroupBy(row => row.TenantID?.ToString(CultureInfo.InvariantCulture) ?? mobileNumber)
            .Select(tenantRows => new PactCustomerMatchDto(
                tenantRows.Key,
                FirstNonBlank(tenantRows.Select(row => row.CustomerName).ToArray()),
                FirstNonBlank(tenantRows.Select(row => row.CustomerMobile).ToArray()) ?? mobileNumber,
                FirstNonBlank(tenantRows.Select(row => row.CustomerEmail).ToArray()),
                tenantRows
                    .Select(row => row.CustomerBuyerType)
                    .FirstOrDefault(buyerType => buyerType is not null)
                    ?.ToString(CultureInfo.InvariantCulture),
                MapContracts(tenantRows)))
            .ToList();

        // customerBuyerType on the contracts response is authoritative. The
        // customer-type endpoint is a fallback only — called once, and only
        // when a tenant's rows all lacked it (the integration spec requires
        // the customer type to accompany a match when PACT can supply one).
        // Its failure never degrades the main result — the type stays null.
        if (matches.Any(match => match.CustomerType is null)
            && await TryGetCustomerTypeAsync(mobileNumber, apiKey, cancellationToken) is { } fallbackType)
        {
            matches = matches
                .Select(match => match.CustomerType is null ? match with { CustomerType = fallbackType } : match)
                .ToList();
        }

        return PactCustomerLookupResult.Success(matches);
    }

    private static IReadOnlyList<PactContractDto> MapContracts(IEnumerable<PactContractRowHttpDto> rows) =>
        rows
            .Select(row => new
            {
                Row = row,
                // unitID is PACT's own identifier for the unit — the primary
                // ExternalUnitId. unitCode ("104-2304") and unitNumber are
                // display codes, used only as fallbacks for a row PACT sent
                // without a unitID. contractID is deliberately NOT in this
                // chain: it identifies the contract (carried as
                // ContractNumber below), never the unit.
                ExternalUnitId = FirstNonBlank(
                    row.UnitID?.ToString(CultureInfo.InvariantCulture),
                    row.UnitCode,
                    row.UnitNumber)
            })
            // A row with no unit id, unit code, or unit number identifies no
            // unit — dropped rather than fabricating an id from its contract.
            .Where(row => row.ExternalUnitId is not null)
            .Select(row => new PactContractDto(
                row.ExternalUnitId!,
                row.Row.ContractID?.ToString(CultureInfo.InvariantCulture),
                FirstNonBlank(row.Row.UnitNumber),
                FirstNonBlank(row.Row.ProjectName),
                FirstNonBlank(row.Row.UnitType)))
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
            // Same Debug-level absolute-URL diagnostic as the contracts call.
            logger.LogDebug(
                "PACT customer-type fallback: GET {AbsoluteRequestUri} -> HTTP {StatusCode}.",
                response.RequestMessage?.RequestUri, (int)response.StatusCode);
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

    /// <summary>
    /// PACT-only phone normalization: trim surrounding whitespace and remove
    /// every '+' — PACT stores/matches numbers without the '+' prefix, so
    /// "+971501234567" must reach it as "971501234567" or an existing
    /// customer returns not-found. Applied to nothing but the two PACT
    /// request paths this gateway builds; the number the caller holds (and
    /// everything CRM/Tasleeh/persistence see) stays exactly as entered.
    /// </summary>
    private static string NormalizePactPhone(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return string.Empty;
        }

        return phoneNumber
            .Trim()
            .Replace("+", "");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    /// <summary>Security-Architecture.md §11's masking discipline, applied to a mobile number for log lines — enough to correlate, never enough to identify.</summary>
    private static string Mask(string mobileNumber) =>
        mobileNumber.Length <= 4 ? "***" : $"***{mobileNumber[^4..]}";
}
