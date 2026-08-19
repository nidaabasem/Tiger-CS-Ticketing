using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Abstractions;

/// <summary>
/// External adapter contract for the Tiger Group CRM (Module-Design.md's CRM
/// Verification module, "external adapter contract"). Implemented in
/// TigerCS.Integrations — either a real HTTP-backed gateway, or the
/// feature-flagged mock adapter used while real CRM endpoint details are
/// unavailable (never described as production-ready — see MockCrmGateway).
/// </summary>
public interface ICrmGateway
{
    Task<CrmUnitResult?> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmUnitResult>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CrmContactResult>> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default);
}

public sealed record CrmUnitResult(
    string CrmUnitId, string UnitNumber, string? PropertyName, string? TowerName, string? UnitType);

public sealed record CrmContactResult(
    string CrmContactId,
    string? DisplayName,
    string? ContactChannel,
    ContactType ContactType,
    string? AuthorizedRepresentativeOfCrmContactId);

/// <summary>
/// Thrown by an <see cref="ICrmGateway"/> implementation when the CRM cannot
/// be reached (timeout/error) — maps to 502/504 (MVP-API-Contracts.md §2.1)
/// and is the trigger for the Intake Record fallback flow (out of scope in
/// this phase — Ticketing module).
/// </summary>
public sealed class CrmGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
