using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>A PACT customer-unit as the snapshot holds it: the account (company, tenant), the unit and the contact PACT has (the mobile may be empty).</summary>
public sealed record PactUnitIdentity(
    int CompanyId, string TenantId, int UnitId, string UnitCode, string FullName, string Mobile, string Email, string? TowerNumber, string? TowerName, int AllRows, int OpenRows);

public sealed record PactUnitInstalment(int CompanyId, string TenantId, int UnitId, string VoucherNumber, DateOnly DueDate, decimal RemainingAmount, decimal? OriginalAmount, decimal? PaidAmount, string PaymentStatus);

/// <summary>Everything the local snapshot knows about ONE unit key, read with one statement: PACT identities and ALL unpaid instalments (future ones included), and the CRM link per company.</summary>
public sealed record PactUnitReceivables(
    SnapshotStatusDto Snapshot, IReadOnlyList<PactUnitIdentity> Identities, IReadOnlyList<PactUnitInstalment> Instalments,
    IReadOnlyDictionary<int, CrmUnitLink> CrmLinks, bool CrmLoaded, DateTime ReadAtUtc);

/// <summary>One unit of the PACT unit/account directory: it exists whether or not anything is open on it (<c>OpenRows = 0</c> = fully paid). <c>IsCancelled</c> = the PACT code contains '*'.</summary>
public sealed record PactDirectoryUnit(int CompanyId, string TenantId, int UnitId, string UnitCode, int OpenRows, int SettledRows, bool IsCancelled = false);

/// <summary>
/// The unit/account directory entry of a unit key: <c>Matches</c> are the (company, tenant, unit id) rows of the exact unit code (fully paid units included, cancelled codes never); <c>AccountUnits</c>
/// are EVERY unit of those accounts, paid or not. <c>IsComplete</c> says whether the SOURCE is accepted as listing every unit of a tenant account (a statement may only be shown for one unit when it is);
/// it is false unless an operator verified the source live. <c>Source</c> names where the directory comes from.
/// </summary>
public sealed record PactAccountDirectory(IReadOnlyList<PactDirectoryUnit> Matches, IReadOnlyList<PactDirectoryUnit> AccountUnits, bool IsComplete, string Source, DateTime? AsOfUtc);

/// <summary>Unit-keyed read for the Payment Summary: by tower + unit code (the normalised unit key), never by a customer phone number.</summary>
public interface IUnitReceivablesSource
{
    Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>The unit/account directory of the unit key, fully paid units included. Never looked up by phone number or name.</summary>
    Task<PactAccountDirectory> ReadAccountDirectoryAsync(string unitKey, int? companyId, CancellationToken cancellationToken);
}
