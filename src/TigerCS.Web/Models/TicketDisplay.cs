using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.Models;

/// <summary>
/// Display mappings for TigerCS.Api's fixed enum-like fields. Priority is a
/// documented, fixed 4-value scale (1=Critical..4=Low) repeated identically
/// across every DTO and controller doc comment in the Api, so hardcoding it
/// here is not a guess. Ticket/verification/SLA status strings are likewise
/// the literal, closed value sets the Api's own XML docs enumerate — not
/// invented labels.
/// </summary>
public static class TicketDisplay
{
    public static string PriorityLabel(byte priorityId) => priorityId switch
    {
        1 => "Critical",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => $"Priority {priorityId}"
    };

    public static string PriorityCssKey(byte priorityId) => priorityId switch
    {
        1 => "critical",
        2 => "high",
        3 => "medium",
        4 => "low",
        _ => "medium"
    };

    public static string TicketStatusLabel(string ticketStatus) => ticketStatus switch
    {
        "Open" => "Open",
        "InProgress" => "In Progress",
        "PendingCustomer" => "Pending Customer",
        "PendingThirdParty" => "Pending Third Party",
        "Resolved" => "Resolved",
        "Closed" => "Closed",
        _ => ticketStatus
    };

    public static string TicketStatusCssKey(string ticketStatus) => ticketStatus switch
    {
        "Open" => "open",
        "InProgress" => "inprogress",
        "PendingCustomer" or "PendingThirdParty" => "pending",
        "Resolved" => "resolved",
        "Closed" => "closed",
        _ => "open"
    };

    public static string VerificationStatusLabel(string verificationStatus) => verificationStatus switch
    {
        "Unverified" => "Unverified",
        "PendingCrmVerification" => "Pending CRM Verification",
        "Verified" => "Verified",
        _ => verificationStatus
    };

    public static string SlaStateLabel(string slaState) => slaState switch
    {
        "Running" => "Running",
        "Paused" => "Paused",
        "Met" => "Met",
        "Breached" => "Breached",
        "NotApplicable" => "Not applicable",
        _ => slaState
    };

    public static string SlaStateCssKey(string slaState) => slaState switch
    {
        "Breached" => "breached",
        "Met" => "met",
        "Paused" => "paused",
        "NotApplicable" => "na",
        _ => "running"
    };

    public static string EscalationLevelLabel(string escalationLevel) => escalationLevel switch
    {
        "None" => "None",
        "Level1" => "Level 1",
        "Level2" => "Level 2",
        "Level3" => "Level 3",
        "Level4" => "Level 4",
        _ => escalationLevel
    };

    public static string ResolutionOutcomeLabel(byte? resolutionOutcome) => resolutionOutcome switch
    {
        1 => "Resolved",
        2 => "Cancelled",
        3 => "Rejected",
        4 => "Duplicate",
        _ => "—"
    };

    /// <summary>A short, readable form of a due/overdue TimeSpan, e.g. "2h 15m" or "38m".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        var abs = span.Duration();
        if (abs.TotalDays >= 1)
        {
            return $"{(int)abs.TotalDays}d {abs.Hours}h";
        }

        if (abs.TotalHours >= 1)
        {
            return $"{(int)abs.TotalHours}h {abs.Minutes}m";
        }

        return $"{Math.Max(1, abs.Minutes)}m";
    }

    /// <summary>
    /// A single badge label for an SLA summary. TigerCS.Api returns only due
    /// dates and breach booleans (no "remaining time"/"breach duration"
    /// field) — the countdown/overdue-by text here is computed from those,
    /// not invented.
    /// </summary>
    public static (string Label, string CssKey) SlaBadgeText(TicketSlaSummaryResponseDto sla, DateTime nowUtc)
    {
        if (sla.SlaState == "Breached")
        {
            var dueAt = sla.ResolutionBreached ? sla.ResolutionDueAtUtc : sla.FirstResponseDueAtUtc;
            return dueAt is DateTime due
                ? ($"Breached {FormatDuration(nowUtc - due)}", "breached")
                : ("Breached", "breached");
        }

        if (sla.SlaState == "Running")
        {
            var dueAt = sla.ResolutionDueAtUtc ?? sla.FirstResponseDueAtUtc;
            return dueAt is DateTime due
                ? ($"Due in {FormatDuration(due - nowUtc)}", "running")
                : ("Running", "running");
        }

        if (sla.SlaState == "Met")
        {
            return ("Met", "met");
        }

        if (sla.SlaState == "Paused")
        {
            return ("Paused", "paused");
        }

        return ("Not applicable", "na");
    }
}
