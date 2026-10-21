using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// The local copy of CRM's unit owners (V010): a complete run is stored in chunks and published atomically, so readers never see a half-loaded feed and a failed
/// load leaves the previous data in place. Links are read in bulk (one statement per few thousand units), never one request per unit.
/// </summary>
public sealed class SqlCollectionsCrmOwnerStore(IConfiguration configuration, ReceivablesSnapshotOptions options) : ICollectionsCrmOwnerStore
{
    private const int Chunk = 1000;

    private string ConnectionString => configuration.GetConnectionString(options.ConnectionStringName)
        ?? throw new InvalidOperationException("The receivables snapshot connection is not configured.");

    public async Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken, string? note = null)
    {
        var run = Guid.NewGuid();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var stage = "opening the connection";
        string? procedure = null;
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var chunks = (rows.Count + Chunk - 1) / Chunk;
            for (var offset = 0; offset < rows.Count; offset += Chunk)
            {
                stage = $"staging chunk {offset / Chunk + 1} of {chunks}";
                procedure = "usp_Collections_StoreCrmUnitOwners";
                var json = JsonSerializer.Serialize(rows.Skip(offset).Take(Chunk).Select(r => new
                {
                    unitKey = r.UnitKey, projectCode = r.ProjectCode, unitNumber = r.UnitNumber, leadId = r.LeadId, leadStatus = r.LeadStatus, customerId = r.CustomerId,
                    unitId = r.UnitId, projectId = r.ProjectId, fullName = r.FullName, mobile = r.Mobile, email = r.Email, phoneNorm = r.PhoneNorm, emailNorm = r.EmailNorm, projectName = r.ProjectName
                }));
                await using var store = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
                store.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = run;
                store.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = json;
                await store.ExecuteNonQueryAsync(cancellationToken);
            }
            stage = "publishing the run";
            procedure = "usp_Collections_PublishCrmUnitOwners";
            await using var publish = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            publish.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = run;
            publish.Parameters.Add("@ExpectedRows", SqlDbType.Int).Value = rows.Count;
            publish.Parameters.Add("@Note", SqlDbType.NVarChar, 1000).Value = (object?)(note is { Length: > 1000 } ? note[..1000] : note) ?? DBNull.Value;
            return Convert.ToInt32(await publish.ExecuteScalarAsync(cancellationToken) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            throw CrmOwnerStorageErrors.FromSql(ex, stage, procedure, connection?.Database, timer.ElapsedMilliseconds, rows.Count);
        }
        catch (InvalidOperationException ex)
        {
            throw CrmOwnerStorageErrors.FromOther(ex, stage, procedure, connection?.Database, timer.ElapsedMilliseconds, rows.Count);
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    public async Task<(int Resolved, int Ambiguous)> ResolveCompaniesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_ResolveCrmOwnerCompanies", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    public async Task RecordFailureAsync(string message, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("dbo.usp_Collections_RecordCrmOwnerFailure", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add("@Message", SqlDbType.NVarChar, 1000).Value = message.Length > 1000 ? message[..1000] : message;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            throw CrmOwnerStorageErrors.FromSql(ex, "recording the failure", "usp_Collections_RecordCrmOwnerFailure", null, timer.ElapsedMilliseconds, 0);
        }
    }

    public async Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_GetCrmOwnerState", connection) { CommandType = CommandType.StoredProcedure };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(false, null, null, null, null, 0, 0);
        return new(reader.GetBoolean(0), reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.FieldCount > 7 ? reader.GetInt32(7) : 0, reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null);
    }

    public async Task<IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>> GetLinksAsync(
        IReadOnlyList<(int CompanyId, string UnitKey)> units, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(int, string), CrmUnitLink>();
        if (units.Count == 0) return result;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var chunk in units.Distinct().Chunk(5000))
        {
            await using var command = new SqlCommand("dbo.usp_Collections_GetCrmUnitLinks", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 };
            command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(chunk.Select(u => new { c = u.CompanyId, k = u.UnitKey }));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result[(reader.GetInt32(0), reader.GetString(1))] = ReadLink(reader);
        }
        return result;
    }

    /// <summary>The columns shared by usp_Collections_GetCrmUnitLinks and result set 4 of usp_Collections_GetUnitReceivables.</summary>
    public static CrmUnitLink ReadLink(SqlDataReader reader)
    {
        var customers = reader.GetInt32(reader.GetOrdinal("Customers"));
        // CompanyAmbiguous: the CRM sale exists but both companies hold the same unit key, so it is tied to neither (V012). Absent column = a server not yet upgraded to V012.
        var companyAmbiguous = false;
        try { var o = reader.GetOrdinal("CompanyAmbiguous"); companyAmbiguous = !reader.IsDBNull(o) && reader.GetBoolean(o); } catch (IndexOutOfRangeException) { }
        return new(customers > 1 ? CrmLinkStatus.Ambiguous : companyAmbiguous ? CrmLinkStatus.CompanyAmbiguous : CrmLinkStatus.Single, customers,
            customers > 1 ? 0 : reader.GetInt32(reader.GetOrdinal("CustomerId")), reader.GetInt32(reader.GetOrdinal("CrmUnitId")), reader.GetInt32(reader.GetOrdinal("LeadId")),
            customers > 1 || companyAmbiguous ? SourceContact.Empty : new SourceContact(reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("PhoneNorm")), reader.GetString(reader.GetOrdinal("EmailNorm"))));
    }
}

/// <summary>Turns a database failure of the CRM owner store into the safe, detailed <see cref="CrmOwnerStorageException"/>. The database's own message text is never copied.</summary>
public static class CrmOwnerStorageErrors
{
    public static CrmOwnerStorageException FromSql(SqlException ex, string stage, string? procedure, string? database, long elapsedMs, int rows) =>
        new(CrmOwnerStorageException.BuildSafeMessage(stage, procedure, ex.Number, ex.Class, ex.State, ex.GetType().Name), stage, procedure, ex.Number, ex.Class, ex.State, ex.LineNumber,
            ex.Server, database, ex.ClientConnectionId, elapsedMs, rows, ex.GetType().Name, ex);

    public static CrmOwnerStorageException FromOther(Exception ex, string stage, string? procedure, string? database, long elapsedMs, int rows) =>
        new(CrmOwnerStorageException.BuildSafeMessage(stage, procedure, null, null, null, ex.GetType().Name), stage, procedure, null, null, null, null, null, database, null, elapsedMs, rows, ex.GetType().Name, ex);
}
