using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>Reads <c>dbo.usp_Collections_GetCrmProjectMap</c> (V011): the verified manual rows plus the project/code pairs of the current CRM owner feed. One statement for any number of projects.</summary>
public sealed class SqlCollectionsCrmProjectMap(IConfiguration configuration, ReceivablesSnapshotOptions options) : ICollectionsCrmProjectMap
{
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>>> GetAsync(IReadOnlyCollection<int> crmProjectIds, CancellationToken cancellationToken)
    {
        var rows = new List<CrmProjectTowerMapping>();
        if (crmProjectIds.Count == 0) return new Dictionary<int, IReadOnlyList<CrmProjectTowerMapping>>();
        await using var connection = new SqlConnection(configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException("The receivables snapshot connection is not configured."));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_GetCrmProjectMap", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(crmProjectIds.Distinct());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetInt32(0), reader.GetString(1).Trim(), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3)));
        return rows.GroupBy(r => r.CrmProjectId).ToDictionary(g => g.Key, g => (IReadOnlyList<CrmProjectTowerMapping>)g.ToList());
    }
}
