using Microsoft.AspNetCore.WebUtilities;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// Ticketing's own read-only CRM lookups (MVP-API-Contracts.md §2.1-§2.3).
///
/// <para>
/// These call TigerCS.Api, which calls Tiger CRM. This application never
/// contacts a CRM directly and never writes CRM master data. A 502 from any
/// of these means the CRM is unreachable, which is a handled, first-class
/// path (ISSUE-006's fallback), not a failure to show as an error.
/// </para>
/// </summary>
public sealed class CrmApiClient(TigerCsApiClient api)
{
    /// <summary>Search units by number, optionally narrowed by property (§2.2).</summary>
    public Task<ApiResult<IReadOnlyList<UnitVerificationResponseDto>>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?> { ["unitNumber"] = unitNumber };
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            query["propertyName"] = propertyName;
        }

        return api.GetAsync<IReadOnlyList<UnitVerificationResponseDto>>(
            QueryHelpers.AddQueryString("/api/crm/units/search", query), cancellationToken);
    }

    /// <summary>
    /// The contacts linked to a unit (§2.3).
    ///
    /// <para>
    /// Keyed by the unit's <b>CRM</b> id, not by the local
    /// <c>unitReferenceId</c> - the two are different identifiers and only the
    /// former is accepted here.
    /// </para>
    /// </summary>
    public Task<ApiResult<IReadOnlyList<ContactVerificationResponseDto>>> GetContactsAsync(
        string crmUnitId, CancellationToken cancellationToken) =>
        api.GetAsync<IReadOnlyList<ContactVerificationResponseDto>>(
            $"/api/crm/units/{Uri.EscapeDataString(crmUnitId)}/contacts", cancellationToken);
}
