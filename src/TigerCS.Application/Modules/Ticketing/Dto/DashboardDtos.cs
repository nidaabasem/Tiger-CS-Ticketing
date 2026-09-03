namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// The Dashboard's operational summary (<c>GET /api/dashboard</c>): concise
/// KPI counts plus the Tickets Requiring Attention rows, all computed
/// server-side over the caller's own visible-department scope — a
/// department user's numbers cover their departments only, CS-layer roles
/// see across departments, exactly per the existing view authorization.
/// Every count is derived from real ticket/SLA/resolution data; nothing
/// here is estimated or fabricated.
/// </summary>
/// <param name="OpenTickets">Active tickets (Open, InProgress, PendingCustomer, PendingThirdParty).</param>
/// <param name="Unassigned">Active tickets with no current owner.</param>
/// <param name="SlaAtRisk">Active tickets with a pending, unbreached SLA deadline due within the at-risk window.</param>
/// <param name="SlaBreached">Active tickets whose SlaState is Breached.</param>
/// <param name="CriticalOrHigh">Active tickets at priority Critical or High.</param>
/// <param name="PendingCustomer">Tickets currently in PendingCustomer.</param>
/// <param name="ResolvedToday">Tickets whose current resolution was recorded today (UTC day).</param>
/// <param name="Reopened">Active tickets that have been reopened at least once.</param>
/// <param name="MyTickets">Active tickets currently owned by the caller.</param>
/// <param name="AttentionTickets">The Tickets Requiring Attention rows — breached, due soon, critical/high, or unassigned — most urgent first.</param>
public sealed record DashboardSummaryDto(
    int OpenTickets,
    int Unassigned,
    int SlaAtRisk,
    int SlaBreached,
    int CriticalOrHigh,
    int PendingCustomer,
    int ResolvedToday,
    int Reopened,
    int MyTickets,
    IReadOnlyList<DashboardAttentionTicketDto> AttentionTickets);

/// <summary>
/// One Tickets Requiring Attention row — deliberately compact: the ticket's
/// one-line request summary, never a long description, and display
/// snapshots rather than raw external ids.
/// </summary>
/// <param name="TicketId">Links to Ticket Details.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CustomerName">The ticket-time customer name snapshot, when one exists (CRM Buyer tickets).</param>
/// <param name="UnitNumber">The unit number snapshot (CRM Buyer or manual), when one exists.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, PendingThirdParty.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="SlaDueAtUtc">The current SLA period's pending resolution deadline, when one exists.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned.</param>
/// <param name="CurrentDepartmentId">The department that currently holds the ticket.</param>
/// <param name="RequestSummary">The request, in the agent's words — rendered as a single scannable line.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
public sealed record DashboardAttentionTicketDto(
    long TicketId,
    string TicketNumber,
    string? CustomerName,
    string? UnitNumber,
    byte PriorityId,
    string TicketStatus,
    string SlaState,
    DateTime? SlaDueAtUtc,
    Guid? CurrentOwnerEmployeeId,
    int CurrentDepartmentId,
    string RequestSummary,
    DateTime CreatedAtUtc);
