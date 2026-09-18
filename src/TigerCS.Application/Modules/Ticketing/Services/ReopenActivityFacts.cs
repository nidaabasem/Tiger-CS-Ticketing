using System.Globalization;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The machine-readable facts of one reopen, as carried on the
/// <c>TicketWorkflowEvent.Reopened</c> row's Note — the department it moved
/// between, what happened to ownership, the new Resolution SLA deadline, and
/// which reopen cycle this was.
///
/// <para>
/// <b>Why here and not in new columns.</b> Ticket Details has to be able to
/// render "Department: Customer Service → Handover · Resolution SLA due: …"
/// beside the reason, and the approved rule asks for that to reuse the
/// existing lifecycle stores rather than a new activity table. The typed event
/// row already exists, is already append-only, and is already served by
/// <c>GET /api/tickets/{ticketId}/approvals</c>; its Note is documented as
/// short display context. So the facts ride there, and — because the entity
/// rightly says consumers must never key on free text — writer and reader
/// share this one formatter/parser instead of each inventing a string format.
/// </para>
///
/// <para>
/// <b>Bounded by construction.</b> Only identifiers, a flag and one
/// round-tripped timestamp are written — never the caller's reason, which
/// lives in the <c>TicketStatusHistory</c> row where free text belongs. That
/// keeps the note comfortably inside the column's 500-character limit whatever
/// the agent typed.
/// </para>
/// </summary>
/// <param name="FromDepartmentId">The department the ticket was closed in.</param>
/// <param name="ToDepartmentId">The department that took the reopened work on. May equal <paramref name="FromDepartmentId"/>.</param>
/// <param name="PreviousOwnerEmployeeId">Who owned it at closure; null if it was already unassigned.</param>
/// <param name="ResultingOwnerEmployeeId">Who the assignment automation landed on; null means the department queue.</param>
/// <param name="ResolutionSlaDueAtUtc">The new Resolution cycle's deadline; null when the ticket has no SLA clock.</param>
/// <param name="ReopenCount">Which reopen this was — 1 for the first.</param>
public sealed record ReopenActivityFacts(
    int FromDepartmentId,
    int ToDepartmentId,
    Guid? PreviousOwnerEmployeeId,
    Guid? ResultingOwnerEmployeeId,
    DateTime? ResolutionSlaDueAtUtc,
    int ReopenCount)
{
    private const string DepartmentQueue = "DepartmentQueue";

    public string Format() => string.Join(';',
        $"FromDepartmentId={FromDepartmentId.ToString(CultureInfo.InvariantCulture)}",
        $"ToDepartmentId={ToDepartmentId.ToString(CultureInfo.InvariantCulture)}",
        $"PreviousOwner={PreviousOwnerEmployeeId?.ToString() ?? DepartmentQueue}",
        $"ResultingOwner={ResultingOwnerEmployeeId?.ToString() ?? DepartmentQueue}",
        $"ResolutionSlaDueAtUtc={(ResolutionSlaDueAtUtc is { } due ? due.ToString("O", CultureInfo.InvariantCulture) : "None")}",
        $"ReopenCount={ReopenCount.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Reads back a note this type wrote. Returns false for anything else —
    /// including a note from a future version carrying fields this one does not
    /// know — so a display surface degrades to showing the reopen without its
    /// detail line rather than throwing at a user.
    /// </summary>
    public static bool TryParse(string? note, out ReopenActivityFacts facts)
    {
        facts = null!;
        if (string.IsNullOrWhiteSpace(note))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in note.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                fields[part[..separator]] = part[(separator + 1)..];
            }
        }

        if (!TryInt(fields, "FromDepartmentId", out var from)
            || !TryInt(fields, "ToDepartmentId", out var to)
            || !TryInt(fields, "ReopenCount", out var reopenCount))
        {
            return false;
        }

        facts = new ReopenActivityFacts(
            from, to, ParseOwner(fields, "PreviousOwner"), ParseOwner(fields, "ResultingOwner"),
            ParseDue(fields), reopenCount);
        return true;
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> fields, string key, out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Guid? ParseOwner(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var raw) && Guid.TryParse(raw, out var owner) ? owner : null;

    private static DateTime? ParseDue(IReadOnlyDictionary<string, string> fields) =>
        fields.TryGetValue("ResolutionSlaDueAtUtc", out var raw)
        && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var due)
            ? due
            : null;
}
