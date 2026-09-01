namespace TigerCS.Application.Modules.CustomerVerification.PactIntegration;

/// <summary>
/// PACT's real customer/contract lookup by mobile number — the PACT
/// counterpart of <see cref="CrmIntegration.ICrmBuyerLookupGateway"/>, and
/// the port <c>CustomerLookupAppService</c>'s PACT leg searches when a
/// Department's <c>DepartmentCustomerLookupSource</c> configuration enables
/// the Pact source. Backed by PACT's <c>GET v1/contracts/{mobile}</c> (the
/// customer's contracts/units) and <c>GET v1/contracts/{mobile}/customer-type</c>
/// (the customer's PACT-recorded type, fetched only when the contracts
/// response did not already carry one).
///
/// <para>
/// <b>Read-only, and never resolves ambiguity on its own.</b> Returns every
/// matched customer (0..N) with every contract/unit PACT associates with
/// them (0..N) — no automatic selection of a first result, matching the
/// business rule that only the CS agent may choose the relevant customer/
/// unit. PACT has no local UnitReference/ContactReference cache table, so a
/// PACT contract is display enrichment for the agent, never linked to a
/// Ticket by id.
/// </para>
///
/// <para>
/// <b>Every failure mode maps to a <see cref="PactCustomerLookupResult"/>
/// outcome — implementations never throw for an expected PACT response</b>
/// (mirrors <see cref="CrmIntegration.ICrmBuyerLookupGateway"/>'s own
/// contract): PACT being down, misconfigured, or answering garbage all
/// collapse to a non-Success outcome, which <c>CustomerLookupAppService</c>
/// reports as a Failed source alongside the other sources' results — a PACT
/// failure or empty answer never blocks New Ticket creation, and the agent
/// can always fall back to manual customer/unit entry.
/// </para>
/// </summary>
public interface IPactCustomerLookupGateway
{
    Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every outcome <see cref="IPactCustomerLookupGateway.SearchByMobileAsync"/>
/// can return — same shape as <see cref="CrmIntegration.CrmBuyerLookupOutcome"/>.
/// </summary>
public enum PactCustomerLookupOutcome
{
    /// <summary>PACT answered with at least one matching customer (each carrying 0..N contracts/units).</summary>
    Success,

    /// <summary>PACT answered and has no customer on file for this mobile number (404, or a 200 body with no match).</summary>
    NotFound,

    /// <summary>PACT rejected the request's X-API-KEY (401/403) — a configuration problem, not a data-not-found result.</summary>
    Unauthorized,

    /// <summary>PACT answered 200 with a body that does not parse as the documented contract, or answered 400.</summary>
    InvalidResponse,

    /// <summary>PACT could not be reached at all — timeout, network failure, missing configuration, a server error, or an unexpected HTTP status.</summary>
    Unavailable
}

/// <summary>
/// The outcome-wrapped result of a PACT customer lookup. <c>Customers</c> is
/// only populated for <see cref="PactCustomerLookupOutcome.Success"/>; every
/// other outcome carries no payload beyond an optional diagnostic
/// <c>Message</c> (for logging only — never shown to the customer).
/// </summary>
public sealed record PactCustomerLookupResult(
    PactCustomerLookupOutcome Outcome, IReadOnlyList<PactCustomerMatchDto>? Customers = null, string? Message = null)
{
    public static PactCustomerLookupResult Success(IReadOnlyList<PactCustomerMatchDto> customers, string? message = null) =>
        new(PactCustomerLookupOutcome.Success, customers, message);

    public static PactCustomerLookupResult NotFound(string? message = null) =>
        new(PactCustomerLookupOutcome.NotFound, Message: message);

    public static PactCustomerLookupResult Unauthorized(string? message = null) =>
        new(PactCustomerLookupOutcome.Unauthorized, Message: message);

    public static PactCustomerLookupResult InvalidResponse(string? message = null) =>
        new(PactCustomerLookupOutcome.InvalidResponse, Message: message);

    public static PactCustomerLookupResult Unavailable(string? message = null) =>
        new(PactCustomerLookupOutcome.Unavailable, Message: message);
}

/// <summary>
/// One PACT customer match — the customer/tenant identity PACT holds for the
/// searched mobile number plus every contract/unit PACT associates with them.
/// Only fields PACT actually has on file are populated; the rest stay null,
/// never fabricated (same discipline as <c>CrmCustomerMatch</c>).
/// </summary>
/// <param name="PactCustomerId">PACT's own identifier for the customer (its tenant id). Never a local reference id — PACT has no local cache table.</param>
/// <param name="DisplayName">The customer's name, when PACT has one on file.</param>
/// <param name="PhoneNumber">The customer's mobile number, when PACT echoes one back.</param>
/// <param name="Email">The customer's email, when PACT has one on file.</param>
/// <param name="CustomerType">The PACT-recorded customer type (e.g. "Tenant"/"Owner"), from the contracts response when it carries one, otherwise from <c>GET v1/contracts/{mobile}/customer-type</c> — null when neither has it.</param>
/// <param name="Contracts">Every contract/unit (0..N) PACT associates with this customer — never assumed to be exactly one, and never auto-selected.</param>
public sealed record PactCustomerMatchDto(
    string PactCustomerId,
    string? DisplayName,
    string? PhoneNumber,
    string? Email,
    string? CustomerType,
    IReadOnlyList<PactContractDto> Contracts);

/// <summary>
/// One contract/unit tied to a <see cref="PactCustomerMatchDto"/>. Display
/// enrichment only: PACT has no UnitReference/ContactReference cache, so
/// nothing here ever resolves to a local id (contrast
/// <c>CrmCustomerUnitMatch</c>).
/// </summary>
/// <param name="ExternalUnitId">PACT's own identifier for the contract's unit (its unit code), falling back to the contract number when PACT sent no unit code.</param>
/// <param name="ContractNumber">The PACT contract number, when on file.</param>
/// <param name="UnitNumber">The unit number, when on file.</param>
/// <param name="ProjectName">The project/property the unit belongs to, when on file.</param>
/// <param name="UnitType">The unit type, when on file.</param>
public sealed record PactContractDto(
    string ExternalUnitId,
    string? ContractNumber,
    string? UnitNumber,
    string? ProjectName,
    string? UnitType);
