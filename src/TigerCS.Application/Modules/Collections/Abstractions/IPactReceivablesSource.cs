namespace TigerCS.Application.Modules.Collections.Abstractions;

public interface IPactReceivablesSource
{
    /// <summary>Reads instalments due on or before the end of <paramref name="throughDate"/> (the reporting month's last day).</summary>
    Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken);
}

public sealed record PactReceivableInstalment(
    int CompanyId, string TenantId, string FullName, string Mobile, string Email,
    int? UnitId, string UnitCode, string ProjectCode, string VoucherNumber,
    string ChequeNumber, DateTime DueDate, decimal Amount, string SourceStatus);

public sealed record PactReceivablesSnapshot(
    IReadOnlyList<PactReceivableInstalment> Items,
    DateTime ReadAtUtc,
    bool LegacyExclusionsApplied);

public sealed class PactReceivablesSourceException(string message) : Exception(message);
