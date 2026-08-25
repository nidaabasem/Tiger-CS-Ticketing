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

/// <summary>One source's result — CRM, PACT, or Tasleeh — inside a <see cref="CustomerLookupResultDto"/>.</summary>
/// <param name="Source">"Crm", "Pact", or "Tasleeh".</param>
/// <param name="Status">Found, NotFound, or Failed — see <see cref="CustomerLookupSourceStatus"/>.</param>
/// <param name="DisplayName">The matched customer's name, when Found.</param>
/// <param name="PhoneNumber">The matched customer's phone number, when Found.</param>
/// <param name="UnitNumber">The matched CRM unit's number, when Found and the source is Crm; null for Pact/Tasleeh, which carry no unit linkage.</param>
/// <param name="UnitReferenceId">The local reference id of the matched unit, when Found and the source is Crm — pass this to ticket creation to link it. Null for Pact/Tasleeh.</param>
/// <param name="ContactReferenceId">The local reference id of the matched contact, when Found and the source is Crm — pass this to ticket creation to link it. Null for Pact/Tasleeh.</param>
public sealed record CustomerLookupSourceResultDto(
    string Source,
    string Status,
    string? DisplayName,
    string? PhoneNumber,
    string? UnitNumber,
    int? UnitReferenceId,
    int? ContactReferenceId)
{
    public static CustomerLookupSourceResultDto Found(
        string source, string? displayName, string? phoneNumber, string? unitNumber = null, int? unitReferenceId = null, int? contactReferenceId = null) =>
        new(source, nameof(CustomerLookupSourceStatus.Found), displayName, phoneNumber, unitNumber, unitReferenceId, contactReferenceId);

    public static CustomerLookupSourceResultDto NotFound(string source) =>
        new(source, nameof(CustomerLookupSourceStatus.NotFound), null, null, null, null, null);

    public static CustomerLookupSourceResultDto Failed(string source) =>
        new(source, nameof(CustomerLookupSourceStatus.Failed), null, null, null, null, null);
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
