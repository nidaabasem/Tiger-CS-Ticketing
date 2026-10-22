using System.Text.RegularExpressions;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Turns the stored failure of a receivables refresh (SQL error number + message, how far the run got) into text that is safe to show on a page:
/// it names the phase and the kind of problem, and never carries server names, addresses, logins or SQL text. Messages written by the refresh procedure itself
/// (error numbers 50010-50013) and by the application are our own wording and are shown as they are.
/// </summary>
public static class SnapshotFailureText
{
    /// <summary>Error numbers THROWn by <c>usp_CollectionsRcv_Refresh</c> with our own message.</summary>
    private static readonly HashSet<int> OwnMessages = [50010, 50011, 50012, 50013];

    private static readonly Regex Address = new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>The message to store on the status: our own wording only; any other (driver / server) message is dropped because it can name servers and logins.</summary>
    public static string? Sanitize(int? number, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var trimmed = message.Trim();
        var own = (number is { } n && OwnMessages.Contains(n))
            || trimmed.StartsWith("The refresh ", StringComparison.Ordinal) || trimmed.StartsWith("The connection to SQL Server was lost", StringComparison.Ordinal)
            || trimmed.StartsWith("Superseded:", StringComparison.Ordinal);
        return own ? Address.Replace(trimmed, "[address]") : null;
    }

    /// <summary>Which step of the refresh failed, from what the run recorded: no fetch time = the PACT call did not finish; our validation errors; otherwise the publish.</summary>
    public static string Phase(int? number, int? fetchMs) => number switch
    {
        50010 or 50011 or 50012 => "validating the PACT result",
        50013 => "publishing the snapshot",
        _ => fetchMs is null ? "reading from PACT" : "publishing the snapshot"
    };

    public static string Kind(int? number) => number switch
    {
        -2 => "the command timed out",
        1205 => "SQL Server chose the refresh as a deadlock victim",
        7399 or 7302 or 7303 or 7357 or 7314 => "the linked server to PACT is not usable",
        18456 or 4060 or 229 or 230 => "a SQL login or permission was refused",
        -1 or 53 or 121 or 233 or 10053 or 10054 or 10060 => "the connection to SQL Server was lost",
        213 or 8152 or 245 or 8114 or 207 => "the PACT procedure returned a result that does not match the expected layout",
        50010 => "PACT returned more rows than the configured limit",
        50011 => "PACT returned rows of another company",
        50012 => "the new result is much smaller than the published snapshot",
        50013 => "the stored row count did not match the validated count",
        null => "the refresh ended without a result (cancelled, timed out or the application restarted)",
        _ => $"SQL error {number}"
    };

    public static string Describe(int? number, string? message, int? fetchMs, int? rawRows)
    {
        var text = $"The last refresh failed while {Phase(number, fetchMs)}: {Kind(number)}.";
        if (rawRows is > 0 && number is 50012) text += $" PACT returned {rawRows:N0} rows.";
        if (Sanitize(number, message) is { } own && !text.Contains(own, StringComparison.Ordinal)) text += " " + own;
        return text;
    }
}
