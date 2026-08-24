namespace TigerCS.Web.Models;

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum TicketStatus
{
    Open,
    InProgress,
    AwaitingCustomer,
    Closed
}

public enum SlaState
{
    OnTrack,
    DueSoon,
    Breached
}

public sealed record TicketActivity(
    string IconKind,
    string Actor,
    string Action,
    string Timestamp,
    string? Note = null,
    bool HasAttachment = false);

public sealed record TicketRecord(
    string Id,
    string Subject,
    string CustomerName,
    string Unit,
    string Project,
    TicketPriority Priority,
    TicketStatus Status,
    string Department,
    string Owner,
    SlaState Sla,
    string SlaText,
    string LastActivity,
    string Source,
    string CreatedDate,
    string CreatedBy,
    string Category,
    string Subcategory,
    int EscalationLevel,
    string CrmCustomerId,
    string Mobile,
    string Email,
    string PreferredLanguage,
    string Tower,
    string UnitStatus,
    string FirstResponseDue,
    string ResolutionDue,
    string RemainingOrBreach,
    IReadOnlyList<TicketActivity> Activities);
