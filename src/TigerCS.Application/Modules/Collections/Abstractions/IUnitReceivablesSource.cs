using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>A PACT customer-unit as the snapshot holds it: the account (company, tenant), the unit and the contact PACT has (the mobile may be empty).</summary>
public sealed record PactUnitIdentity(
    int CompanyId, string TenantId, int UnitId, string UnitCode, string FullName, string Mobile, string Email, string? TowerNumber, string? TowerName, int AllRows, int OpenRows);

public sealed record PactUnitInstalment(int CompanyId, string TenantId, int UnitId, string VoucherNumber, DateOnly DueDate, decimal RemainingAmount, decimal? OriginalAmount, decimal? PaidAmount, string PaymentStatus);

/// <summary>Everything the local snapshot knows about ONE unit key, read with one statement: PACT identities and unpaid instalments due today or earlier, and the CRM link per company.</summary>
public sealed record PactUnitReceivables(
    SnapshotStatusDto Snapshot, IReadOnlyList<PactUnitIdentity> Identities, IReadOnlyList<PactUnitInstalment> Instalments,
    IReadOnlyDictionary<int, CrmUnitLink> CrmLinks, bool CrmLoaded, DateTime ReadAtUtc);

/// <summary>Unit-keyed read for the Payment Summary: by tower + unit code (the normalised unit key), never by a customer phone number.</summary>
public interface IUnitReceivablesSource
{
    Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken);
}
