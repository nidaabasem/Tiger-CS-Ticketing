namespace TigerCS.Application.Modules.Collections.Abstractions;

public interface IPactReceivablesSource
{
    /// <summary>Reads instalments due on or before the end of <paramref name="throughDate"/> (the reporting month's last day).</summary>
    Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken);

    /// <summary>
    /// Reads only the requested instalment due-date window and, when <see cref="PactReceivablesRequest.CompanyId"/>
    /// is set, only that company's procedure. The default ignores the window (legacy sources); the SQL source honours it.
    /// </summary>
    Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request.ThroughDate, cancellationToken);
}

/// <summary>Due-date window (inclusive) and optional company scope pushed down to the PACT procedures.</summary>
/// <param name="FromDate">Passed as @StartDate; null uses the configured default start.</param>
/// <param name="ThroughDate">Last due date included.</param>
/// <param name="CompanyId">4 or 32 to read a single company; null reads both.</param>
public sealed record PactReceivablesRequest(DateOnly? FromDate, DateOnly ThroughDate, int? CompanyId = null);

public sealed record PactReceivableInstalment(
    int CompanyId, string TenantId, string FullName, string Mobile, string Email,
    int? UnitId, string UnitCode, string ProjectCode, string VoucherNumber,
    string ChequeNumber, DateTime DueDate, decimal Amount, string SourceStatus);

public sealed record PactReceivablesSnapshot(
    IReadOnlyList<PactReceivableInstalment> Items,
    DateTime ReadAtUtc,
    bool LegacyExclusionsApplied);

public sealed class PactReceivablesSourceException(string message) : Exception(message);
