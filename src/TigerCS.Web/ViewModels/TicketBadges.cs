using System.Globalization;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.ViewModels;

/// <summary>
/// Maps the API's status strings onto the badges the UI shows.
///
/// <para>
/// The value sets here are the fixed, approved enumerations documented on the
/// API's own DTOs - <c>TicketStatus</c>, <c>VerificationStatus</c>,
/// <c>PriorityId</c> (1=Critical, 2=High, 3=Medium, 4=Low), <c>SlaState</c>
/// and <c>EscalationLevel</c>. They are transcribed, not invented, and an
/// unrecognised value falls through to a neutral badge showing the raw value
/// rather than being hidden or guessed at.
/// </para>
/// </summary>
public static class TicketBadges
{
    /// <summary>1=Critical, 2=High, 3=Medium, 4=Low.</summary>
    public static BadgeSpec Priority(byte priorityId) => priorityId switch
    {
        1 => new BadgeSpec("Critical", "critical", "▲", "Priority: Critical, the highest"),
        2 => new BadgeSpec("High", "high", "▲", "Priority: High"),
        3 => new BadgeSpec("Medium", "medium", null, "Priority: Medium"),
        4 => new BadgeSpec("Low", "low", null, "Priority: Low"),
        _ => new BadgeSpec($"Priority {priorityId}", "neutral", null, "Priority not recognised")
    };

    /// <summary>The name of a priority, for filter labels and pickers.</summary>
    public static string PriorityName(byte priorityId) => priorityId switch
    {
        1 => "Critical",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => priorityId.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>The fixed priority list, highest urgency first.</summary>
    public static IReadOnlyList<(byte Id, string Name)> Priorities { get; } =
        [(1, "Critical"), (2, "High"), (3, "Medium"), (4, "Low")];

    /// <summary>Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</summary>
    public static BadgeSpec Status(string ticketStatus) => ticketStatus switch
    {
        "Open" => new BadgeSpec("Open", "navy"),
        "InProgress" => new BadgeSpec("In progress", "medium"),
        "PendingCustomer" => new BadgeSpec("Pending customer", "warn", "⏸"),
        "PendingThirdParty" => new BadgeSpec("Pending third party", "warn", "⏸"),
        "Resolved" => new BadgeSpec("Resolved", "ok", "✓"),
        "Closed" => new BadgeSpec("Closed", "neutral", "🔒", "Closed: this ticket is read-only"),
        _ => new BadgeSpec(ticketStatus, "neutral")
    };

    /// <summary>The statuses reachable through <c>POST /api/tickets/{id}/status</c>.</summary>
    public static IReadOnlyList<(string Value, string Name)> WorkingStatuses { get; } =
    [
        ("Open", "Open"),
        ("InProgress", "In progress"),
        ("PendingCustomer", "Pending customer"),
        ("PendingThirdParty", "Pending third party")
    ];

    /// <summary>Every status the queue can filter on.</summary>
    public static IReadOnlyList<(string Value, string Name)> AllStatuses { get; } =
    [
        ("Open", "Open"),
        ("InProgress", "In progress"),
        ("PendingCustomer", "Pending customer"),
        ("PendingThirdParty", "Pending third party"),
        ("Resolved", "Resolved"),
        ("Closed", "Closed")
    ];

    /// <summary>
    /// Unverified, PendingCrmVerification, Verified.
    ///
    /// <para>
    /// Pending CRM is the one the brief singles out as needing to be obvious
    /// without colour, so it gets both a glyph and an explicit label rather
    /// than an amber dot.
    /// </para>
    /// </summary>
    public static BadgeSpec Verification(string verificationStatus) => verificationStatus switch
    {
        "Verified" => new BadgeSpec("Verified", "ok", "✓", "Customer verified against CRM"),
        "PendingCrmVerification" => new BadgeSpec(
            "Pending CRM", "warn", "◑", "Provisional ticket: awaiting CRM reconciliation"),
        "Unverified" => new BadgeSpec("Unverified", "neutral", "○", "Customer not verified"),
        _ => new BadgeSpec(verificationStatus, "neutral")
    };

    /// <summary>Every verification status the queue can filter on.</summary>
    public static IReadOnlyList<(string Value, string Name)> VerificationStatuses { get; } =
    [
        ("Verified", "Verified"),
        ("PendingCrmVerification", "Pending CRM"),
        ("Unverified", "Unverified")
    ];

    /// <summary>Running, Paused, Met, Breached, NotApplicable.</summary>
    public static BadgeSpec Sla(string? slaState) => slaState switch
    {
        "Breached" => new BadgeSpec("SLA breached", "critical", "!", "Service level agreement breached"),
        "Running" => new BadgeSpec("SLA running", "medium", null, "Service level clock running"),
        "Met" => new BadgeSpec("SLA met", "ok", "✓", "Service level met"),
        "Paused" => new BadgeSpec("SLA paused", "neutral", "⏸"),
        "NotApplicable" => new BadgeSpec("No SLA", "neutral", null, "No service level applies"),
        null or "" => new BadgeSpec("SLA unknown", "neutral", "?", "Service level state could not be read"),
        _ => new BadgeSpec(slaState, "neutral")
    };

    /// <summary>None, Level1, Level2, Level3, Level4. "None" produces no badge at all.</summary>
    public static BadgeSpec? Escalation(string? escalationLevel) => escalationLevel switch
    {
        null or "" or "None" => null,
        "Level1" => new BadgeSpec("Esc L1", "warn", "↑", "Escalated to level 1"),
        "Level2" => new BadgeSpec("Esc L2", "warn", "↑", "Escalated to level 2"),
        "Level3" => new BadgeSpec("Esc L3", "critical", "↑", "Escalated to level 3"),
        "Level4" => new BadgeSpec("Esc L4", "critical", "↑↑", "Escalated to level 4, the highest"),
        _ => new BadgeSpec(escalationLevel, "neutral")
    };

    /// <summary>
    /// The queue row's SLA badge, derived from the per-row SLA summary.
    ///
    /// <para>
    /// A breach is reported from the breach flags rather than only from
    /// <c>SlaState</c>: the flags are immutable once set, so a ticket that
    /// breached and was later resolved still reads as having breached, which is
    /// the fact an operator needs.
    /// </para>
    /// </summary>
    public static BadgeSpec SlaFor(TicketSlaSummaryResponseDto? summary)
    {
        if (summary is null)
        {
            return Sla(null);
        }

        if (summary.FirstResponseBreached || summary.ResolutionBreached)
        {
            var which = (summary.FirstResponseBreached, summary.ResolutionBreached) switch
            {
                (true, true) => "First response and resolution deadlines both breached",
                (true, false) => "First response deadline breached",
                _ => "Resolution deadline breached"
            };

            return new BadgeSpec("SLA breached", "critical", "!", which);
        }

        return Sla(summary.SlaState);
    }

    /// <summary>1=Resolved, 2=Cancelled, 3=Rejected, 4=Duplicate.</summary>
    public static string? ResolutionOutcomeName(byte? outcome) => outcome switch
    {
        1 => "Resolved",
        2 => "Cancelled",
        3 => "Rejected",
        4 => "Duplicate",
        null => null,
        _ => outcome.Value.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>The outcomes <c>POST /api/tickets/{id}/resolution</c> accepts.</summary>
    public static IReadOnlyList<(string Value, string Name)> ResolutionOutcomes { get; } =
    [
        ("Resolved", "Resolved"),
        ("Cancelled", "Cancelled"),
        ("Rejected", "Rejected"),
        ("Duplicate", "Duplicate")
    ];
}
