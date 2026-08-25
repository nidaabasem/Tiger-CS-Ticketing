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
    /// <summary>
    /// Business-rule change: a phone number is not assumed to resolve to one
    /// customer, and a customer is not assumed to own one unit — returns
    /// every distinct Buyer matched by this phone number (0..N), each
    /// carrying every unit (0..N) tied to that Buyer by a valid ownership
    /// record (see <see cref="CrmCustomerMatch"/>/<see cref="CrmCustomerUnitMatch"/>'s
    /// own remarks for exactly what "valid ownership" means against this
    /// integration's real, existing CRM contact-relationship rules).
    /// </summary>
    Task<IReadOnlyList<CrmCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// One CRM Buyer match — an external customer identity plus every unit tied
/// to that Buyer by a valid ownership record. Carries only the fields the
/// CRM contact-lookup contract (<c>ICrmGateway</c>/<c>CrmContactResult</c>)
/// actually exposes; fields the CRM may not have on file are left null
/// rather than fabricated.
/// </summary>
/// <param name="ExternalCustomerId">The CRM's own immutable identifier for the Buyer — distinct from a per-unit relationship record's own id (see <see cref="CrmCustomerUnitMatch.CrmContactId"/>'s remarks): the same Buyer can be linked to several units, each through its own relationship record, while sharing one <c>ExternalCustomerId</c>.</param>
/// <param name="DisplayName">The Buyer's name, when the CRM has one on file.</param>
/// <param name="PhoneNumber">The Buyer's phone number — the searched value, echoed back.</param>
/// <param name="Email">The Buyer's email, when the CRM has one on file.</param>
/// <param name="CustomerType">The Buyer's CRM-recorded customer type (e.g. "Buyer"), when the CRM has one on file.</param>
/// <param name="Units">Every unit tied to this Buyer by a valid ownership record (0..N) — never assumed to be exactly one.</param>
public sealed record CrmCustomerMatch(
    string ExternalCustomerId,
    string? DisplayName,
    string? PhoneNumber,
    string? Email,
    string? CustomerType,
    IReadOnlyList<CrmCustomerUnitMatch> Units);

/// <summary>
/// One unit tied to a <see cref="CrmCustomerMatch"/>, carrying the CRM's own
/// unit/contact identifiers so the caller can resolve them into the local
/// UnitReference/ContactReference cache exactly as the existing CRM lookup
/// flow already does.
/// </summary>
/// <param name="CrmUnitId">The CRM's own identifier for the unit — passed straight to <c>ICrmGateway.GetUnitAsync</c>.</param>
/// <param name="CrmContactId">The CRM's own identifier for <i>this specific unit's</i> ownership-relationship record — not the Buyer's own <see cref="CrmCustomerMatch.ExternalCustomerId"/>. A Buyer linked to two units has one relationship record (and one <see cref="CrmContactId"/>) per unit, exactly like <c>ICrmGateway.GetContactsAsync</c>'s existing per-unit contact rows.</param>
public sealed record CrmCustomerUnitMatch(string CrmUnitId, string CrmContactId);

public sealed class CrmCustomerLookupGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>PACT read-only customer search by phone — 0..N customers, same multiplicity rule as CRM. No local reference/cache table exists for PACT — a match is pure display enrichment for the agent, never linked to a Ticket by id, and PACT exposes no unit/tenancy data today (units are represented in the normalized DTO as an empty list, never fabricated).</summary>
public interface IPactGateway
{
    Task<IReadOnlyList<PactCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

public sealed record PactCustomerMatch(string PactCustomerId, string? DisplayName, string PhoneNumber);

public sealed class PactGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Tasleeh read-only customer search by phone — 0..N customers, same multiplicity rule as CRM. No local reference/cache table exists for Tasleeh — a match is pure display enrichment for the agent, never linked to a Ticket by id, and Tasleeh exposes no asset/unit data today (units are represented in the normalized DTO as an empty list, never fabricated).</summary>
public interface ITasleehGateway
{
    Task<IReadOnlyList<TasleehCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default);
}

public sealed record TasleehCustomerMatch(string TasleehCustomerId, string? DisplayName, string PhoneNumber);

public sealed class TasleehGatewayUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
