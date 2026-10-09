using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Real HTTP-backed <see cref="ICrmUnitDetailsGateway"/> — calls Tiger CRM's
/// <c>GET /TicketingSystem/GetUnitDetails</c> (see
/// <see cref="CrmUnitDetailsHttpResponse"/> for the status of that contract)
/// with the same base address and <c>X-SECRET-KEY</c> as
/// <see cref="CrmBuyerHttpGateway"/>.
///
/// <para>
/// Never throws for an expected answer. <c>404</c> and <c>found:false</c> map
/// to <see cref="CrmUnitDetailsOutcome.NotAvailable"/> — which is also what an
/// environment whose CRM has not deployed the route yet answers, so shipping
/// this ahead of the CRM change degrades to nulls, not an outage. Timeouts,
/// 401, 400, 5xx and unreadable bodies are
/// <see cref="CrmUnitDetailsOutcome.Unavailable"/>. The secret is never logged.
/// </para>
/// </summary>
public sealed class CrmUnitDetailsHttpGateway(
    HttpClient httpClient, IOptions<CrmGatewayOptions> options, ILogger<CrmUnitDetailsHttpGateway> logger)
    : ICrmUnitDetailsGateway
{
    private const string SecretHeaderName = "X-SECRET-KEY";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CrmUnitDetailsResult> GetUnitDetailsAsync(
        int crmCustomerId, int crmUnitId, int? crmLeadId = null, bool includeSale = false, CancellationToken cancellationToken = default)
    {
        var secretKey = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            logger.LogError("Crm:SecretKey is not configured — cannot call CRM GetUnitDetails for unit {UnitId}.", crmUnitId);
            return CrmUnitDetailsResult.Unavailable();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"TicketingSystem/GetUnitDetails?customerId={crmCustomerId.ToString(CultureInfo.InvariantCulture)}"
            + $"&unitId={crmUnitId.ToString(CultureInfo.InvariantCulture)}"
            + (crmLeadId is { } lead ? $"&leadId={lead.ToString(CultureInfo.InvariantCulture)}" : string.Empty)
            + (includeSale ? "&includeSale=true" : string.Empty));
        request.Headers.TryAddWithoutValidation(SecretHeaderName, secretKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "CRM GetUnitDetails timed out for unit {UnitId}.", crmUnitId);
            return CrmUnitDetailsResult.Unavailable();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CRM GetUnitDetails could not be reached for unit {UnitId}.", crmUnitId);
            return CrmUnitDetailsResult.Unavailable();
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ParseAsync(response, crmUnitId, cancellationToken);
                case HttpStatusCode.NotFound:
                    return CrmUnitDetailsResult.NotAvailable();
                default:
                    logger.LogWarning(
                        "CRM GetUnitDetails returned status {StatusCode} for unit {UnitId}.", (int)response.StatusCode, crmUnitId);
                    return CrmUnitDetailsResult.Unavailable();
            }
        }
    }

    private async Task<CrmUnitDetailsResult> ParseAsync(HttpResponseMessage response, int crmUnitId, CancellationToken cancellationToken)
    {
        CrmUnitDetailsHttpResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<CrmUnitDetailsHttpResponse>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "CRM GetUnitDetails returned a malformed body for unit {UnitId}.", crmUnitId);
            return CrmUnitDetailsResult.Unavailable();
        }

        if (payload is null || !payload.Success)
        {
            return CrmUnitDetailsResult.Unavailable();
        }

        if (!payload.Found || payload.Unit is null)
        {
            return CrmUnitDetailsResult.NotAvailable();
        }

        var u = payload.Unit;
        var project = u.Project is { } p
            ? new CrmProjectDetails(
                Blank(p.Address), Blank(p.Status), ParseDate(p.ExpectedHandoverDate), ParseDate(p.ActualHandoverDate),
                Blank(p.Description), p.Amenities?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
                p.CompletionPercentage is >= 0 and <= 100 ? p.CompletionPercentage : null,
                ParseDate(p.ExpectedCompletionDate), ParseDate(p.ActualCompletionDate))
            : null;

        return CrmUnitDetailsResult.Found(new CrmUnitDetails(
            Blank(u.UnitTypeName),
            Blank(u.TowerName),
            u.Bedrooms,
            u.Area,
            Blank(u.AreaUnit),
            u.Parking?.Select(x => new CrmParkingSpace(Blank(x.Number), Blank(x.Level), Blank(x.Type))).ToList(),
            ParseDate(u.ExpectedHandoverDate),
            ParseDate(u.ActualHandoverDate),
            project,
            u.Sale is { } sale
                ? new CrmSaleDetails(sale.LeadId, NonNegative(sale.SoldPrice), NonNegative(sale.RegistrationCost), Blank(sale.Currency))
                : null));
    }

    /// <summary>An amount of 0 is genuine; a negative one is not a price and reads as not recorded.</summary>
    private static decimal? NonNegative(decimal? value) => value is >= 0 ? value : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>ISO date or date-time ("2027-03-31", "2027-03-31T00:00:00"); anything else — including a sentinel — is "not recorded", never a guessed date.</summary>
    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length >= 10
            && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date.Year > 1900)
        {
            return date;
        }

        return null;
    }
}
