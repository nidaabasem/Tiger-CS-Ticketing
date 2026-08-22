using System.Globalization;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Renders the FR-NOT-01 acknowledgement body.
///
/// <para>
/// <b>Approved fields only, and the list is shorter than either source asks
/// for — deliberately.</b> FR-NOT-01 specifies "ticket number, expected
/// response time, assigned department, and Geyness reference". Two of those
/// four could not be honoured as written, and neither gap was papered over:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <description>
/// <b>"Geyness reference" is omitted (HIGH, reported).</b> ISSUE-003 resolved
/// "Geyness" as the vendor name for the Genesys platform, so the only
/// identifier that could plausibly be meant is
/// <c>GenesysInteractions.ConversationId</c> — and no Genesys table exists in
/// the merged schema, the module is out of this increment's scope, and a
/// phone-intake ticket created without a Genesys interaction would have no
/// such value regardless. Inventing a reference number the customer could
/// quote back to an agent who cannot look it up would be worse than omitting
/// it. <b>A decision is required</b> on what this field actually is.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><c>RequestSummary</c> and ticket status are omitted.</b> They appear in
/// this increment's brief gated on "if disclosure is allowed" / "only if
/// approved", and no merged document grants either approval. Both are one
/// small change away once granted.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// What ships is the uncontested intersection: ticket number, creation
/// timestamp, assigned department, and the expected first-response time. No
/// internal note, audit record, assignee, escalation level or authorization
/// information appears anywhere — a customer-facing message carries only what
/// the customer is entitled to see.
/// </para>
///
/// <para>
/// <b>UTC, labelled as such</b> (NFR-UAE-01 — the system stores and reasons
/// in UTC). Rendering into <c>Asia/Dubai</c> local time for the customer would
/// read better, but no merged document specifies a customer-facing display
/// zone or format, and silently shifting a contractual "expected response
/// time" by four hours is not a formatting choice to make unilaterally.
/// Flagged rather than guessed.
/// </para>
/// </summary>
public static class TicketAcknowledgementContent
{
    public static string Subject(string ticketNumber) =>
        $"Tiger Group Customer Service — we have received your request ({ticketNumber})";

    public static string Body(
        string ticketNumber,
        DateTime createdAtUtc,
        string departmentName,
        DateTime? expectedFirstResponseAtUtc)
    {
        var lines = new List<string>
        {
            "Thank you for contacting Tiger Group Customer Service.",
            string.Empty,
            "We have logged your request and it is now with the team below.",
            string.Empty,
            $"Ticket number: {ticketNumber}",
            $"Logged at: {Format(createdAtUtc)}",
            $"Handled by: {departmentName}"
        };

        // Absent only for a ticket whose SLA clock has not started — a
        // provisional ticket awaiting CRM reconciliation (FR-TKT-09). Stating
        // no time is correct there; stating a made-up one would not be.
        if (expectedFirstResponseAtUtc is { } expected)
        {
            lines.Add($"Expected first response by: {Format(expected)}");
        }

        lines.AddRange(
        [
            string.Empty,
            "This is an automated acknowledgement confirming receipt. A member of the team will follow up with you directly.",
            string.Empty,
            "Please do not reply to this message."
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
