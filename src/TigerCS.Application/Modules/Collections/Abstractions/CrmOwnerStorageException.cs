namespace TigerCS.Application.Modules.Collections.Abstractions;

/// <summary>
/// Storing the CRM owner feed in the local database failed. <see cref="Exception.Message"/> is the SAFE text saved to <c>CollectionsCrmOwnerState.LastError</c> and shown on the pages:
/// stage, procedure, SQL error number / class / state and a fixed description of the number. It never contains the database's own error text (which can echo a customer value, for example
/// "Truncated value: ..."), a connection string, a password or any CRM data. <see cref="LogDetail"/> adds the connection details for the API log only.
/// </summary>
public sealed class CrmOwnerStorageException(
    string safeMessage, string stage, string? procedure, int? sqlNumber, int? sqlClass, int? sqlState, int? lineNumber,
    string? server, string? database, Guid? clientConnectionId, long elapsedMs, int rows, string exceptionType, Exception? inner = null) : Exception(safeMessage, inner)
{
    public string Stage { get; } = stage;
    public string? Procedure { get; } = procedure;
    public int? SqlNumber { get; } = sqlNumber;
    public int? SqlClass { get; } = sqlClass;
    public int? SqlState { get; } = sqlState;
    public string ExceptionType { get; } = exceptionType;

    /// <summary>Everything needed to find the failure in the SQL Server log and on the network path. Log only: it names the server and database, never the password or any data value.</summary>
    public string LogDetail =>
        $"stage={Stage}; procedure={Procedure ?? "(none)"}; sqlNumber={SqlNumber?.ToString() ?? "(none)"}; class={SqlClass?.ToString() ?? "(none)"}; state={SqlState?.ToString() ?? "(none)"}; "
        + $"line={lineNumber?.ToString() ?? "(none)"}; exceptionType={ExceptionType}; server={server ?? "(unknown)"}; database={database ?? "(unknown)"}; "
        + $"clientConnectionId={clientConnectionId?.ToString() ?? "(none)"}; rows={rows}; elapsedMs={elapsedMs}";

    /// <summary>A fixed, data-free explanation of a SQL error number. Anything not listed says to read the API log instead of guessing.</summary>
    public static string Describe(int number) => number switch
    {
        -1 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 10061 => "the connection to SQL Server was lost or could not be established",
        -2 => "a command timed out",
        1205 => "the statement was chosen as a deadlock victim",
        2601 or 2627 => "a duplicate key was rejected",
        2628 or 8152 => "a value was too long for its column (the column is in the API log)",
        515 => "a required value was missing",
        207 or 208 or 2812 => "a database object was not found (is the CRM owner script V010 / V012 applied?)",
        229 or 262 or 300 => "the database user lacks permission for the procedure",
        50040 => "the staged row count did not match the expected count, so nothing was published",
        _ => "see the API log for the SQL error detail"
    };

    public static string BuildSafeMessage(string stage, string? procedure, int? number, int? sqlClass, int? state, string exceptionType)
    {
        var where = string.IsNullOrEmpty(procedure) ? stage : $"{stage} ({procedure})";
        var text = number is { } n
            ? $"The CRM owners could not be stored: {where} failed with SQL error {n} (class {sqlClass}, state {state}): {Describe(n)}."
            : $"The CRM owners could not be stored: {where} failed ({exceptionType}).";
        return text + " The previous CRM data was kept.";
    }
}
