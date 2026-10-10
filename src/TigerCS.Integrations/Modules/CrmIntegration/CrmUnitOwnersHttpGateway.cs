using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Real HTTP gateway for the bulk CRM owner feed, <c>GET /TicketingSystem/GetUnitOwners?page=&amp;pageSize=</c> (contract: docs/Collections/CRM-GetUnitOwners-Contract.md).
/// <b>CRM does not publish this endpoint yet</b>: until it does the gateway answers <c>Unavailable</c> / <c>InvalidResponse</c> (HTTP 404 / HTML) and the load simply records the
/// failure - Collections keeps working on PACT contact data. The X-SECRET-KEY is never logged. One request per page; never one per unit.
/// </summary>
public sealed class CrmUnitOwnersHttpGateway(HttpClient httpClient, IOptions<CrmGatewayOptions> options, ILogger<CrmUnitOwnersHttpGateway> logger) : ICrmUnitOwnersGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<CrmUnitOwnersPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var secret = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(options.Value.BaseUrl))
            return new(CrmUnitOwnersOutcome.NotConfigured, [], Message: "Crm:BaseUrl / Crm:SecretKey are not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"TicketingSystem/GetUnitOwners?page={page}&pageSize={pageSize}");
        request.Headers.TryAddWithoutValidation("X-SECRET-KEY", secret);
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, cancellationToken); }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { logger.LogWarning(ex, "CRM GetUnitOwners timed out (page {Page}).", page); return new(CrmUnitOwnersOutcome.Unavailable, [], Message: "CRM request timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        { logger.LogWarning(ex, "CRM GetUnitOwners could not be reached (page {Page}).", page); return new(CrmUnitOwnersOutcome.Unavailable, [], Message: "CRM could not be reached."); }
        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized: return new(CrmUnitOwnersOutcome.Unauthorized, [], Message: "CRM rejected the configured secret key.");
                case HttpStatusCode.OK: break;
                default: return new(CrmUnitOwnersOutcome.Unavailable, [], Message: $"CRM returned status {(int)response.StatusCode} (is GetUnitOwners published?).");
            }
            CrmUnitOwnersHttpResponse? payload;
            try { payload = await response.Content.ReadFromJsonAsync<CrmUnitOwnersHttpResponse>(JsonOptions, cancellationToken); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            { return new(CrmUnitOwnersOutcome.InvalidResponse, [], Message: "CRM returned a malformed response body."); }
            if (payload is null || !payload.Success || payload.Owners is null)
                return new(CrmUnitOwnersOutcome.InvalidResponse, [], Message: payload?.Message ?? "CRM answered without an owners list.");
            return new(CrmUnitOwnersOutcome.Success, payload.Owners.Select(o => new CrmUnitOwnerDto(o.LeadId, o.LeadStatus, o.LeadStatusName, o.CustomerType, o.CustomerId,
                o.FullNameEnglish, o.FullNameArabic, o.MobileNumber, o.Email, o.UnitId, o.UnitNumber, o.ProjectId, o.ProjectCode, o.ProjectName)).ToList(), payload.Total);
        }
    }
}

internal sealed record CrmUnitOwnersHttpResponse(bool Success, string? Message, int? Total, List<CrmUnitOwnerHttpDto>? Owners);

internal sealed record CrmUnitOwnerHttpDto(
    int LeadId, int LeadStatus, string? LeadStatusName, int CustomerType, int CustomerId, string? FullNameEnglish, string? FullNameArabic, string? MobileNumber, string? Email,
    int UnitId, string? UnitNumber, int ProjectId, string? ProjectCode, string? ProjectName);
