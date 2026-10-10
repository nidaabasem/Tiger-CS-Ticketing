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

    public async Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken)
    {
        var run = Guid.NewGuid();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        for (var offset = 0; offset < rows.Count; offset += Chunk)
        {
            var json = JsonSerializer.Serialize(rows.Skip(offset).Take(Chunk).Select(r => new
            {
                unitKey = r.UnitKey, projectCode = r.ProjectCode, unitNumber = r.UnitNumber, leadId = r.LeadId, leadStatus = r.LeadStatus, customerId = r.CustomerId,
                unitId = r.UnitId, projectId = r.ProjectId, fullName = r.FullName, mobile = r.Mobile, email = r.Email, phoneNorm = r.PhoneNorm, emailNorm = r.EmailNorm
            }));
            await using var store = new SqlCommand("dbo.usp_Collections_StoreCrmUnitOwners", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            store.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = run;
            store.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = json;
            await store.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var publish = new SqlCommand("dbo.usp_Collections_PublishCrmUnitOwners", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        publish.Parameters.Add("@RunId", SqlDbType.UniqueIdentifier).Value = run;
        publish.Parameters.Add("@ExpectedRows", SqlDbType.Int).Value = rows.Count;
        return Convert.ToInt32(await publish.ExecuteScalarAsync(cancellationToken) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task RecordFailureAsync(string message, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_RecordCrmOwnerFailure", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@Message", SqlDbType.NVarChar, 1000).Value = message.Length > 1000 ? message[..1000] : message;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_GetCrmOwnerState", connection) { CommandType = CommandType.StoredProcedure };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(false, null, null, null, null, 0, 0);
        return new(reader.GetBoolean(0), reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6));
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
        return new(customers > 1 ? CrmLinkStatus.Ambiguous : CrmLinkStatus.Single, customers,
            customers > 1 ? 0 : reader.GetInt32(reader.GetOrdinal("CustomerId")), reader.GetInt32(reader.GetOrdinal("CrmUnitId")), reader.GetInt32(reader.GetOrdinal("LeadId")),
            customers > 1 ? SourceContact.Empty : new SourceContact(reader.GetString(reader.GetOrdinal("FullName")), reader.GetString(reader.GetOrdinal("PhoneNorm")), reader.GetString(reader.GetOrdinal("EmailNorm"))));
    }
}
