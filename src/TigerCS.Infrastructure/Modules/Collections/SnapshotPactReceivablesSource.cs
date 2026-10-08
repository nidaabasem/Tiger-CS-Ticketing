using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Reads receivables from the LOCAL snapshot (dbo.usp_Collections_GetReceivables). It never calls PACT, so a preview is
/// independent of PACT latency. Missing data is an error, never an empty list; stale data is returned with its status so the
/// caller can warn and the export gate can refuse.
/// </summary>
public sealed class SnapshotPactReceivablesSource(
    IConfiguration configuration, ReceivablesSnapshotOptions snapshotOptions, PactReceivablesOptions pactOptions,
    TimeProvider timeProvider, ILogger<SnapshotPactReceivablesSource> logger) : IPactReceivablesSource
{
    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
    {
        if (pactOptions.ApplyLegacyExclusions)
            throw new PactReceivablesSourceException("The legacy exclusions are not supported by the local receivables snapshot; disable them or set Collections:ReceivablesSnapshot:UseLocalSnapshot to false.");
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var from = request.FromDate ?? DateOnly.FromDateTime(pactOptions.StartDate);
        if (from > request.ThroughDate)
            throw new PactReceivablesSourceException("The receivables start date is after its end date.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");

        var asOf = request.AsOfDate ?? request.ThroughDate;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var companies = new List<SnapshotCompanyRaw>();
        var unmatched = new List<UnmatchedTowerDto>();
        var rows = new List<PactReceivableInstalment>();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetReceivables", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600)
            };
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.ThroughDate.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = asOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ReceivableClass", SqlDbType.VarChar, 20).Value = request.Class switch
            {
                PactReceivableClass.DueOrOverdue => "DueOrOverdue",
                PactReceivableClass.Due => "Due",
                PactReceivableClass.Overdue => "Overdue",
                _ => "Any"
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // 1 scope
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            // 2 coverage
            while (await reader.ReadAsync(cancellationToken))
                companies.Add(new SnapshotCompanyRaw(
                    reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetBoolean(reader.GetOrdinal("HasSnapshot")),
                    Utc(reader, "LastSuccessUtc"), Utc(reader, "LastAttemptUtc"), Text(reader, "LastAttemptStatus"),
                    Int(reader, "LastErrorNumber"), reader.GetInt32(reader.GetOrdinal("ConsecutiveFailures")),
                    reader.GetInt32(reader.GetOrdinal("SnapshotRowCount")), Day(reader, "CoverageFromDate"), Day(reader, "CoverageThroughDate"),
                    reader.GetInt32(reader.GetOrdinal("ExcludedInvalidUnitRows")), reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidUnitAmount")),
                    reader.GetInt32(reader.GetOrdinal("ExcludedInvalidIdentityRows")), reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidIdentityAmount")),
                    reader.GetInt32(reader.GetOrdinal("ContradictoryStatusRows")), reader.GetInt32(reader.GetOrdinal("UnknownStatusRows")),
                    reader.GetBoolean(reader.GetOrdinal("RefreshInProgress"))));
            await reader.NextResultAsync(cancellationToken);
            // 3 rows
            var limit = Math.Clamp(pactOptions.MaxSourceRows, 1, 1_000_000);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PactReceivableInstalment(
                    reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")),
                    reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("ProjectCode")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    reader.GetDateTime(reader.GetOrdinal("DueDate")), reader.GetDecimal(reader.GetOrdinal("Amount")), Text(reader, "SourceStatus") ?? "",
                    Text(reader, "TowerNumber"), Int(reader, "TowerId"), Text(reader, "TowerName")));
                if (rows.Count > limit)
                    throw new PactReceivablesSourceException("The receivables snapshot exceeded the configured row limit; no partial list was returned.");
            }
            await reader.NextResultAsync(cancellationToken);
            // 4 unmatched towers
            while (await reader.ReadAsync(cancellationToken))
                unmatched.Add(new UnmatchedTowerDto(reader.GetInt32(reader.GetOrdinal("CompanyId")), Text(reader, "TowerNumber"),
                    reader.GetString(reader.GetOrdinal("Reason")), reader.GetInt64(reader.GetOrdinal("RowCount_")), reader.GetDecimal(reader.GetOrdinal("Amount"))));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Receivables snapshot read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }

        var snapshot = ReceivablesSnapshotComposer.Compose(from, request.ThroughDate, companies, unmatched, rows, now, snapshotOptions.MaxAgeMinutes);
        logger.LogInformation("Receivables snapshot read: {Rows} rows, {Loaded}/{Scope} companies loaded, oldest success {ReadAt:O}, window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.",
            rows.Count, snapshot.Snapshot!.Companies.Count(c => c.HasSnapshot), snapshot.Snapshot.Companies.Count, snapshot.ReadAtUtc, from, request.ThroughDate);
        return snapshot;
    }

    private static string? Text(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static DateTime? Utc(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc); }
    private static DateOnly? Day(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i)); }
}

public sealed class SqlCollectionsTowerCatalog(IConfiguration configuration, ReceivablesSnapshotOptions options, ILogger<SqlCollectionsTowerCatalog> logger)
    : ICollectionsTowerCatalog
{
    public async Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var towers = new List<CollectionsTowerDto>();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetTowers", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                towers.Add(new CollectionsTowerDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4)));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Tower list read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The tower list could not be read.");
        }
        return towers;
    }
}

/// <summary>Calls dbo.usp_Collections_RefreshReceivables on the Ticketing database. All PACT/linked-server work happens inside SQL.</summary>
public sealed class SqlReceivablesRefresher(IConfiguration configuration, ReceivablesSnapshotOptions options, ILogger<SqlReceivablesRefresher> logger)
    : IReceivablesRefresher
{
    // Process-level guard: a second refresh in this process returns immediately. Other processes are stopped by the SQL application lock.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ReceivablesRefreshResult> RefreshAsync(string triggerSource, int? companyId, CancellationToken cancellationToken,
        DateOnly? fromDate = null, DateOnly? throughDate = null)
    {
        if (companyId is not (null or 4 or 32)) throw new ArgumentOutOfRangeException(nameof(companyId));
        if (!await Gate.WaitAsync(0, cancellationToken))
            return new ReceivablesRefreshResult(null, "AlreadyRunning", "A refresh is already running in this process.", []);
        try
        {
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_RefreshReceivables", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.RefreshCommandTimeoutSeconds, 30, 7200) };
            command.Parameters.Add("@TriggerSource", SqlDbType.NVarChar, 50).Value = triggerSource.Length > 50 ? triggerSource[..50] : triggerSource;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)companyId ?? DBNull.Value;
            // The procedure unions this window with the coverage already published (@ExtendCoverage = 1), so an on-demand range
            // load is kept by every later scheduled refresh.
            command.Parameters.Add("@SourceFromDate", SqlDbType.Date).Value = (fromDate?.ToDateTime(TimeOnly.MinValue) ?? options.SourceFromDate).Date;
            command.Parameters.Add("@SourceThroughDate", SqlDbType.Date).Value = (throughDate?.ToDateTime(TimeOnly.MinValue) ?? options.SourceThroughDate).Date;
            command.Parameters.Add("@ExtendCoverage", SqlDbType.Bit).Value = true;
            command.Parameters.Add("@SourceMinAmount", SqlDbType.Int).Value = options.SourceMinAmount;
            command.Parameters.Add("@MaxRawRows", SqlDbType.Int).Value = options.MaxRawRows;
            command.Parameters.Add("@MaxShrinkPercent", SqlDbType.Int).Value = Math.Clamp(options.MaxShrinkPercent, 0, 100);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Guid? runId = null; var status = "Failed"; string? message = null;
            if (await reader.ReadAsync(cancellationToken))
            {
                runId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                status = reader.GetString(1);
                message = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            var companies = new List<ReceivablesRefreshCompanyResult>();
            if (await reader.NextResultAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    companies.Add(new ReceivablesRefreshCompanyResult(reader.GetInt32(0), reader.GetString(1),
                        Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4), Nullable(reader, 5), Nullable(reader, 6), Nullable(reader, 7),
                        reader.IsDBNull(8) ? null : reader.GetString(8)));
            return new ReceivablesRefreshResult(runId, status, message, companies);
        }
        catch (SqlException ex)
        {
            logger.LogError("Receivables refresh procedure failed (SQL error {SqlNumber}).", ex.Number);
            return new ReceivablesRefreshResult(null, "Failed", $"The refresh procedure failed (SQL error {ex.Number}).", []);
        }
        finally { Gate.Release(); }
    }

    private static int? Nullable(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
