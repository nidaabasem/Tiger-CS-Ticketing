using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

/// <summary>
/// The Tiger Group CRM's entire responsibility toward this system, and the
/// only thing this interface may ever grow to do: <b>read-only data
/// access.</b> Tiger CRM (i) looks up a unit by unit number, (ii) returns
/// that unit's immutable CRM-issued identifier plus the minimum display
/// details needed for an agent's verbal read-back, and (iii) returns the
/// contacts/owners/tenants/authorized representatives linked to that unit.
/// Nothing more.
///
/// <para>
/// <b>This is a data-access integration port, not a verification
/// authority.</b> It holds no state machine, no session concept, no
/// eligibility rule, no audit trail, and makes no decision about whether a
/// ticket may be created — those are all Tiger CS Ticketing's own business
/// logic (<c>VerificationSessionAppService</c>: selecting the
/// requester, recording the verification method/result, deciding whether
/// ticket creation is allowed, capturing the immutable verification-time
/// snapshot, and the audit/authorization/expiry rules around all of it).
/// <see cref="TigerCS.Application.Modules.CustomerVerification.Services.CrmUnitLookupAppService"/>
/// is this port's only caller — it does cache-aside lookups against this
/// interface and nothing else; it is not where verification decisions are
/// made either.
/// </para>
/// <para>
/// Implemented in TigerCS.Integrations.Modules.CrmIntegration — either a
/// real HTTP-backed gateway, or the feature-flagged mock adapter used while
/// real CRM endpoint details are unavailable (never described as
/// production-ready — see MockCrmGateway). Any future automated intake
/// surface (e.g. a Genesys AI voice/chat integration) that needs a unit or
/// contact looked up must call Tiger CS Ticketing's own API — it must never
/// call this interface, or any CRM system, directly: verification rules and
/// the decision of whether a ticket may be created live only in Tiger CS
/// Ticketing, and only Tiger CS Ticketing is a legitimate caller of a CRM
/// data source.
/// </para>
/// </summary>
public interface ICrmGateway
{
    Task<CrmUnitResult?> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmUnitResult>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmContactResult>> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default);
}

/// <summary>Raw CRM unit data — the CRM's immutable identifier plus the minimum display details for read-back. No verification state.</summary>
public sealed record CrmUnitResult(
    string CrmUnitId, string UnitNumber, string? PropertyName, string? TowerName, string? UnitType);

/// <summary>Raw CRM contact data — an owner/tenant/authorized representative linked to a unit. No verification state.</summary>
public sealed record CrmContactResult(
    string CrmContactId,
    string? DisplayName,
    string? ContactChannel,
    ContactType ContactType,
    string? AuthorizedRepresentativeOfCrmContactId);

/// <summary>
/// Thrown by an <see cref="ICrmGateway"/> implementation when the CRM cannot
/// be reached (timeout/error) — maps to 502/504 (MVP-API-Contracts.md §2.1)
/// and is the trigger for the Intake Record fallback flow (Tiger CS
/// Ticketing business logic, out of scope in this phase).
/// </summary>
public sealed class CrmGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
