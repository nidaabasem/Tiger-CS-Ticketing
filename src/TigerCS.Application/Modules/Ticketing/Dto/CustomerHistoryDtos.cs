namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Customer → Previous Ticket History. Sourced exclusively from the
/// existing Tickets table — never a live CRM call — so this works even when
/// CRM is offline. Exactly one identity is ever used per result:
/// <see cref="VerificationType"/> <c>"Verified"</c> means every ticket in
/// <see cref="Tickets"/> shares the same <c>CrmBuyerCustomerId</c> (a phone
/// number may match more than one CRM customer, so phone is never used to
/// widen a verified customer's history); <c>"Unverified"</c> means there was
/// no CrmBuyerCustomerId to key on and the persisted
/// <c>IntakeRecord.PhoneNumber</c> snapshot was used as a fallback key
/// instead — a weaker identity, and always labelled as such rather than
/// silently presented the same way as CRM-verified history.
/// </summary>
/// <param name="VerificationType">One of "Verified" (keyed by CrmBuyerCustomerId) or "Unverified" (keyed by phone-number snapshot).</param>
/// <param name="CrmBuyerCustomerId">The CRM customer id this history was queried for, when <paramref name="VerificationType"/> is "Verified"; otherwise null.</param>
/// <param name="PhoneNumberSnapshot">The phone-number snapshot this history was queried for, when <paramref name="VerificationType"/> is "Unverified"; otherwise null.</param>
/// <param name="CustomerDisplayName">Best-effort display name for the customer identity, when one is known (the CRM Buyer's ticket-time name snapshot). Null when no ticket in the history carried one.</param>
/// <param name="TotalTickets">Count of every matching ticket — not just the ones returned in <paramref name="Tickets"/>.</param>
/// <param name="OpenTickets">Count of matching tickets whose TicketStatus is not Resolved or Closed.</param>
/// <param name="ClosedTickets">Count of matching tickets whose TicketStatus is Resolved or Closed.</param>
/// <param name="Tickets">The newest tickets first, limited to the caller's requested page size — never the customer's entire history unbounded.</param>
public sealed record CustomerHistoryDto(
    string VerificationType,
    int? CrmBuyerCustomerId,
    string? PhoneNumberSnapshot,
    string? CustomerDisplayName,
    int TotalTickets,
    int OpenTickets,
    int ClosedTickets,
    IReadOnlyList<CustomerHistoryTicketDto> Tickets);

/// <summary>One row of a customer's ticket history — fields drawn from the Ticket aggregate alone, no other lookup performed per row.</summary>
/// <param name="TicketId">The ticket — links to <c>GET /api/tickets/{ticketId}</c> / the Ticket Details page.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC — the sort key (newest first).</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="CategoryId">The ticket's category.</param>
/// <param name="CurrentDepartmentId">The department that currently holds the ticket.</param>
/// <param name="ProjectName">The CRM Buyer or manually-entered project name snapshot, or null when neither is set.</param>
/// <param name="UnitNumber">The CRM Buyer or manually-entered unit number snapshot, or null when neither is set.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
public sealed record CustomerHistoryTicketDto(
    long TicketId,
    string TicketNumber,
    DateTime CreatedAtUtc,
    string TicketStatus,
    byte PriorityId,
    int CategoryId,
    int CurrentDepartmentId,
    string? ProjectName,
    string? UnitNumber,
    string VerificationStatus);

public enum CustomerHistoryOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record CustomerHistoryResult(CustomerHistoryOutcome Outcome, CustomerHistoryDto? Response = null)
{
    public static CustomerHistoryResult Success(CustomerHistoryDto response) => new(CustomerHistoryOutcome.Success, response);
    public static CustomerHistoryResult Failure(CustomerHistoryOutcome outcome) => new(outcome);
}
