using System.Data;
using Microsoft.Data.SqlClient;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Rcv.Parity;

/// <summary>
/// TEST-ONLY port of the PREVIOUS pipeline's Campaigns engine read (dbo.usp_Collections_GetCampaignUnits over dbo.CollectionsReceivable*; both still deployed on the local database). It exists so
/// <see cref="CampaignParityTests"/> can run the SAME scenarios through the old SQL evaluation and through the new application evaluation over the SAME PACT data and compare the results.
/// Not used by the application. Copied from the retired SnapshotPactReceivablesSource.ReadCampaignAsync (git history, commit fbbdae5).
/// </summary>
internal sealed class LegacyCampaignEngine(string connectionString) : IPactReceivablesSource, IPactCampaignSource
{
    private const int Timeout = 120;
    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("the legacy engine answers campaigns itself");

    public async Task<PactCampaignPage> ReadCampaignAsync(PactCampaignRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var read = await ReadOnceAsync(request, cancellationToken);
            if (read.Status == "NeedsNorm" && attempt < 3) { await CollectionsContactNormStore.StoreAsync(connectionString, read.Missing, Timeout, cancellationToken); continue; }
            if (read.Status == "NeedsNorm") throw new PactReceivablesSourceException("The campaign contact values could not be prepared.");
            var status = ReceivablesSnapshotComposer.BuildStatus(request.From, request.To, read.Companies, [], DateTime.UtcNow, 100000);
            if (read.Status == "LegacyRequired") return new PactCampaignPage(false, 0, 0, 0, 0, [], ReceivablesSnapshotComposer.ReadAt(status), status, 0);
            return new PactCampaignPage(true, read.BadIdentityRows, read.Total, read.Clean, read.Review, read.Units, ReceivablesSnapshotComposer.ReadAt(status), status, 0);
        }
    }

    private sealed record Read(string Status, int BadIdentityRows, int Total, int Clean, int Review, List<SnapshotCompanyRaw> Companies, List<CampaignUnitFacts> Units, List<(int Id, byte Kind, string Raw)> Missing);

    private async Task<Read> ReadOnceAsync(PactCampaignRequest request, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_GetCampaignUnits", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = Timeout };
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
        var companies = new List<SnapshotCompanyRaw>(); var units = new List<CampaignUnitFacts>(); var missing = new List<(int, byte, string)>();
        int total = 0, clean = 0, review = 0, bad = 0; var status = "Ok";
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(reader.GetOrdinal("ScopeValid"))) throw new PactReceivablesScopeException("The selected tower is not available.");
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) companies.Add(ReadCompany(reader));
        await reader.NextResultAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken)) { status = reader.GetString(reader.GetOrdinal("Status")); bad = reader.GetInt32(reader.GetOrdinal("BadIdentityRows")); }
        await reader.NextResultAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken)) { total = reader.GetInt32(reader.GetOrdinal("TotalUnits")); clean = reader.GetInt32(reader.GetOrdinal("CleanUnits")); review = reader.GetInt32(reader.GetOrdinal("ReviewUnits")); }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var amountOrdinal = reader.GetOrdinal("Amount"); var dueOrdinal = reader.GetOrdinal("EarliestDue");
            units.Add(new CampaignUnitFacts(reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetString(reader.GetOrdinal("TenantId")),
                reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("Phone")), reader.GetString(reader.GetOrdinal("Email")),
                (int)reader.GetInt64(reader.GetOrdinal("UnitId")), reader.GetString(reader.GetOrdinal("UnitCode")), reader.GetString(reader.GetOrdinal("ProjectCode")),
                reader.IsDBNull(amountOrdinal) ? null : reader.GetDecimal(amountOrdinal), reader.IsDBNull(dueOrdinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(dueOrdinal)),
                reader.GetInt32(reader.GetOrdinal("Flags")), Text(reader, "TowerNumber"), Text(reader, "TowerName"),
                request.Today is null ? 0m : reader.GetDecimal(reader.GetOrdinal("DueAmount")), request.Today is null ? 0m : reader.GetDecimal(reader.GetOrdinal("OverdueAmount")),
                reader.GetInt32(reader.GetOrdinal("CrmStatus")), NullableInt(reader, "CrmCustomerId")));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }   // unmatched towers
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) missing.Add((reader.GetInt32(0), reader.GetByte(1), reader.GetString(2)));
        return new Read(status, bad, total, clean, review, companies, units, missing);
    }

    private static SnapshotCompanyRaw ReadCompany(SqlDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("CompanyId")), reader.GetBoolean(reader.GetOrdinal("HasSnapshot")), Utc(reader, "LastSuccessUtc"), Utc(reader, "LastAttemptUtc"), Text(reader, "LastAttemptStatus"),
        Int(reader, "LastErrorNumber"), reader.GetInt32(reader.GetOrdinal("ConsecutiveFailures")), reader.GetInt32(reader.GetOrdinal("SnapshotRowCount")), Day(reader, "CoverageFromDate"), Day(reader, "CoverageThroughDate"),
        reader.GetInt32(reader.GetOrdinal("ExcludedInvalidUnitRows")), reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidUnitAmount")), reader.GetInt32(reader.GetOrdinal("ExcludedInvalidIdentityRows")),
        reader.GetDecimal(reader.GetOrdinal("ExcludedInvalidIdentityAmount")), reader.GetInt32(reader.GetOrdinal("ContradictoryStatusRows")), reader.GetInt32(reader.GetOrdinal("UnknownStatusRows")),
        reader.GetBoolean(reader.GetOrdinal("RefreshInProgress")), Utc(reader, "RefreshStartedUtc"), Text(reader, "RunCompanyStatus"),
        reader.GetBoolean(reader.GetOrdinal("PaidRetained")), reader.GetBoolean(reader.GetOrdinal("BreakdownAvailable")), reader.GetInt32(reader.GetOrdinal("UnclassifiedRows")));

    private static string? Text(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? Int(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture); }
    private static int? NullableInt(SqlDataReader r, string name) => Int(r, name);
    private static DateTime? Utc(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc); }
    private static DateOnly? Day(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i)); }
}

/// <summary>TEST-ONLY copy of the retired contact-normalisation dictionary filler the previous engine needed (see <see cref="LegacyCampaignEngine"/>).</summary>
internal static class CollectionsContactNormStore
{
    private const int Chunk = 2000;

    public static async Task StoreAsync(string connectionString, IReadOnlyList<(int Id, byte Kind, string Raw)> items, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        for (var offset = 0; offset < items.Count; offset += Chunk)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(items.Skip(offset).Take(Chunk).Select(i => new
            { id = i.Id, norm = i.Kind == 1 ? CollectionsContactNormalizer.NormalizePhone(i.Raw) : CollectionsContactNormalizer.NormalizeEmail(i.Raw) }));
            await using var command = new SqlCommand("dbo.usp_Collections_SetContactNorms", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = timeoutSeconds };
            command.Parameters.Add("@NormVersion", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
            command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = json;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var sync = new SqlCommand("dbo.usp_Collections_SyncUnitContacts", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = timeoutSeconds };
        sync.Parameters.Add("@NormVersion", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
        await sync.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task FillPendingAsync(string connectionString, int timeoutSeconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = new List<(int, byte, string)>();
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("SELECT TOP (20000) ContactId, Kind, Raw FROM dbo.CollectionsContactNorm WHERE Norm IS NULL OR NormVersion IS NULL OR NormVersion <> @v", connection) { CommandTimeout = timeoutSeconds };
                command.Parameters.Add("@v", SqlDbType.TinyInt).Value = CollectionsContactNormalizer.Version;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) pending.Add((reader.GetInt32(0), reader.GetByte(1), reader.GetString(2)));
            }
            await StoreAsync(connectionString, pending, timeoutSeconds, cancellationToken);
            if (pending.Count < 20000) return;
        }
    }
}
