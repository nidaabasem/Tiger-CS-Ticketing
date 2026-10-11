using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Reads receivables from the LOCAL snapshot (<c>dbo.CollectionsRcvLine</c>, procedures <c>dbo.usp_CollectionsRcv_*</c>). It never calls PACT, so a page view is independent of PACT latency.
/// Missing data is a status, never an empty list: a company that was never loaded reports <c>HasSnapshot = false</c>; a stale or failed one is served with its status so the caller can
/// warn and the export gate can refuse. The snapshot always covers the whole window 2000-2099 (<see cref="ReceivablesSnapshotOptions.SourceFromDate"/>..), so there are no coverage gaps.
/// </summary>
public sealed class RcvReceivablesSource(
    IConfiguration configuration, ReceivablesSnapshotOptions options, PactReceivablesOptions pactOptions,
    TimeProvider timeProvider, ILogger<RcvReceivablesSource> logger) : IPactReceivablesSource, IPactInstalmentUnitSource, IUnitReceivablesSource
{
    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    /// <summary>Payment rows of a due-date window (Campaigns, Review): <c>dbo.usp_CollectionsRcv_GetLines</c>.</summary>
    public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
    {
        if (pactOptions.ApplyLegacyExclusions)
            throw new PactReceivablesSourceException("The legacy exclusions are not supported by the local receivables snapshot; disable them.");
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var from = request.FromDate ?? DateOnly.FromDateTime(pactOptions.StartDate);
        if (from > request.ThroughDate)
            throw new PactReceivablesSourceException("The receivables start date is after its end date.");

        var companies = new List<SnapshotCompanyRaw>();
        var rows = new List<PactReceivableInstalment>();
        var limit = Math.Clamp(pactOptions.MaxSourceRows, 1, 1_000_000);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetLines");
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.ThroughDate.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await ReadScopeAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            var asOf = request.AsOfDate ?? request.ThroughDate;
            var monthStart = new DateOnly(asOf.Year, asOf.Month, 1);
            var nextMonth = monthStart.AddMonths(1);
            while (await reader.ReadAsync(cancellationToken))
            {
                var due = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate")));
                // The legacy month classification of the instalments API; Campaigns and Review ask for Any.
                var keep = request.Class switch
                {
                    PactReceivableClass.Overdue => due < monthStart,
                    PactReceivableClass.Due => due >= monthStart && due < nextMonth,
                    PactReceivableClass.DueOrOverdue => due < nextMonth,
                    _ => true
                };
                if (!keep) continue;
                rows.Add(new PactReceivableInstalment(
                    reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")),
                    reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("ProjectCode")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    due.ToDateTime(TimeOnly.MinValue), reader.GetDecimal(reader.GetOrdinal("Amount")), Text(reader, "SourceStatus") ?? "",
                    Text(reader, "TowerNumber"), null, Text(reader, "TowerName")));
                if (rows.Count > limit)
                    throw new PactReceivablesSourceException("The receivables snapshot exceeded the configured row limit; no partial list was returned.");
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Receivables snapshot read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }

        var snapshot = ReceivablesSnapshotComposer.Compose(from, request.ThroughDate, companies, [], rows, timeProvider.GetUtcNow().UtcDateTime, options.MaxAgeMinutes);
        logger.LogInformation("Receivables snapshot read: {Rows} rows, {Loaded}/{Scope} companies loaded, oldest success {ReadAt:O}, window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.",
            rows.Count, snapshot.Snapshot!.Companies.Count(c => c.HasSnapshot), snapshot.Snapshot.Companies.Count, snapshot.ReadAtUtc, from, request.ThroughDate);
        return snapshot;
    }

    /// <summary>
    /// Receivables page: one round trip to <c>dbo.usp_CollectionsRcv_GetUnits</c>. Payments are filtered (window, Due / Overdue), summed per apartment, filtered by Minimum Total, counted,
    /// ordered (latest due date DESC) and paged in SQL; the payments of exactly the listed apartments come back with them.
    /// <see cref="PactInstalmentsRequest.AsOf"/> is TODAY (Dubai); <see cref="PactInstalmentsRequest.MinTotal"/> is the apartment-level minimum (strictly greater).
    /// </summary>
    public async Task<PactInstalmentUnitsPage> ReadInstalmentUnitsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var companies = new List<SnapshotCompanyRaw>();
        var heads = new List<(int Company, string Tenant, long UnitId, string UnitCode, string Name, string? Tower, string? TowerName, int Count, decimal Total, DateOnly Latest,
            int? CrmCustomers, int? CrmCustomerId, string? CrmName, string? CrmPhone, string? CrmEmail)>();
        var byUnit = new Dictionary<(int, string, long, string), List<PactInstalmentRowDto>>();
        var totals = new PactInstalmentTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetUnits");
            command.Parameters.Add("@Today", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@Status", SqlDbType.VarChar, 10).Value = request.Status ?? "all";
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            command.Parameters.Add(new SqlParameter("@MinTotal", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = (object?)request.MinTotal ?? DBNull.Value });
            command.Parameters.Add("@PageNumber", SqlDbType.Int).Value = request.Page;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = request.PageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            await ReadScopeAsync(reader, cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                totals = new PactInstalmentTotalsDto(reader.GetInt32(reader.GetOrdinal("LineCount")), reader.GetDecimal(reader.GetOrdinal("Total")),
                    reader.GetInt32(reader.GetOrdinal("OverdueLineCount")), reader.GetDecimal(reader.GetOrdinal("OverdueTotal")),
                    reader.GetInt32(reader.GetOrdinal("DueLineCount")), reader.GetDecimal(reader.GetOrdinal("DueTotal")), 0, 0m, 0, reader.GetInt32(reader.GetOrdinal("UnitCount")));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                heads.Add((reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")),
                    reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    reader.GetInt32(reader.GetOrdinal("InstalmentCount")), reader.GetDecimal(reader.GetOrdinal("Total")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("LatestDueDate"))),
                    NullableInt(reader, "CrmCustomers"), NullableInt(reader, "CrmCustomerId"), Text(reader, "CrmName"), Text(reader, "CrmPhone"), Text(reader, "CrmEmail")));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var company = reader.GetInt32(reader.GetOrdinal("CompanyId"));
                var key = (company, reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")));
                if (!byUnit.TryGetValue(key, out var list)) byUnit[key] = list = [];
                // PACT returns only the remaining amount: the original and paid amounts and whether the payment is unpaid or partly paid are unknown, never estimated.
                list.Add(new PactInstalmentRowDto(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), null, null, (int)key.Item3, key.Item4, key.Item2, "",
                    reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))), null, null, reader.GetDecimal(reader.GetOrdinal("RemainingAmount")),
                    "Unknown", reader.GetString(reader.GetOrdinal("Classification")), Text(reader, "SourceStatus") ?? ""));
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit page read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, companies, [], now, options.MaxAgeMinutes);
        var units = heads.Select(h => new PactInstalmentUnitDto(h.Company, ReceivablesSnapshotStatusBuilder.CompanyName(h.Company), h.Tower, h.TowerName, (int)h.UnitId, h.UnitCode,
            h.Tenant, h.Name, h.Count, h.Total, h.Latest,
            byUnit.TryGetValue((h.Company, h.Tenant, h.UnitId, h.UnitCode), out var rows) ? rows.Select(r => r with { TowerNumber = h.Tower, TowerName = h.TowerName, CustomerName = h.Name }).ToList() : [],
            h.CrmCustomers, h.CrmCustomerId, h.CrmName, h.CrmPhone, h.CrmEmail)).ToList();
        var sqlMs = timer.Elapsed.TotalMilliseconds;
        logger.LogInformation("Unit page read: page {Page}, {Units} units / {Total} payments in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, status {Status}, min total {Min}).",
            request.Page, totals.UnitCount, totals.Count, sqlMs, request.From, request.To, request.Status ?? "all", request.MinTotal);
        return new PactInstalmentUnitsPage(totals, units, false, status, ReceivablesSnapshotComposer.ReadAt(status), sqlMs);
    }

    /// <summary>
    /// One unit by its normalised key (tower + unit code), read with ONE statement: the PACT accounts that hold it (a missing PACT mobile changes nothing), the unit's own unpaid
    /// instalments due today or earlier, and its CRM link. Nothing is looked up by phone number or name.
    /// </summary>
    public async Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken)
    {
        var companies = new List<SnapshotCompanyRaw>();
        var identities = new List<PactUnitIdentity>();
        var instalments = new List<PactUnitInstalment>();
        var links = new Dictionary<int, CrmUnitLink>();
        var crmLoaded = false;
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using (var command = Procedure(connection, "dbo.usp_CollectionsRcv_GetUnit"))
            {
                command.Parameters.Add("@UnitKey", SqlDbType.NVarChar, 220).Value = unitKey;
                command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)companyId ?? DBNull.Value;
                command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    identities.Add(new PactUnitIdentity(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), (int)reader.GetInt64(reader.GetOrdinal("UnitId")),
                        reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                        Text(reader, "TowerNumber"), Text(reader, "TowerName"), reader.GetInt32(reader.GetOrdinal("AllRows")), reader.GetInt32(reader.GetOrdinal("OpenRows"))));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    instalments.Add(new PactUnitInstalment(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), (int)reader.GetInt64(reader.GetOrdinal("UnitId")),
                        reader.GetString(reader.GetOrdinal("VoucherNumber")), DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))), reader.GetDecimal(reader.GetOrdinal("RemainingAmount")),
                        null, null, "Unknown"));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) links[reader.GetInt32(reader.GetOrdinal("CompanyId"))] = SqlCollectionsCrmOwnerStore.ReadLink(reader);
            }
            await using var state = Procedure(connection, "dbo.usp_Collections_GetCrmOwnerState");
            await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
            crmLoaded = await stateReader.ReadAsync(cancellationToken) && stateReader.GetBoolean(0);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit receivables read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(today, today, companies, [], now, options.MaxAgeMinutes);
        return new PactUnitReceivables(status, identities, instalments, links, crmLoaded, ReceivablesSnapshotComposer.ReadAt(status));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(options.ReadCommandTimeoutSeconds, 1, 600) };

    private static async Task ReadScopeAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
            throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
        await reader.NextResultAsync(cancellationToken);
    }

    /// <summary>One row of <c>usp_CollectionsRcv_GetStatus</c>. Coverage is the configured window (the whole window is always stored); nothing about paid instalments or breakdowns is claimed.</summary>
    private SnapshotCompanyRaw ReadCompany(SqlDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetBoolean(reader.GetOrdinal("HasSnapshot")),
        Utc(reader, "LastSuccessUtc"), Utc(reader, "LastAttemptUtc"), Text(reader, "LastStatus"),
        Int(reader, "LastErrorNumber"), reader.GetInt32(reader.GetOrdinal("ConsecutiveFailures")),
        reader.GetInt32(reader.GetOrdinal("PublishedRows")),
        reader.GetBoolean(reader.GetOrdinal("HasSnapshot")) ? DateOnly.FromDateTime(options.SourceFromDate) : null,
        reader.GetBoolean(reader.GetOrdinal("HasSnapshot")) ? DateOnly.FromDateTime(options.SourceThroughDate) : null,
        0, 0m, 0, 0m, 0, 0,
        reader.GetBoolean(reader.GetOrdinal("IsLoading")), reader.GetBoolean(reader.GetOrdinal("IsLoading")) ? Utc(reader, "LastAttemptUtc") : null, Text(reader, "LastStatus"));

    private static string? Text(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? NullableInt(SqlDataReader r, string name) => Int(r, name);
    private static DateTime? Utc(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc); }
}
