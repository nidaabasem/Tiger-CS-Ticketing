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
/// Reads receivables from the LOCAL snapshot (dbo.usp_Collections_GetReceivables). It never calls PACT, so a preview is
/// independent of PACT latency. Missing data is an error, never an empty list; stale data is returned with its status so the
/// caller can warn and the export gate can refuse.
/// </summary>
public sealed class SnapshotPactReceivablesSource(
    IConfiguration configuration, ReceivablesSnapshotOptions snapshotOptions, PactReceivablesOptions pactOptions,
    TimeProvider timeProvider, ILogger<SnapshotPactReceivablesSource> logger) : IPactReceivablesSource, IPactReceivablesPageSource, IPactInstalmentSource, IPactInstalmentUnitSource, IPactInstalmentMonthSource, IPactCampaignSource, IUnitReceivablesSource
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
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // 1 scope
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            // 2 coverage
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
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
                    Text(reader, "TowerNumber"), Int(reader, "TowerId"), Text(reader, "TowerName"),
                    PlanAmount: NullableDecimal(reader, "OriginalAmount"), AllocatedAmount: NullableDecimal(reader, "PaidAmount")));   // verified breakdown only (V2 shape); never invented
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

    /// <summary>
    /// Receivables page: one round trip to dbo.usp_Collections_GetReceivablesPage, which filters, aggregates per apartment, counts and pages in
    /// SQL. Only one page of apartments and their instalments is read into memory.
    /// </summary>
    public async Task<PactReceivablesPage> ReadPageAsync(PactReceivablesPageRequest request, CancellationToken cancellationToken)
    {
        if (pactOptions.ApplyLegacyExclusions)
            throw new PactReceivablesSourceException("The legacy exclusions are not supported by the local receivables snapshot; disable them or set Collections:ReceivablesSnapshot:UseLocalSnapshot to false.");
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var companies = new List<SnapshotCompanyRaw>();
        var unmatched = new List<UnmatchedTowerDto>();
        var apartments = new List<PactReceivableApartment>();
        int totalApartments = 0, dueApartments = 0, overdueApartments = 0;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetReceivablesPage", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) };
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            command.Parameters.Add("@Status", SqlDbType.VarChar, 10).Value = request.Status;
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            command.Parameters.Add("@PageNumber", SqlDbType.Int).Value = request.Page;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = request.PageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalApartments = reader.GetInt32(reader.GetOrdinal("TotalApartments"));
                dueApartments = reader.GetInt32(reader.GetOrdinal("DueApartments"));
                overdueApartments = reader.GetInt32(reader.GetOrdinal("OverdueApartments"));
            }
            await reader.NextResultAsync(cancellationToken);
            var heads = new List<(int Company, string Tenant, long UnitId, string UnitCode, string Name, string Mobile, string Email, string Project, string? Tower, string? TowerName,
                int DueRows, int OverRows, decimal? DueAmt, decimal? OverAmt, DateOnly Earliest)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var dueAmbiguous = reader.GetBoolean(reader.GetOrdinal("DueAmbiguous"));
                var overAmbiguous = reader.GetBoolean(reader.GetOrdinal("OverAmbiguous"));
                heads.Add((reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")),
                    reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")),
                    reader.GetString(reader.GetOrdinal("Email")), reader.GetString(reader.GetOrdinal("ProjectCode")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    reader.GetInt32(reader.GetOrdinal("DueRows")), reader.GetInt32(reader.GetOrdinal("OverRows")),
                    dueAmbiguous ? null : reader.GetDecimal(reader.GetOrdinal("DueAmt")), overAmbiguous ? null : reader.GetDecimal(reader.GetOrdinal("OverAmt")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("Earliest")))));
            }
            await reader.NextResultAsync(cancellationToken);
            var byApartment = new Dictionary<(int, string, long, string), List<PactReceivableInstalment>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")));
                if (!byApartment.TryGetValue(key, out var list)) byApartment[key] = list = [];
                var head = heads.FirstOrDefault(h => (h.Company, h.Tenant, h.UnitId, h.UnitCode) == key);
                list.Add(new PactReceivableInstalment(key.Item1, key.Item2, head.Name ?? "", head.Mobile ?? "", head.Email ?? "", (int)key.Item3, key.Item4, reader.GetString(reader.GetOrdinal("ProjectCode")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")), reader.GetDateTime(reader.GetOrdinal("DueDate")),
                    reader.GetDecimal(reader.GetOrdinal("Amount")), Text(reader, "SourceStatus") ?? "", head.Tower, null, head.TowerName));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                unmatched.Add(new UnmatchedTowerDto(reader.GetInt32(reader.GetOrdinal("CompanyId")), Text(reader, "TowerNumber"),
                    reader.GetString(reader.GetOrdinal("Reason")), reader.GetInt64(reader.GetOrdinal("RowCount_")), reader.GetDecimal(reader.GetOrdinal("Amount"))));
            foreach (var h in heads)
                apartments.Add(new PactReceivableApartment(h.Company, h.Tenant, (int)h.UnitId, h.UnitCode, h.Name, h.Mobile, h.Email, h.Project, h.Tower, h.TowerName,
                    h.DueRows, h.OverRows, h.DueAmt, h.OverAmt, h.Earliest,
                    byApartment.TryGetValue((h.Company, h.Tenant, h.UnitId, h.UnitCode), out var rows) ? rows : []));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Receivables page read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, companies, unmatched, now, snapshotOptions.MaxAgeMinutes);
        var sqlMs = timer.Elapsed.TotalMilliseconds;
        logger.LogInformation("Receivables page read: page {Page} of {Total} apartments in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, min {Min}).",
            request.Page, totalApartments, sqlMs, request.From, request.To, request.MinAmount);
        return new PactReceivablesPage(totalApartments, dueApartments, overdueApartments, apartments, ReceivablesSnapshotComposer.ReadAt(status), status, sqlMs);
    }

    /// <summary>
    /// Campaigns preview / export: one round trip to dbo.usp_Collections_GetCampaignUnits, which filters the window, aggregates per unit, applies the stage rule and
    /// the unit-level review flags, searches, counts and pages in SQL. When the procedure reports phone / e-mail texts it has no normalisation for yet, they are
    /// normalised here (CollectionsContactNormalizer - the only definition), stored, and the read is repeated; the stored values serve every later read.
    /// A snapshot the SQL engine cannot use (published before it existed, or with text it cannot trim like .NET) returns Supported = false.
    /// </summary>
    public async Task<PactCampaignPage> ReadCampaignAsync(PactCampaignRequest request, CancellationToken cancellationToken)
    {
        if (pactOptions.ApplyLegacyExclusions)
            throw new PactReceivablesSourceException("The legacy exclusions are not supported by the local receivables snapshot; disable them or set Collections:ReceivablesSnapshot:UseLocalSnapshot to false.");
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var read = await ReadCampaignOnceAsync(connectionString, request, cancellationToken);
                if (read.Status == "NeedsNorm" && attempt < 3)
                {
                    logger.LogInformation("Campaign read: normalising {Count} contact values (attempt {Attempt}).", read.Missing.Count, attempt + 1);
                    await CollectionsContactNormStore.StoreAsync(connectionString, read.Missing, snapshotOptions.ReadCommandTimeoutSeconds, cancellationToken);
                    continue;
                }
                if (read.Status == "NeedsNorm")
                    throw new PactReceivablesSourceException("The campaign contact values could not be prepared. Please retry.");
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, read.Companies, read.Unmatched, now, snapshotOptions.MaxAgeMinutes);
                var sqlMs = timer.Elapsed.TotalMilliseconds;
                if (read.Status == "LegacyRequired")
                {
                    logger.LogInformation("Campaign read: the snapshot is not prepared for the SQL engine; the application evaluates the campaign in memory.");
                    return new PactCampaignPage(false, 0, 0, 0, 0, [], ReceivablesSnapshotComposer.ReadAt(status), status, sqlMs);
                }
                logger.LogInformation("Campaign read: {Units} of {Total} units in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, min {Min}).",
                    read.Units.Count, read.Total, sqlMs, request.From, request.To, request.MinAmount);
                return new PactCampaignPage(true, read.BadIdentityRows, read.Total, read.Clean, read.Review, read.Units, ReceivablesSnapshotComposer.ReadAt(status), status, sqlMs);
            }
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Campaign read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
    }

    private sealed record CampaignRead(string Status, int BadIdentityRows, int Total, int Clean, int Review, List<SnapshotCompanyRaw> Companies,
        List<UnmatchedTowerDto> Unmatched, List<CampaignUnitFacts> Units, List<(int Id, byte Kind, string Raw)> Missing);

    private async Task<CampaignRead> ReadCampaignOnceAsync(string connectionString, PactCampaignRequest request, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_GetCampaignUnits", connection)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) };
        command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
        command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
        command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
        command.Parameters.Add("@StageFrom", SqlDbType.Date).Value = request.StageFrom is { } stageFrom ? stageFrom.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@StageToExclusive", SqlDbType.Date).Value = request.StageToExclusive.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add(new SqlParameter("@Threshold", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.Threshold });
        command.Parameters.Add("@ContactRequired", SqlDbType.Bit).Value = request.ContactRequired;
        command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
        command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = request.Offset;
        command.Parameters.Add("@Take", SqlDbType.Int).Value = request.Take;
        command.Parameters.Add("@NormVersion", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
        if (request.Today is { } today) command.Parameters.Add("@Today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
        if (request.MinTotal is { } minTotal) command.Parameters.Add(new SqlParameter("@MinTotal", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = minTotal });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var companies = new List<SnapshotCompanyRaw>();
        var unmatched = new List<UnmatchedTowerDto>();
        var units = new List<CampaignUnitFacts>();
        var missing = new List<(int, byte, string)>();
        int total = 0, clean = 0, review = 0, bad = 0;
        var status = "Ok";
        // 1 scope
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
            throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
        await reader.NextResultAsync(cancellationToken);
        // 2 coverage
        while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
        await reader.NextResultAsync(cancellationToken);
        // 3 header
        if (await reader.ReadAsync(cancellationToken))
        {
            status = reader.GetString(reader.GetOrdinal("Status"));
            bad = reader.GetInt32(reader.GetOrdinal("BadIdentityRows"));
        }
        await reader.NextResultAsync(cancellationToken);
        // 4 totals
        if (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt32(reader.GetOrdinal("TotalUnits"));
            clean = reader.GetInt32(reader.GetOrdinal("CleanUnits"));
            review = reader.GetInt32(reader.GetOrdinal("ReviewUnits"));
        }
        await reader.NextResultAsync(cancellationToken);
        // 5 page
        while (await reader.ReadAsync(cancellationToken))
        {
            var amountOrdinal = reader.GetOrdinal("Amount");
            var dueOrdinal = reader.GetOrdinal("EarliestDue");
            units.Add(new CampaignUnitFacts(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")),
                reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Phone")), reader.GetString(reader.GetOrdinal("Email")),
                (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("ProjectCode")),
                reader.IsDBNull(amountOrdinal) ? null : reader.GetDecimal(amountOrdinal),
                reader.IsDBNull(dueOrdinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(dueOrdinal)),
                reader.GetInt32(reader.GetOrdinal("Flags")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                request.Today is null ? 0m : reader.GetDecimal(reader.GetOrdinal("DueAmount")), request.Today is null ? 0m : reader.GetDecimal(reader.GetOrdinal("OverdueAmount")),
                reader.GetInt32(reader.GetOrdinal("CrmStatus")), NullableInt(reader, "CrmCustomerId")));
        }
        await reader.NextResultAsync(cancellationToken);
        // 6 unmatched towers
        while (await reader.ReadAsync(cancellationToken))
            unmatched.Add(new UnmatchedTowerDto(reader.GetInt32(reader.GetOrdinal("CompanyId")), Text(reader, "TowerNumber"),
                reader.GetString(reader.GetOrdinal("Reason")), reader.GetInt64(reader.GetOrdinal("RowCount_")), reader.GetDecimal(reader.GetOrdinal("Amount"))));
        await reader.NextResultAsync(cancellationToken);
        // 7 contact values that still need normalising
        while (await reader.ReadAsync(cancellationToken))
            missing.Add((reader.GetInt32(0), reader.GetByte(1), reader.GetString(2)));
        return new CampaignRead(status, bad, total, clean, review, companies, unmatched, units, missing);
    }

    /// <summary>
    /// Instalment list: one round trip to dbo.usp_Collections_GetInstalmentsPage (filter, totals and OFFSET/FETCH paging in SQL). Only one page of rows is read.
    /// </summary>
    public async Task<PactInstalmentsPage> ReadInstalmentsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var companies = new List<SnapshotCompanyRaw>();
        var unmatched = new List<UnmatchedTowerDto>();
        var rows = new List<PactInstalmentRowDto>();
        var totals = new PactInstalmentTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var unavailable = false;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetInstalmentsPage", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) };
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            command.Parameters.Add("@PaymentFilter", SqlDbType.VarChar, 12).Value = request.PaymentFilter;
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            command.Parameters.Add("@PageNumber", SqlDbType.Int).Value = request.Page;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = request.PageSize;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totals = new PactInstalmentTotalsDto(reader.GetInt32(reader.GetOrdinal("TotalInstalments")), reader.GetDecimal(reader.GetOrdinal("RemainingTotal")),
                    reader.GetInt32(reader.GetOrdinal("OverdueCount")), reader.GetDecimal(reader.GetOrdinal("OverdueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("DueCount")), reader.GetDecimal(reader.GetOrdinal("DueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("NotYetDueCount")), reader.GetDecimal(reader.GetOrdinal("NotYetDueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("FullyPaidCount")));
                unavailable = reader.GetBoolean(reader.GetOrdinal("Unavailable"));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var company = reader.GetInt32(reader.GetOrdinal("CompanyId"));
                rows.Add(new PactInstalmentRowDto(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("TenantId")),
                    reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))),
                    NullableDecimal(reader, "OriginalAmount"), NullableDecimal(reader, "PaidAmount"), reader.GetDecimal(reader.GetOrdinal("RemainingAmount")),
                    reader.GetString(reader.GetOrdinal("PaymentStatus")), reader.GetString(reader.GetOrdinal("Classification")), Text(reader, "SourceStatus") ?? ""));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                unmatched.Add(new UnmatchedTowerDto(reader.GetInt32(reader.GetOrdinal("CompanyId")), Text(reader, "TowerNumber"),
                    reader.GetString(reader.GetOrdinal("Reason")), reader.GetInt64(reader.GetOrdinal("RowCount_")), reader.GetDecimal(reader.GetOrdinal("Amount"))));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Instalment page read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, companies, unmatched, now, snapshotOptions.MaxAgeMinutes);
        var sqlMs = timer.Elapsed.TotalMilliseconds;
        logger.LogInformation("Instalment page read: page {Page}, {Total} instalments in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, filter {Filter}, min {Min}).",
            request.Page, totals.Count, sqlMs, request.From, request.To, request.PaymentFilter, request.MinAmount);
        return new PactInstalmentsPage(totals, rows, unavailable, status, ReceivablesSnapshotComposer.ReadAt(status), sqlMs);
    }

    /// <summary>
    /// "By unit" view: one round trip to dbo.usp_Collections_GetInstalmentUnitsPage. Units are grouped, counted and paged in SQL; the instalments of exactly the
    /// listed units come back with them and are attached by (company, customer, unit), so a unit is never split over pages.
    /// </summary>
    public async Task<PactInstalmentUnitsPage> ReadInstalmentUnitsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var companies = new List<SnapshotCompanyRaw>();
        var unmatched = new List<UnmatchedTowerDto>();
        var heads = new List<(int Company, string Tenant, long UnitId, string UnitCode, string Name, string? Tower, string? TowerName, int Count, decimal Remaining, DateOnly Oldest,
            int? CrmCustomers, int? CrmCustomerId, string? CrmName, string? CrmPhone, string? CrmEmail)>();
        var byUnit = new Dictionary<(int, string, long, string), List<PactInstalmentRowDto>>();
        var totals = new PactInstalmentTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var unavailable = false;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetInstalmentUnitsPage", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) };
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            command.Parameters.Add("@PaymentFilter", SqlDbType.VarChar, 12).Value = request.PaymentFilter;
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            command.Parameters.Add("@PageNumber", SqlDbType.Int).Value = request.Page;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = request.PageSize;
            // Day-based classification and the status filter exist from V008's day-based revision; they are only sent when asked for.
            if (request.ClassifyByDay) command.Parameters.Add("@ClassifyByDay", SqlDbType.Bit).Value = true;
            if (request.Status is not null) command.Parameters.Add("@StatusFilter", SqlDbType.VarChar, 10).Value = request.Status;
            if (request.MinTotal is { } minTotal) command.Parameters.Add(new SqlParameter("@MinTotal", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = minTotal });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totals = new PactInstalmentTotalsDto(reader.GetInt32(reader.GetOrdinal("TotalInstalments")), reader.GetDecimal(reader.GetOrdinal("RemainingTotal")),
                    reader.GetInt32(reader.GetOrdinal("OverdueCount")), reader.GetDecimal(reader.GetOrdinal("OverdueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("DueCount")), reader.GetDecimal(reader.GetOrdinal("DueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("NotYetDueCount")), reader.GetDecimal(reader.GetOrdinal("NotYetDueRemaining")),
                    reader.GetInt32(reader.GetOrdinal("FullyPaidCount")), reader.GetInt32(reader.GetOrdinal("UnitCount")));
                unavailable = reader.GetBoolean(reader.GetOrdinal("Unavailable"));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                heads.Add((reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")),
                    reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("FullName")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    reader.GetInt32(reader.GetOrdinal("InstalmentCount")), reader.GetDecimal(reader.GetOrdinal("RemainingTotal")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("OldestDueDate"))),
                    NullableInt(reader, "CrmCustomers"), NullableInt(reader, "CrmCustomerId"), Text(reader, "CrmName"), Text(reader, "CrmPhone"), Text(reader, "CrmEmail")));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var company = reader.GetInt32(reader.GetOrdinal("CompanyId"));
                var key = (company, reader.GetString(reader.GetOrdinal("TenantId")), reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")));
                if (!byUnit.TryGetValue(key, out var list)) byUnit[key] = list = [];
                list.Add(new PactInstalmentRowDto(company, ReceivablesSnapshotStatusBuilder.CompanyName(company), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                    (int)key.Item3, key.Item4, key.Item2, reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Mobile")), reader.GetString(reader.GetOrdinal("Email")),
                    reader.GetString(reader.GetOrdinal("VoucherNumber")), reader.GetString(reader.GetOrdinal("ChequeNumber")),
                    DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("DueDate"))),
                    NullableDecimal(reader, "OriginalAmount"), NullableDecimal(reader, "PaidAmount"), reader.GetDecimal(reader.GetOrdinal("RemainingAmount")),
                    reader.GetString(reader.GetOrdinal("PaymentStatus")), reader.GetString(reader.GetOrdinal("Classification")), Text(reader, "SourceStatus") ?? ""));
            }
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                unmatched.Add(new UnmatchedTowerDto(reader.GetInt32(reader.GetOrdinal("CompanyId")), Text(reader, "TowerNumber"),
                    reader.GetString(reader.GetOrdinal("Reason")), reader.GetInt64(reader.GetOrdinal("RowCount_")), reader.GetDecimal(reader.GetOrdinal("Amount"))));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit page read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, companies, unmatched, now, snapshotOptions.MaxAgeMinutes);
        var units = heads.Select(h => new PactInstalmentUnitDto(h.Company, ReceivablesSnapshotStatusBuilder.CompanyName(h.Company), h.Tower, h.TowerName, (int)h.UnitId, h.UnitCode,
            h.Tenant, h.Name, h.Count, h.Remaining, h.Oldest, byUnit.TryGetValue((h.Company, h.Tenant, h.UnitId, h.UnitCode), out var rows) ? rows : [],
            h.CrmCustomers, h.CrmCustomerId, h.CrmName, h.CrmPhone, h.CrmEmail)).ToList();
        var sqlMs = timer.Elapsed.TotalMilliseconds;
        logger.LogInformation("Unit page read: page {Page}, {Units} units / {Total} instalments in {SqlMs:F0} ms (window {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, filter {Filter}, min {Min}).",
            request.Page, totals.UnitCount, totals.Count, sqlMs, request.From, request.To, request.PaymentFilter, request.MinAmount);
        return new PactInstalmentUnitsPage(totals, units, unavailable, status, ReceivablesSnapshotComposer.ReadAt(status), sqlMs);
    }

    /// <summary>
    /// One unit by its normalised key (tower + unit code), read with ONE statement (dbo.usp_Collections_GetUnitReceivables): the PACT accounts that hold it (a missing PACT mobile
    /// changes nothing), the unit's own unpaid instalments due today or earlier, and its CRM link. Nothing is looked up by phone number or name.
    /// </summary>
    public async Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var companies = new List<SnapshotCompanyRaw>();
        var identities = new List<PactUnitIdentity>();
        var instalments = new List<PactUnitInstalment>();
        var links = new Dictionary<int, CrmUnitLink>();
        var crmLoaded = false;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var command = new SqlCommand("dbo.usp_Collections_GetUnitReceivables", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) })
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
                        NullableDecimal(reader, "OriginalAmount"), NullableDecimal(reader, "PaidAmount"), reader.GetString(reader.GetOrdinal("PaymentStatus"))));
                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) links[reader.GetInt32(reader.GetOrdinal("CompanyId"))] = SqlCollectionsCrmOwnerStore.ReadLink(reader);
            }
            await using var state = new SqlCommand("dbo.usp_Collections_GetCrmOwnerState", connection) { CommandType = CommandType.StoredProcedure };
            await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
            crmLoaded = await stateReader.ReadAsync(cancellationToken) && stateReader.GetBoolean(0);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Unit receivables read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = ReceivablesSnapshotComposer.BuildStatus(today, today, companies, [], now, snapshotOptions.MaxAgeMinutes);
        return new PactUnitReceivables(status, identities, instalments, links, crmLoaded, ReceivablesSnapshotComposer.ReadAt(status));
    }

    /// <summary>Month overview: one round trip to dbo.usp_Collections_GetInstalmentMonths (the same filters as the list, no paging, no month selection).</summary>
    public async Task<IReadOnlyList<PactInstalmentMonthDto>> ReadInstalmentMonthsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.CompanyId is not (null or 4 or 32))
            throw new PactReceivablesSourceException("The PACT report company is not supported.");
        var connectionString = configuration.GetConnectionString(snapshotOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The receivables snapshot connection is not configured.");
        var months = new List<PactInstalmentMonthDto>();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_GetInstalmentMonths", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(snapshotOptions.ReadCommandTimeoutSeconds, 1, 600) };
            command.Parameters.Add("@TowerId", SqlDbType.Int).Value = (object?)request.TowerId ?? DBNull.Value;
            command.Parameters.Add("@CompanyId", SqlDbType.Int).Value = (object?)request.CompanyId ?? DBNull.Value;
            command.Parameters.Add("@FromDate", SqlDbType.Date).Value = request.From.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@ToDate", SqlDbType.Date).Value = request.To.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = request.AsOf.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(new SqlParameter("@MinAmount", SqlDbType.Decimal) { Precision = 19, Scale = 4, Value = request.MinAmount });
            command.Parameters.Add("@PaymentFilter", SqlDbType.VarChar, 12).Value = request.PaymentFilter;
            command.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = (object?)request.Search ?? DBNull.Value;
            command.Parameters.Add("@PhoneDigits", SqlDbType.NVarChar, 200).Value = (object?)request.PhoneDigits ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid")))
                throw new PactReceivablesScopeException("The selected tower is not available. Choose a tower from the list.");
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                months.Add(new PactInstalmentMonthDto(reader.GetInt32(reader.GetOrdinal("DueYear")), reader.GetInt32(reader.GetOrdinal("DueMonth")),
                    reader.GetInt32(reader.GetOrdinal("InstalmentCount")), reader.GetDecimal(reader.GetOrdinal("RemainingTotal")),
                    reader.GetInt32(reader.GetOrdinal("OverdueCount")), reader.GetDecimal(reader.GetOrdinal("OverdueRemaining"))));
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Month overview read failed (SQL error {SqlNumber}).", ex.Number);
            throw new PactReceivablesSourceException("The local receivables snapshot could not be read.");
        }
        return months;
    }

    private static int? NullableInt(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static decimal? NullableDecimal(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetDecimal(i); }

    private static SnapshotCompanyRaw ReadCompany(SqlDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetBoolean(reader.GetOrdinal("HasSnapshot")),
        Utc(reader, "LastSuccessUtc"), Utc(reader, "LastAttemptUtc"), Text(reader, "LastAttemptStatus"),
        Int(reader, "LastErrorNumber"), reader.GetInt32(reader.GetOrdinal("ConsecutiveFailures")),
        reader.GetInt32(reader.GetOrdinal("SnapshotRowCount")), Day(reader, "CoverageFromDate"), Day(reader, "CoverageThroughDate"),
        reader.GetInt32(reader.GetOrdinal("ExcludedInvalidUnitRows")), reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidUnitAmount")),
        reader.GetInt32(reader.GetOrdinal("ExcludedInvalidIdentityRows")), reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidIdentityAmount")),
        reader.GetInt32(reader.GetOrdinal("ContradictoryStatusRows")), reader.GetInt32(reader.GetOrdinal("UnknownStatusRows")),
        reader.GetBoolean(reader.GetOrdinal("RefreshInProgress")), Utc(reader, "RefreshStartedUtc"), Text(reader, "RunCompanyStatus"),
        reader.GetBoolean(reader.GetOrdinal("PaidRetained")), reader.GetBoolean(reader.GetOrdinal("BreakdownAvailable")), reader.GetInt32(reader.GetOrdinal("UnclassifiedRows")));

    private static string? Text(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static DateTime? Utc(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc); }
    private static DateOnly? Day(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i)); }
}

public sealed class SqlCollectionsTowerCatalog(IConfiguration configuration, ReceivablesSnapshotOptions options, ILogger<SqlCollectionsTowerCatalog> logger)
    : ICollectionsTowerCatalog
{
    // The tower list changes rarely and is read on every page view: keep it for a minute so the page shell never waits on it twice.
    private static readonly object CacheLock = new();
    private static (DateTime AtUtc, IReadOnlyList<CollectionsTowerDto> Towers)? cache;

    public async Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken)
    {
        lock (CacheLock) { if (cache is { } hit && DateTime.UtcNow - hit.AtUtc < TimeSpan.FromSeconds(60)) return hit.Towers; }
        var towers = await ReadAsync(cancellationToken);
        lock (CacheLock) { cache = (DateTime.UtcNow, towers); }
        return towers;
    }

    private async Task<IReadOnlyList<CollectionsTowerDto>> ReadAsync(CancellationToken cancellationToken)
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
        // The id is ours so that a call that never returns (timeout, cancellation, restart) can still be closed: the procedure's own CATCH does not run then.
        var runId = Guid.NewGuid();
        string? connectionString = null;
        var returned = false; int closeNumber = -1; string closeMessage = "The refresh did not finish.";
        try
        {
            connectionString = configuration.GetConnectionString(options.ConnectionStringName)
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
            command.Parameters.Add("@ProcedureSuffix", SqlDbType.NVarChar, 16).Value = options.SourceProcedureSuffix ?? "";
            command.Parameters.Add("@RetainPaid", SqlDbType.Bit).Value = options.RetainPaidInstalments;
            command.Parameters.Add("@StrictIdentity", SqlDbType.Bit).Value = options.StrictIdentity;
            command.Parameters.Add("@SourceMinAmount", SqlDbType.Int).Value = options.SourceMinAmount;
            command.Parameters.Add("@MaxRawRows", SqlDbType.Int).Value = options.MaxRawRows;
            command.Parameters.Add("@MaxShrinkPercent", SqlDbType.Int).Value = Math.Clamp(options.MaxShrinkPercent, 0, 100);
            command.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = runId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Guid? resultRunId = null; var status = "Failed"; string? message = null;
            if (await reader.ReadAsync(cancellationToken))
            {
                resultRunId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                status = reader.GetString(1);
                message = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            var companies = new List<ReceivablesRefreshCompanyResult>();
            if (await reader.NextResultAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    companies.Add(new ReceivablesRefreshCompanyResult(reader.GetInt32(0), reader.GetString(1),
                        Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4), Nullable(reader, 5), Nullable(reader, 6), Nullable(reader, 7),
                        reader.IsDBNull(8) ? null : reader.GetString(8), Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11)));
            await reader.DisposeAsync();
            returned = true;   // the procedure ran to its end: every run it started has its own terminal state
            if (companies.Any(x => x.Status == "Succeeded"))
            {
                // Best effort: normalise the contact values the new run registered, so the first campaign preview does not have to.
                try { await CollectionsContactNormStore.FillPendingAsync(connectionString, options.ReadCommandTimeoutSeconds, cancellationToken); }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                { logger.LogWarning("Contact normalisation after the refresh was skipped ({ExceptionType}); it will run on the first campaign read.", ex.GetType().Name); }
            }
            return new ReceivablesRefreshResult(resultRunId, status, message, companies);
        }
        catch (SqlException ex)
        {
            closeNumber = ex.Number;
            closeMessage = ex.Number == -2 ? "The refresh exceeded its command timeout and was stopped." : $"The refresh procedure failed (SQL error {ex.Number}).";
            logger.LogError("Receivables refresh procedure failed (SQL error {SqlNumber}).", ex.Number);
            return new ReceivablesRefreshResult(null, "Failed", closeMessage, []);
        }
        catch (OperationCanceledException)
        {
            closeMessage = "The refresh was cancelled (application shutdown or job cancellation).";
            throw;
        }
        catch (Exception ex)
        {
            closeMessage = $"The refresh stopped unexpectedly ({ex.GetType().Name}).";
            throw;
        }
        finally
        {
            if (!returned && connectionString is not null) await CloseUnfinishedRunAsync(connectionString, runId, closeNumber, closeMessage);
            Gate.Release();
        }
    }

    /// <summary>
    /// Gives the run this call started a terminal state (Run, RunCompany and the company's last attempt) when the call ended without a result.
    /// Safe to call always: the procedure only touches rows still 'Running' and does nothing while a refresh still holds the application lock.
    /// A timeout or cancellation does not stop a batch blocked in the linked-server call: its SQL session (and the lock) lives until the remote procedure
    /// returns, so the procedure waits up to <see cref="CloseLockWaitSeconds"/> for it. Never throws; whatever it cannot close is closed by the next refresh under the lock.
    /// </summary>
    private const int CloseLockWaitSeconds = 120;

    private async Task CloseUnfinishedRunAsync(string connectionString, Guid runId, int errorNumber, string message)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CloseLockWaitSeconds + 30));
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cts.Token);
            await using var command = new SqlCommand("dbo.usp_Collections_CloseReceivablesRun", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = CloseLockWaitSeconds + 20 };
            command.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = runId;
            command.Parameters.Add("@ErrorNumber", SqlDbType.Int).Value = errorNumber;
            command.Parameters.Add("@Message", SqlDbType.NVarChar, 1000).Value = message;
            command.Parameters.Add("@RequireIdle", SqlDbType.Bit).Value = true;
            command.Parameters.Add("@LockWaitSeconds", SqlDbType.Int).Value = CloseLockWaitSeconds;
            var outcome = command.Parameters.Add("@Outcome", SqlDbType.VarChar, 20); outcome.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(cts.Token);
            if (outcome.Value is "StillRunning")
                logger.LogWarning("Receivables refresh {RunId} did not return and its SQL session (blocked in the PACT call) still holds the refresh lock after {Wait} s; the next refresh closes it once that session ends.", runId, CloseLockWaitSeconds);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        { logger.LogWarning("Could not close the unfinished receivables refresh {RunId} ({ExceptionType}); the next refresh will.", runId, ex.GetType().Name); }
    }

    private static int? Nullable(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

/// <summary>
/// Fills <c>dbo.CollectionsContactNorm</c> with the application's phone / e-mail normalisations (CollectionsContactNormalizer). The dictionary is a pure
/// function of the raw text, so a stored value stays valid until <see cref="CollectionsContactNormalizer.Version"/> changes.
/// </summary>
public static class CollectionsContactNormStore
{
    private const int Chunk = 2000;   // well below the ~5000-lock escalation threshold: a table lock would stall readers

    public static async Task StoreAsync(string connectionString, IReadOnlyList<(int Id, byte Kind, string Raw)> items, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        for (var offset = 0; offset < items.Count; offset += Chunk)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(items.Skip(offset).Take(Chunk).Select(i => new
            {
                id = i.Id,
                norm = i.Kind == 1 ? CollectionsContactNormalizer.NormalizePhone(i.Raw) : CollectionsContactNormalizer.NormalizeEmail(i.Raw)
            }));
            await using var command = new SqlCommand("dbo.usp_Collections_SetContactNorms", connection)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(timeoutSeconds, 1, 600) };
            command.Parameters.Add("@NormVersion", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
            command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = json;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        // Record the result on the units (also repairs units that lag behind an already normalised dictionary; a no-op when everything is current).
        await using var sync = new SqlCommand("dbo.usp_Collections_SyncUnitContacts", connection)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = Math.Clamp(timeoutSeconds, 1, 600) };
        sync.Parameters.Add("@NormVersion", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
        await sync.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Normalises every registered value that has no up-to-date normalisation yet.</summary>
    public static async Task FillPendingAsync(string connectionString, int timeoutSeconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = new List<(int, byte, string)>();
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(
                    "SELECT TOP (20000) ContactId, Kind, Raw FROM dbo.CollectionsContactNorm WHERE Norm IS NULL OR NormVersion IS NULL OR NormVersion <> @v", connection)
                { CommandTimeout = Math.Clamp(timeoutSeconds, 1, 600) };
                command.Parameters.Add("@v", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) pending.Add((reader.GetInt32(0), reader.GetByte(1), reader.GetString(2)));
            }
            await StoreAsync(connectionString, pending, timeoutSeconds, cancellationToken);   // also syncs the units (a no-op when nothing was pending)
            if (pending.Count < 20000) return;
        }
    }
}
