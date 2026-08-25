namespace TigerCS.Application.Modules.CustomerVerification.CustomerLookup;

/// <summary>
/// Business-rule change: customer lookup is enrichment/identification, not a
/// Ticket creation gate. Each of the three external sources — Tiger CRM,
/// PACT, and Tasleeh — gets its own narrow, read-only, phone-search-only
/// port, deliberately separate from <c>ICrmGateway</c> (that interface's own
/// remarks scope it to unit-number lookup only; phone-based customer search
/// is a different capability with a different caller,
/// <c>CustomerLookupAppService</c>, and must never be able to widen
/// <c>ICrmGateway</c>'s own documented boundary).
///
/// <para>
/// <b>Every implementation must fail closed, never block.</b> A source that
/// cannot be reached throws its own <c>*GatewayUnavailableException</c>;
/// <c>CustomerLookupAppService</c> catches each source independently so one
/// source's outage never affects another's result, and none of the three
/// ever prevents a ticket from being created.
/// </para>
/// </summary>
public interface ICrmCustomerLookupGateway
{
    Task<CrmCustomerMatch?> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

/// <summary>A CRM customer match — carries the CRM's own unit/contact identifiers so the caller can upsert them into the local UnitReference/ContactReference cache exactly as the existing CRM lookup flow already does.</summary>
public sealed record CrmCustomerMatch(string CrmUnitId, string CrmContactId, string? DisplayName, string? PhoneNumber);

public sealed class CrmCustomerLookupGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>PACT read-only customer search by phone. No local reference/cache table exists for PACT — a match is pure display enrichment for the agent, never linked to a Ticket by id.</summary>
public interface IPactGateway
{
    Task<PactCustomerMatch?> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

public sealed record PactCustomerMatch(string PactCustomerId, string? DisplayName, string PhoneNumber);

public sealed class PactGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Tasleeh read-only customer search by phone. No local reference/cache table exists for Tasleeh — a match is pure display enrichment for the agent, never linked to a Ticket by id.</summary>
public interface ITasleehGateway
{
    Task<TasleehCustomerMatch?> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

public sealed record TasleehCustomerMatch(string TasleehCustomerId, string? DisplayName, string PhoneNumber);

public sealed class TasleehGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
