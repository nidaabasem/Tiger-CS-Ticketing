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

    /// <summary>
    /// Business-rule change: a phone number may match several distinct
    /// Buyers, and each Buyer may own several units — never assumed to be
    /// one of either. Every matched Buyer's every unit is resolved to this
    /// system's own local UnitReference/ContactReference cache ids via the
    /// same tested cache-aside upsert <see cref="SearchCrmAsync"/> already
    /// used (<see cref="CrmUnitLookupAppService"/>), one unit at a time, so
    /// each unit in the response carries the correct pair to pass straight
    /// to ticket creation for that specific customer/unit relationship.
    /// </summary>
    private async Task<CustomerLookupSourceResultDto> SearchCrmAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var sourceName = CustomerLookupSource.Crm.ToString();

        IReadOnlyList<CrmCustomerMatch> matches;
        try
        {
            matches = await crmCustomerLookupGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
        }
        catch (CrmCustomerLookupGatewayUnavailableException)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }

        if (matches.Count == 0)
        {
            return CustomerLookupSourceResultDto.NotFound(sourceName);
        }

        var customers = new List<CustomerLookupCustomerDto>();
        foreach (var match in matches)
        {
            var units = new List<CustomerLookupUnitDto>();

            // Distinct by CrmUnitId: a duplicate relationship row for the
            // same unit (e.g. a data glitch upstream) must never surface as
            // two separate units for the same customer.
            foreach (var unitMatch in match.Units.DistinctBy(u => u.CrmUnitId))
            {
                // Cache-aside upsert (CrmUnitLookupAppService already absorbs
                // any gateway failure into its own CrmUnavailable outcome
                // rather than throwing — MockCrmGateway already answered
                // above for this same phone search, so a failure here would
                // be a genuinely new, independent fault).
                var unitResult = await crmUnitLookupAppService.GetUnitAsync(unitMatch.CrmUnitId, cancellationToken);
                if (unitResult.Outcome == CrmLookupOutcome.CrmUnavailable)
                {
                    // A genuine CRM outage mid-resolution is the same fault
                    // the top-level catch above handles — isolate the whole
                    // Crm source, not just this one unit/customer.
                    return CustomerLookupSourceResultDto.Failed(sourceName);
                }

                if (unitResult.Outcome != CrmLookupOutcome.Success || unitResult.Response is null)
                {
                    // Matched by phone, but the unit record itself is
                    // already gone from the CRM — skip just this one unit,
                    // never the whole customer.
                    continue;
                }

                var contactsResult = await crmUnitLookupAppService.GetContactsAsync(unitMatch.CrmUnitId, cancellationToken);
                var contact = contactsResult.Outcome == CrmLookupOutcome.Success
                    ? contactsResult.Contacts?.FirstOrDefault(c => c.CrmContactId == unitMatch.CrmContactId)
                    : null;

                units.Add(new CustomerLookupUnitDto(
                    unitMatch.CrmUnitId,
                    unitResult.Response.UnitNumber,
                    unitResult.Response.PropertyName,
                    unitResult.Response.TowerName,
                    unitResult.Response.UnitType,
                    unitResult.Response.UnitReferenceId,
                    contact?.ContactReferenceId));
            }

            customers.Add(new CustomerLookupCustomerDto(
                match.ExternalCustomerId, match.DisplayName, match.PhoneNumber, match.Email, match.CustomerType, units));
        }

        return CustomerLookupSourceResultDto.Found(sourceName, customers);
    }

    private async Task<CustomerLookupSourceResultDto> SearchPactAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        var sourceName = CustomerLookupSource.Pact.ToString();
        try
        {
            var matches = await pactGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
            if (matches.Count == 0)
            {
                return CustomerLookupSourceResultDto.NotFound(sourceName);
            }

            // Pact exposes no unit/tenancy data today — Units is an empty
            // list, never fabricated (see CustomerLookupCustomerDto's remarks).
            var customers = matches
                .Select(match => new CustomerLookupCustomerDto(
                    match.PactCustomerId, match.DisplayName, match.PhoneNumber, Email: null, CustomerType: null, Units: []))
                .ToList();
            return CustomerLookupSourceResultDto.Found(sourceName, customers);
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
            var matches = await tasleehGateway.SearchByPhoneAsync(phoneNumber, cancellationToken);
            if (matches.Count == 0)
            {
                return CustomerLookupSourceResultDto.NotFound(sourceName);
            }

            // Tasleeh exposes no asset/unit data today — Units is an empty
            // list, never fabricated (see CustomerLookupCustomerDto's remarks).
            var customers = matches
                .Select(match => new CustomerLookupCustomerDto(
                    match.TasleehCustomerId, match.DisplayName, match.PhoneNumber, Email: null, CustomerType: null, Units: []))
                .ToList();
            return CustomerLookupSourceResultDto.Found(sourceName, customers);
        }
        catch (TasleehGatewayUnavailableException)
        {
            return CustomerLookupSourceResultDto.Failed(sourceName);
        }
    }
}
