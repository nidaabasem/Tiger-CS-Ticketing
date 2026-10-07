using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>Read-only report procedures on PACTRPT; never writes to TigerCS or PACT.</summary>
public sealed class PactSqlReceivablesSource(
    IConfiguration configuration, PactReceivablesOptions options, TimeProvider timeProvider) : IPactReceivablesSource
{
    public async Task<PactReceivablesSnapshot> ReadAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The PACT report connection is not configured.");

        var endDate = businessDate.ToDateTime(TimeOnly.MinValue).AddDays(1).AddMilliseconds(-3);
        HashSet<(string UnitCode, DateTime DueDate)> downPayments = [];
        if (options.ApplyLegacyExclusions)
        {
            // Do not silently remove the original CRM filter when migrating this function.
            if (!options.SpecialCasesConfigured)
                throw new PactReceivablesSourceException("The original special-case unit exclusions have not been configured.");
            downPayments = await ReadDownPaymentsAsync(cancellationToken);
        }

        var rows = new List<PactReceivableInstalment>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var (company, procedure) in new[] { (4, "dbo.p4AccountReceivables"), (32, "dbo.p32AccountReceivables") })
        {
            using var command = new SqlCommand(procedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = Math.Clamp(options.CommandTimeoutSeconds, 1, 300)
            };
            command.Parameters.Add("@StartDate", SqlDbType.DateTime).Value = new DateTime(2000, 1, 1);
            command.Parameters.Add("@EndDate", SqlDbType.DateTime).Value = endDate;
            // Include sub-dirham balances; only positive amounts survive in the service.
            command.Parameters.Add("@MinAmount", SqlDbType.Int).Value = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "CompanyID", "TenantID", "FullName", "Mobile", "Email", "UnitID", "UnitCode", "VoucherNumber", "ChequeNumber", "DueDate", "Amount", "Status" })
                if (!columns.ContainsKey(name))
                    throw new PactReceivablesSourceException("The PACT report result does not match the expected column contract.");

            string Text(string name) => columns.TryGetValue(name, out var index) && !reader.IsDBNull(index)
                ? Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "" : "";
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(columns["DueDate"]) || reader.IsDBNull(columns["Amount"]))
                    throw new PactReceivablesSourceException("The PACT report returned an instalment without a date or amount.");
                var rowCompany = Convert.ToInt32(reader.GetValue(columns["CompanyID"]), CultureInfo.InvariantCulture);
                if (rowCompany != company)
                    throw new PactReceivablesSourceException("The PACT report returned an unexpected company identity.");
                rows.Add(new PactReceivableInstalment(rowCompany, Text("TenantID"), Text("FullName"), Text("Mobile"), Text("Email"),
                    reader.IsDBNull(columns["UnitID"]) ? null : Convert.ToInt32(reader.GetValue(columns["UnitID"]), CultureInfo.InvariantCulture),
                    Text("UnitCode"), Text("ProjectCode"), Text("VoucherNumber"), Text("ChequeNumber"),
                    reader.GetDateTime(columns["DueDate"]), Convert.ToDecimal(reader.GetValue(columns["Amount"]), CultureInfo.InvariantCulture), Text("Status")));
                if (rows.Count > Math.Clamp(options.MaxSourceRows, 1, 1000000))
                    throw new PactReceivablesSourceException("The PACT report exceeded the configured source-row limit; no partial list was returned.");
            }
        }

        if (options.ApplyLegacyExclusions)
            rows = ApplyExclusions(rows, downPayments, options).ToList();
        return new PactReceivablesSnapshot(rows, timeProvider.GetUtcNow().UtcDateTime, options.ApplyLegacyExclusions);
    }

    private async Task<HashSet<(string UnitCode, DateTime DueDate)>> ReadDownPaymentsAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(options.CrmConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new PactReceivablesSourceException("The CRM booking/down-payment exclusion connection is not configured.");
        var keys = new HashSet<(string UnitCode, DateTime DueDate)>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // The supplied EDSM method only executes its Booking & Downpayment branch.
        // Its Handover branches are commented out and TotDate is not referenced:
        // keep the original absence of an upper date/project filter.
        using var command = new SqlCommand("""
            SELECT TRIM(TRIM(l.ProjectCode) + '-' + TRIM(l.UnitNumber)) AS UnitCode,
                   p.Amount, p.DueDate
            FROM tblPayment p
            INNER JOIN tblLead l ON l.ID = p.LeadID
            WHERE p.[Type] IN (2, 3)
              AND l.Status != 6
              AND p.DueDate >= @FromDate
            """, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = Math.Clamp(options.CommandTimeoutSeconds, 1, 300)
        };
        command.Parameters.Add("@FromDate", SqlDbType.DateTime).Value = new DateTime(2025, 11, 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, x => x, StringComparer.OrdinalIgnoreCase);
        if (!columns.TryGetValue("UnitCode", out var unit) || !columns.TryGetValue("DueDate", out var date))
            throw new PactReceivablesSourceException("The CRM exclusion result does not match the expected column contract.");
        var rowCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(unit) || reader.IsDBNull(date))
                throw new PactReceivablesSourceException("The CRM exclusions returned a row without a unit or date.");
            keys.Add((reader.GetString(unit), reader.GetDateTime(date)));
            if (++rowCount > Math.Clamp(options.MaxSourceRows, 1, 1000000))
                throw new PactReceivablesSourceException("The CRM exclusions exceeded the source-row limit.");
        }
        return keys;
    }

    /// <summary>Exact date matching and unit-level PDC exclusions reproduce the supplied EDSM function.</summary>
    public static IEnumerable<PactReceivableInstalment> ApplyExclusions(
        IEnumerable<PactReceivableInstalment> rows, IReadOnlySet<(string UnitCode, DateTime DueDate)> downPayments,
        PactReceivablesOptions options)
    {
        var special = options.SpecialCaseUnitCodes.ToHashSet(StringComparer.Ordinal);
        var pdc = options.PdcExcludedUnitCodes.ToHashSet(StringComparer.Ordinal);
        return rows.Where(r => !downPayments.Contains((r.UnitCode, r.DueDate))
            && !(r.UnitCode.StartsWith("TP121", StringComparison.Ordinal) && r.DueDate > new DateTime(2025, 12, 15))
            && !(r.UnitCode.StartsWith("TP122", StringComparison.Ordinal) && r.DueDate > new DateTime(2026, 2, 20))
            && !(r.UnitCode.StartsWith("TP123", StringComparison.Ordinal) && r.DueDate > new DateTime(2026, 3, 20))
            && !new[] { "TP103", "TP102", "TP104", "TP101" }.Any(prefix => r.UnitCode.StartsWith(prefix, StringComparison.Ordinal))
            && !pdc.Contains(r.UnitCode) && !r.FullName.Contains('*') && !special.Contains(r.UnitCode));
    }
}
