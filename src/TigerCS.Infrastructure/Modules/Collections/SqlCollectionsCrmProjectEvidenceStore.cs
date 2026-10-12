using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>Writes <c>dbo.usp_Collections_RecordCrmProjectEvidence</c> (V013). Evidence only; it never creates a mapping.</summary>
public sealed class SqlCollectionsCrmProjectEvidenceStore(IConfiguration configuration, ReceivablesSnapshotOptions options) : ICollectionsCrmProjectEvidenceStore
{
    public async Task RecordAsync(IReadOnlyList<CrmProjectEvidence> evidence, CancellationToken cancellationToken)
    {
        if (evidence.Count == 0) return;
        await using var connection = new SqlConnection(configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException("The receivables snapshot connection is not configured."));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.usp_Collections_RecordCrmProjectEvidence", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.Add("@Json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(evidence.Select(e => new
        {
            projectId = e.CrmProjectId, projectCode = e.ProjectCode, companyId = e.CompanyId, crmUnitId = e.CrmUnitId, crmCustomerId = e.CrmCustomerId,
            unitNumber = e.UnitNumber, tenantId = e.PactTenantId, pactUnitCode = e.PactUnitCode,
        }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
