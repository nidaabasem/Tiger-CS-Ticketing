using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>The tower dropdown: the local <c>dbo.CollectionsTowers</c> table through <c>dbo.usp_CollectionsRcv_GetTowers</c> (no PACT access, no invented towers).</summary>
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
            await using var command = new SqlCommand("dbo.usp_CollectionsRcv_GetTowers", connection)
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
