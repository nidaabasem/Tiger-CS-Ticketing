using System.Data;
using Microsoft.Data.SqlClient;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Rcv.Parity;

/// <summary>
/// TEST-ONLY independent reference for <see cref="CampaignParityTests"/>: the published lines of dbo.CollectionsRcvLine read with a plain SELECT (no read procedure) and filtered in C#
/// (window, company, tower, minimum amount, the unit exclusions). The SAME application evaluation (CollectionsCampaignAppService.BuildFactsInMemory, the engine production uses, now with the
/// current-Dubai-month Due / Overdue rule) runs once over this oracle and once over <see cref="TigerCS.Infrastructure.Modules.Collections.RcvReceivablesSource"/>
/// (usp_CollectionsRcv_GetLines), so any difference is a difference in what the SQL read procedure returns. The snapshot status (freshness / coverage) is taken from the real source.
/// Not used by the application.
/// </summary>
internal sealed class PublishedLinesOracle(string connectionString, IPactReceivablesSource statusSource) : IPactReceivablesSource
{
    public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) =>
        ReadAsync(new PactReceivablesRequest(null, throughDate), cancellationToken);

    public async Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
    {
        var from = request.FromDate ?? new DateOnly(2000, 1, 1);
        var real = await statusSource.ReadAsync(new PactReceivablesRequest(from, request.ThroughDate, request.CompanyId, request.TowerId, MinAmount: 1_000_000_000m), cancellationToken);   // status only: no row qualifies
        var rows = new List<PactReceivableInstalment>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string? towerNumber = null;
        if (request.TowerId is { } towerId)
        {
            await using var tower = new SqlCommand("SELECT LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) FROM dbo.CollectionsTowers WHERE TowerId = @id", connection);
            tower.Parameters.AddWithValue("@id", towerId);
            towerNumber = await tower.ExecuteScalarAsync(cancellationToken) as string;
        }
        await using var command = new SqlCommand(
            "SELECT l.CompanyId, l.TenantId, l.FullName, l.Mobile, l.Email, l.UnitId, l.UnitCode, l.ProjectCode, l.VoucherNumber, l.ChequeNumber, l.DueDate, l.Amount, l.SourceStatus, l.TowerNumber, " +
            "       (SELECT TOP (1) CONVERT(nvarchar(400), t.TowerName) FROM dbo.CollectionsTowers t WHERE t.CompanyId = l.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = l.TowerNumber " +
            "         ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) AS TowerName " +
            "  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId " +
            " ORDER BY l.CompanyId, l.TenantId, l.UnitId, l.DueDate, l.VoucherNumber, l.LineId", connection) { CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var company = reader.GetInt32(0); var unitCode = reader.GetString(6); var unitId = reader.GetInt64(5);
            var due = DateOnly.FromDateTime(reader.GetDateTime(10)); var amount = reader.GetDecimal(11);
            var tn = reader.IsDBNull(13) ? null : reader.GetString(13);
            if ((request.CompanyId is { } c && c != company) || (towerNumber is not null && tn != towerNumber)
                || due < from || due > request.ThroughDate || amount <= 0 || amount < request.MinAmount
                || unitId <= 0 || unitCode.Trim() is "" or "0" || unitCode.Contains('*')) continue;
            rows.Add(new PactReceivableInstalment(company, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), (int)unitId, unitCode, reader.GetString(7),
                reader.GetString(8), reader.GetString(9), due.ToDateTime(TimeOnly.MinValue), amount, reader.IsDBNull(12) ? "" : reader.GetString(12), tn, null, reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return real with { Items = rows };
    }
}
