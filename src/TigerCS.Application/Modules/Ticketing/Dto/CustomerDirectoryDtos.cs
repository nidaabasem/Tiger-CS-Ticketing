namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// How a customer known to TigerCS is identified. TigerCS has no customer
/// table: a customer is whatever the persisted tickets say about who raised
/// them, so identity is resolved from the most stable fact each ticket
/// carries, in this order — the CRM Buyer customer id, the persisted
/// external-verification pair (source + external customer id), and only
/// then the phone number captured by the intake the ticket was promoted
/// from. A ticket with none of these has no customer identity and is not
/// in the directory.
/// </summary>
public enum CustomerIdentityKind
{
    /// <summary>Tiger CRM Buyer — <c>Ticket.CrmBuyerCustomerId</c>.</summary>
    Crm = 1,

    /// <summary>PACT / Tasleeh — <c>Ticket.CustomerVerificationSource</c> + <c>Ticket.ExternalCustomerId</c>.</summary>
    External = 2,

    /// <summary>No verified identity — the phone number on the promoted <c>IntakeRecord</c>.</summary>
    Phone = 3,
}

/// <summary>One resolved customer identity — see <see cref="CustomerIdentityKind"/>.</summary>
public sealed record CustomerIdentity(
    CustomerIdentityKind Kind,
    int? CrmBuyerCustomerId,
    string? ExternalSource,
    string? ExternalCustomerId,
    string? PhoneNumber)
{
    public static CustomerIdentity Crm(int crmBuyerCustomerId) => new(CustomerIdentityKind.Crm, crmBuyerCustomerId, null, null, null);

    public static CustomerIdentity External(string source, string externalCustomerId) =>
        new(CustomerIdentityKind.External, null, source, externalCustomerId, null);

    public static CustomerIdentity Phone(string phoneNumber) => new(CustomerIdentityKind.Phone, null, null, null, phoneNumber);

    /// <summary>
    /// The round-trippable key that names this customer in URLs and DTOs:
    /// <c>crm:{id}</c>, <c>ext:{source}:{externalCustomerId}</c> or
    /// <c>phone:{number}</c>. The external id and phone segments are
    /// percent-encoded so a key is always one path segment.
    /// </summary>
    public string Key => Kind switch
    {
        CustomerIdentityKind.Crm => $"crm:{CrmBuyerCustomerId}",
        CustomerIdentityKind.External => $"ext:{Uri.EscapeDataString(ExternalSource!)}:{Uri.EscapeDataString(ExternalCustomerId!)}",
        _ => $"phone:{Uri.EscapeDataString(PhoneNumber!)}",
    };

    /// <summary>The identity a ticket's own persisted facts resolve to, or null when it has none (see <see cref="CustomerIdentityKind"/> for the precedence).</summary>
    public static CustomerIdentity? FromTicketFacts(int? crmBuyerCustomerId, string? verificationSource, string? externalCustomerId, string? intakePhoneNumber)
    {
        if (crmBuyerCustomerId is { } crmId)
        {
            return Crm(crmId);
        }

        if (!string.IsNullOrWhiteSpace(verificationSource) && !string.IsNullOrWhiteSpace(externalCustomerId))
        {
            return External(verificationSource, externalCustomerId);
        }

        return string.IsNullOrWhiteSpace(intakePhoneNumber) ? null : Phone(intakePhoneNumber);
    }

    public static bool TryParse(string? key, out CustomerIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        key = key.Trim();
        if (key.StartsWith("crm:", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[4..], out var crmId) && crmId > 0)
        {
            identity = Crm(crmId);
            return true;
        }

        if (key.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = key[4..];
            var separator = rest.IndexOf(':');
            if (separator <= 0 || separator == rest.Length - 1)
            {
                return false;
            }

            var source = Uri.UnescapeDataString(rest[..separator]);
            var externalId = Uri.UnescapeDataString(rest[(separator + 1)..]);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(externalId))
            {
                return false;
            }

            identity = External(source, externalId);
            return true;
        }

        if (key.StartsWith("phone:", StringComparison.OrdinalIgnoreCase))
        {
            var phone = Uri.UnescapeDataString(key[6..]);
            if (string.IsNullOrWhiteSpace(phone))
            {
                return false;
            }

            identity = Phone(phone);
            return true;
        }

        return false;
    }
}

/// <summary>The Customers directory list request (<c>GET api/customers</c>). Every filter is optional; the list is never empty just because nothing was searched.</summary>
/// <param name="Search">Free text matched against the customer name, phone, unit number, project and ticket number of any of the customer's tickets.</param>
/// <param name="VerificationSource">"Crm", "Unverified", or an external source name ("Pact", "Tasleeh") — the identity kind/source to keep.</param>
/// <param name="DepartmentId">Keep customers with at least one ticket currently in this department.</param>
/// <param name="OpenOnly">Keep customers with at least one active ticket.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page (1–100; defaults to 25 otherwise).</param>
public sealed record CustomerDirectoryListRequestDto(
    string? Search = null,
    string? VerificationSource = null,
    int? DepartmentId = null,
    bool OpenOnly = false,
    int Page = 1,
    int PageSize = 25);

/// <summary>One customer row of the directory: the identity, what the latest ticket says about them, and their ticket footprint.</summary>
public sealed record CustomerDirectoryRowDto(
    string CustomerKey,
    string IdentityKind,
    string? DisplayName,
    string? PhoneNumber,
    string? ProjectName,
    string? UnitNumber,
    string VerificationSource,
    int OpenTickets,
    int TotalTickets,
    long LastTicketId,
    string LastTicketNumber,
    string LastTicketStatus,
    DateTime LastTicketCreatedAtUtc,
    DateTime? LastInteractionAtUtc);

public sealed record CustomerDirectoryListResultDto(IReadOnlyList<CustomerDirectoryRowDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>A unit/property this customer's tickets were raised for — CRM Buyer or manual snapshot, deduplicated by project + unit number.</summary>
public sealed record CustomerDirectoryUnitDto(string? ProjectName, string? UnitNumber, string Source, int TicketCount, DateTime LastTicketAtUtc, int? CrmBuyerUnitId, string? ExternalUnitId);

/// <summary>One of the customer's tickets, with the facts the Customer Profile lists: status, priority, department, dates, unit and last activity.</summary>
public sealed record CustomerDirectoryTicketDto(
    long TicketId,
    string TicketNumber,
    string TicketStatus,
    byte? PriorityId,
    int CurrentDepartmentId,
    DateTime CreatedAtUtc,
    string? ProjectName,
    string? UnitNumber,
    string RequestSummary,
    DateTime LastActivityAtUtc,
    DateTime? ResolvedAtUtc,
    string VerificationStatus);

/// <summary>One recorded call/chat across the customer's tickets (Genesys or local), newest first.</summary>
public sealed record CustomerDirectoryInteractionDto(
    long TicketInteractionId,
    long TicketId,
    string TicketNumber,
    byte ChannelId,
    string? ChannelName,
    string? Direction,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string Status,
    string? AgentName,
    bool IsOriginatingInteraction);

/// <summary>The Customer Profile read model — everything TigerCS itself knows about one customer identity, from its persisted tickets, intakes and interactions.</summary>
public sealed record CustomerDirectoryProfileDto(
    string CustomerKey,
    string IdentityKind,
    string? DisplayName,
    IReadOnlyList<string> PhoneNumbers,
    IReadOnlyList<string> Emails,
    string VerificationSource,
    int? CrmBuyerCustomerId,
    string? ExternalSource,
    string? ExternalCustomerId,
    int OpenTickets,
    int TotalTickets,
    DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc,
    long LastTicketId,
    IReadOnlyList<CustomerDirectoryUnitDto> Units,
    IReadOnlyList<CustomerDirectoryTicketDto> Tickets,
    IReadOnlyList<CustomerDirectoryInteractionDto> Interactions);

public enum CustomerDirectoryProfileOutcome
{
    Success,
    InvalidKey,
    NotFound,
}

public sealed record CustomerDirectoryProfileResult(CustomerDirectoryProfileOutcome Outcome, CustomerDirectoryProfileDto? Response = null)
{
    public static CustomerDirectoryProfileResult Success(CustomerDirectoryProfileDto response) => new(CustomerDirectoryProfileOutcome.Success, response);
    public static CustomerDirectoryProfileResult Failure(CustomerDirectoryProfileOutcome outcome) => new(outcome);
}
