using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Business-rule change: searches CRM, PACT, and/or Tasleeh by the intake's
/// phone number and returns whatever each searched source found — this is
/// enrichment/identification for the agent, never a Ticket creation gate
/// (see <c>TicketCreationAppService.CreateAsync</c>, which accepts an
/// optional matched unit/contact pair but never requires one).
///
/// <para>
/// <b>Which sources are searched depends on the intake's Department, not a
/// hard-coded rule.</b> An IntakeRecord with no <c>DepartmentId</c> searches
/// all three sources. One with a <c>DepartmentId</c> searches only the
/// source(s) configured for that Department
/// (<see cref="IDepartmentCustomerLookupSourceRepository"/> — data-driven,
/// not an <c>if (departmentId == ...)</c> branch per department) — including
/// zero sources if none are configured, never silently falling back to
/// "search everything".
/// </para>
///
/// <para>
/// <b>Sources are searched independently, in parallel, and never let one
/// another's failure hide a result.</b> Each of <see cref="SearchCrmAsync"/>/
/// <see cref="SearchPactAsync"/>/<see cref="SearchTasleehAsync"/> catches
/// only its own source's unavailable exception; a source that cannot be
/// reached comes back <c>Failed</c>, a source that answers with nothing
/// comes back <c>NotFound</c>, and both are returned alongside whatever the
/// other searched source(s) found (e.g. CRM: Found, PACT: Failed, Tasleeh:
/// NotFound all in the same response) — this call itself never throws for
/// any of that, and never rejects the request. A source that was never
/// searched (out of Department scope) has no entry at all — never a fake
/// NotFound standing in for "not queried".
/// </para>
///
/// <para>
/// <b>CRM matches reuse the existing cache-aside upsert.</b> A CRM phone
/// match is resolved to this system's own local <c>UnitReferenceId</c>/
/// <c>ContactReferenceId</c> via <see cref="CrmUnitLookupAppService"/> — the
/// same tested upsert path <c>CrmController</c>'s unit-number lookups
/// already use — so a match found here can be passed straight to ticket
/// creation. PACT and Tasleeh have no equivalent local reference table: a
/// match from either is pure display enrichment, never linked to a Ticket by
/// id (see <see cref="ICrmCustomerLookupGateway"/>'s remarks).
/// </para>
/// </summary>
public sealed class CustomerLookupAppService(
    IIntakeRecordRepository intakeRecordRepository,
    IDepartmentCustomerLookupSourceRepository departmentSourceRepository,
    ICrmCustomerLookupGateway crmCustomerLookupGateway,
    IPactGateway pactGateway,
    ITasleehGateway tasleehGateway,
    CrmUnitLookupAppService crmUnitLookupAppService)
{
    private static readonly IReadOnlyCollection<CustomerLookupSource> AllSources =
        [CustomerLookupSource.Crm, CustomerLookupSource.Pact, CustomerLookupSource.Tasleeh];

    public async Task<CustomerLookupResult> SearchAsync(long intakeRecordId, CancellationToken cancellationToken = default)
    {
        var intakeRecord = await intakeRecordRepository.GetByIdAsync(intakeRecordId, cancellationToken);
        if (intakeRecord is null)
        {
            return CustomerLookupResult.Failure(CustomerLookupOutcome.IntakeRecordNotFound);
        }

        var sources = intakeRecord.DepartmentId is { } departmentId
            ? await departmentSourceRepository.GetSourcesForDepartmentAsync(departmentId, cancellationToken)
            : AllSources;

        var tasks = sources.Select(source => SearchSourceAsync(source, intakeRecord.PhoneNumber, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        return CustomerLookupResult.Success(new CustomerLookupResultDto(intakeRecordId, intakeRecord.PhoneNumber, results));
    }

    private Task<CustomerLookupSourceResultDto> SearchSourceAsync(
        CustomerLookupSource source, string phoneNumber, CancellationToken cancellationToken) => source switch
    {
        CustomerLookupSource.Crm => SearchCrmAsync(phoneNumber, cancellationToken),
        CustomerLookupSource.Pact => SearchPactAsync(phoneNumber, cancellationToken),
        CustomerLookupSource.Tasleeh => SearchTasleehAsync(phoneNumber, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown customer lookup source.")
    };

    private async Task<CustomerLookupSourceResultDto> SearchCrmAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var sourceName = CustomerLookupSource.Crm.ToString();

        CrmCustomerMatch? match;
        try
        {
            match = await crmCustomerLookupGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
        }
        catch (CrmCustomerLookupGatewayUnavailableException)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }

        if (match is null)
        {
            return CustomerLookupSourceResultDto.NotFound(sourceName);
        }

        // Cache-aside upsert (CrmUnitLookupAppService already absorbs any
        // gateway failure into its own CrmUnavailable outcome rather than
        // throwing — MockCrmGateway already answered above for this same
        // phone search, so a failure here would be a genuinely new,
        // independent fault).
        var unitResult = await crmUnitLookupAppService.GetUnitAsync(match.CrmUnitId, cancellationToken);
        if (unitResult.Outcome != CrmLookupOutcome.Success || unitResult.Response is null)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }

        var contactsResult = await crmUnitLookupAppService.GetContactsAsync(match.CrmUnitId, cancellationToken);
        var contact = contactsResult.Outcome == CrmLookupOutcome.Success
            ? contactsResult.Contacts?.FirstOrDefault(c => c.CrmContactId == match.CrmContactId)
            : null;

        return CustomerLookupSourceResultDto.Found(
            sourceName,
            contact?.DisplayName ?? match.DisplayName,
            contact?.ContactChannel ?? match.PhoneNumber,
            unitResult.Response.UnitNumber,
            unitResult.Response.UnitReferenceId,
            contact?.ContactReferenceId);
    }

    private async Task<CustomerLookupSourceResultDto> SearchPactAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var sourceName = CustomerLookupSource.Pact.ToString();
        try
        {
            var match = await pactGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
            return match is null
                ? CustomerLookupSourceResultDto.NotFound(sourceName)
                : CustomerLookupSourceResultDto.Found(sourceName, match.DisplayName, match.PhoneNumber);
        }
        catch (PactGatewayUnavailableException)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }
    }

    private async Task<CustomerLookupSourceResultDto> SearchTasleehAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var sourceName = CustomerLookupSource.Tasleeh.ToString();
        try
        {
            var match = await tasleehGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
            return match is null
                ? CustomerLookupSourceResultDto.NotFound(sourceName)
                : CustomerLookupSourceResultDto.Found(sourceName, match.DisplayName, match.PhoneNumber);
        }
        catch (TasleehGatewayUnavailableException)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }
    }
}
