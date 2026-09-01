namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// One source's outcome for one phone-number search. <c>NotFound</c> means
/// the source answered and had nothing; <c>Failed</c> means the source could
/// not be reached at all. Neither ever blocks Ticket creation — both are
/// reported to the agent exactly the same way as <c>Found</c>.
/// </summary>
public enum CustomerLookupSourceStatus
{
    Found,
    NotFound,
    Failed
}

/// <summary>
/// One unit associated with a matched customer, inside a
/// <see cref="CustomerLookupCustomerDto"/>. Business-rule change: a customer
/// is never assumed to have exactly one unit — this is one entry in that
/// customer's 0..N units. Only the fields the source's own gateway actually
/// exposes are populated; a field the source doesn't have on file is left
/// null rather than fabricated (e.g. only Crm ever populates the local
/// reference ids, and Tasleeh exposes no unit data at all — see
/// <see cref="CustomerLookupCustomerDto.Units"/>'s remarks).
/// </summary>
/// <param name="ExternalUnitId">The source's own immutable identifier for the unit — for Crm, its CrmUnitId.</param>
/// <param name="UnitNumber">The unit number, when the source has one on file.</param>
/// <param name="PropertyName">The property the unit belongs to, when the source has one on file.</param>
/// <param name="TowerName">The tower within the property, when the source has one on file.</param>
/// <param name="UnitType">The unit type, when the source has one on file.</param>
/// <param name="UnitReferenceId">This system's local cache id for the unit, when the source has one (Crm only) — pass this to ticket creation to link it.</param>
/// <param name="ContactReferenceId">This system's local cache id for the specific customer/unit relationship, when the source has one (Crm only) — pass this to ticket creation to link it.</param>
public sealed record CustomerLookupUnitDto(
    string ExternalUnitId,
    string? UnitNumber,
    string? PropertyName,
    string? TowerName,
    string? UnitType,
    int? UnitReferenceId,
    int? ContactReferenceId);

/// <summary>
/// One matched customer inside a <see cref="CustomerLookupSourceResultDto"/>.
/// Business-rule change: a phone number is never assumed to resolve to one
/// customer — a source's Found result carries 0..N of these.
/// </summary>
/// <param name="ExternalCustomerId">The source's own immutable identifier for the customer.</param>
/// <param name="DisplayName">The customer's name, when the source has one on file.</param>
/// <param name="PhoneNumber">The customer's phone number, when the source has one on file.</param>
/// <param name="Email">The customer's email, when the source has one on file.</param>
/// <param name="CustomerType">The customer's source-recorded customer type (e.g. "Buyer"), when the source has one on file.</param>
/// <param name="Units">Every unit (0..N) this source associates with the customer by its own ownership/relationship rules. Empty — never fabricated — for a source that exposes no unit/tenancy data at all (Tasleeh today) or for a customer the source has on file with no eligible unit. Pact units carry PACT's contract/unit data but never the local reference ids (no cache table exists for PACT) — display enrichment only, never linked to a Ticket by id.</param>
public sealed record CustomerLookupCustomerDto(
    string ExternalCustomerId,
    string? DisplayName,
    string? PhoneNumber,
    string? Email,
    string? CustomerType,
    IReadOnlyList<CustomerLookupUnitDto> Units);

/// <summary>One source's result — CRM, PACT, or Tasleeh — inside a <see cref="CustomerLookupResultDto"/>.</summary>
/// <param name="Source">"Crm", "Pact", or "Tasleeh".</param>
/// <param name="Status">Found, NotFound, or Failed — see <see cref="CustomerLookupSourceStatus"/>. Found means one or more customer matched; NotFound means the source answered with zero customers.</param>
/// <param name="Customers">0..N matched customers, each with 0..N units — empty for NotFound/Failed.</param>
public sealed record CustomerLookupSourceResultDto(
    string Source,
    string Status,
    IReadOnlyList<CustomerLookupCustomerDto> Customers)
{
    public static CustomerLookupSourceResultDto Found(string source, IReadOnlyList<CustomerLookupCustomerDto> customers) =>
        new(source, nameof(CustomerLookupSourceStatus.Found), customers);

    public static CustomerLookupSourceResultDto NotFound(string source) =>
        new(source, nameof(CustomerLookupSourceStatus.NotFound), []);

    public static CustomerLookupSourceResultDto Failed(string source) =>
        new(source, nameof(CustomerLookupSourceStatus.Failed), []);
}

/// <summary>
/// The aggregated result of searching CRM, PACT, and Tasleeh by the intake's
/// phone number. Always returns 200 with all three sources' outcomes — one
/// source failing or finding nothing never hides another source's result
/// (e.g. CRM: Found, PACT: Failed, Tasleeh: NotFound all come back together),
/// and this call never blocks or gates ticket creation.
/// </summary>
public sealed record CustomerLookupResultDto(
    long IntakeRecordId,
    string PhoneNumber,
    IReadOnlyList<CustomerLookupSourceResultDto> Sources);

public enum CustomerLookupOutcome
{
    Success,
    IntakeRecordNotFound
}

public sealed record CustomerLookupResult(CustomerLookupOutcome Outcome, CustomerLookupResultDto? Response = null)
{
    public static CustomerLookupResult Success(CustomerLookupResultDto response) => new(CustomerLookupOutcome.Success, response);
    public static CustomerLookupResult Failure(CustomerLookupOutcome outcome) => new(outcome);
}
