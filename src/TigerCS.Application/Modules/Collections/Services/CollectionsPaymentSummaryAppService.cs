using System.Globalization;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// EDSM's payment summary for a TigerCS customer.
///
/// <para>
/// <b>Identifier mapping — only what existing code proves.</b> EDSM's summary
/// is keyed by <c>CompanyId</c> + <c>TenantId</c>. TigerCS persists a PACT
/// customer as identity <c>ext:Pact:{tenantID}</c> (the ticket's
/// <c>ExternalCustomerId</c>), but never PACT's <c>companyID</c>. The company
/// comes, live, from PACT's own <c>v1/contracts/{mobile}</c> rows (which carry
/// both <c>tenantID</c> and <c>companyID</c>), looked up by the customer's
/// phone numbers and kept only where the row's tenant equals the stored
/// tenant. A tenant with contracts in two companies gets two summaries.
/// </para>
///
/// <para>
/// A CRM-identified customer is <c>NotMapped</c>: no code or data links a
/// Tiger CRM <c>customerId</c> to a PACT tenant, and matching them by phone
/// alone would be a guess. Nothing here is used for reminder eligibility —
/// the summary has no due dates, so it cannot show how long anything is overdue.
/// </para>
/// </summary>
public sealed class CollectionsPaymentSummaryAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsCustomerProfiles directory,
    IPactCustomerLookupGateway pact,
    IEdsmPaymentSummaryGateway edsm)
{
    public const string PactSource = "Pact";
    private const int MaxPhoneLookups = 5;

    public async Task<CollectionsResult<CollectionsPaymentSummaryResponseDto>> GetAsync(
        CollectionsCaller caller, string customerKey, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return Fail(CollectionsOutcome.Disabled);
        }

        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
        {
            return Fail(CollectionsOutcome.Forbidden, "Viewing customer payments requires the Collections financial-read permission.");
        }

        var profileResult = await directory.GetProfileAsync(caller.EmployeeId, caller.Roles, customerKey, cancellationToken);
        switch (profileResult.Outcome)
        {
            case CustomerDirectoryProfileOutcome.InvalidKey:
                return Fail(CollectionsOutcome.InvalidRequest, "customerKey is not a valid customer key.");
            case CustomerDirectoryProfileOutcome.Success when profileResult.Response is not null:
                break;
            default:
                return Fail(CollectionsOutcome.AccountNotFound, "Customer not found, or not visible to you.");
        }

        var profile = profileResult.Response;
        var retrievedAt = clock.UtcNow;

        if (!string.Equals(profile.ExternalSource, PactSource, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(profile.ExternalCustomerId, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId)
            || tenantId <= 0)
        {
            var why = profile.CrmBuyerCustomerId is { } crmId
                ? $"This customer is identified by Tiger CRM (customerId {crmId}). EDSM's payment summary is keyed by PACT CompanyId and TenantId, and no verified mapping from a CRM customer to a PACT tenant exists."
                : "This customer is not identified as a PACT tenant, so EDSM's payment summary cannot be requested.";
            return Ok(NotMapped(profile.CustomerKey, null, why, retrievedAt));
        }

        var tenantKey = tenantId.ToString(CultureInfo.InvariantCulture);
        var contracts = new List<PactContractDto>();
        var anyAnswered = false;
        foreach (var phone in profile.PhoneNumbers.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxPhoneLookups))
        {
            var lookup = await pact.SearchByMobileAsync(phone, cancellationToken);
            if (lookup.Outcome is PactCustomerLookupOutcome.Success or PactCustomerLookupOutcome.NotFound)
            {
                anyAnswered = true;
            }

            contracts.AddRange((lookup.Customers ?? [])
                .Where(c => string.Equals(c.PactCustomerId, tenantKey, StringComparison.Ordinal))
                .SelectMany(c => c.Contracts));
        }

        if (contracts.Count == 0)
        {
            return anyAnswered
                ? Ok(NotMapped(profile.CustomerKey, tenantKey,
                    $"PACT returned no contracts for tenant {tenantKey} under this customer's phone numbers, so the EDSM company is unknown.", retrievedAt))
                : Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT could not be reached to resolve the customer's company, so the EDSM payment summary is unavailable.");
        }

        var distinct = contracts
            .DistinctBy(c => (c.CompanyId, c.ContractNumber, c.ExternalUnitId))
            .ToList();

        var companies = new List<CollectionsCompanyPaymentSummaryDto>();
        foreach (var group in distinct.Where(c => c.CompanyId is not null).GroupBy(c => c.CompanyId!.Value).OrderBy(g => g.Key))
        {
            var result = await edsm.GetPaymentSummaryAsync(group.Key, tenantKey, cancellationToken);
            companies.Add(ToCompanyDto(group.Key, result, group.Select(ToRef).ToList()));
        }

        return Ok(new CollectionsPaymentSummaryResponseDto(
            profile.CustomerKey,
            companies.Count == 0 ? "NotMapped" : "Mapped",
            companies.Count == 0 ? "PACT returned this tenant's contracts without a companyID, so the EDSM company is unknown." : null,
            tenantKey,
            edsm.SourceName,
            retrievedAt,
            SourceAsOfUtc: null,
            Currency: null,
            FieldDefinitionsConfirmed: false,
            InstalmentDetailAvailable: false,
            TransactionDetailAvailable: false,
            companies,
            distinct.Where(c => c.CompanyId is null).Select(ToRef).ToList()));
    }

    private static CollectionsCompanyPaymentSummaryDto ToCompanyDto(
        int companyId, EdsmPaymentSummaryResult result, IReadOnlyList<CollectionsPactContractRefDto> contracts)
    {
        if (result.Outcome != EdsmPaymentSummaryOutcome.Success || result.Summary is not { } s)
        {
            return new CollectionsCompanyPaymentSummaryDto(companyId, result.Outcome.ToString(), result.Message, null, contracts,
                null, null, null, null, null);
        }

        return new CollectionsCompanyPaymentSummaryDto(companyId, "Available", null, result.Envelope, contracts,
            ToDto(s.TotalAmount), ToDto(s.PaidAmount), ToDto(s.DueAmount), ToDto(s.OutstandingAmount), ToDto(s.LateFines));
    }

    private static CollectionsEdsmAmountDto ToDto(EdsmAmount amount) => new(amount.Status.ToString(), amount.Value, amount.Raw);

    private static CollectionsPactContractRefDto ToRef(PactContractDto c) => new(c.ContractNumber, c.ExternalUnitId, c.UnitNumber, c.ProjectName);

    private CollectionsPaymentSummaryResponseDto NotMapped(string customerKey, string? tenantId, string detail, DateTime retrievedAt) =>
        new(customerKey, "NotMapped", detail, tenantId, edsm.SourceName, retrievedAt, null, null, false, false, false, [], []);

    private static CollectionsResult<CollectionsPaymentSummaryResponseDto> Ok(CollectionsPaymentSummaryResponseDto value) =>
        CollectionsResult<CollectionsPaymentSummaryResponseDto>.Ok(value);

    private static CollectionsResult<CollectionsPaymentSummaryResponseDto> Fail(CollectionsOutcome outcome, string? detail = null) =>
        CollectionsResult<CollectionsPaymentSummaryResponseDto>.Fail(outcome, detail);
}

/// <summary>The Customer Profile read the summary is resolved from — the directory's own department-scoped visibility applies.</summary>
public interface ICollectionsCustomerProfiles
{
    Task<CustomerDirectoryProfileResult> GetProfileAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default);
}

public sealed class CustomerDirectoryCollectionsProfiles(CustomerDirectoryAppService directory) : ICollectionsCustomerProfiles
{
    public Task<CustomerDirectoryProfileResult> GetProfileAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
        directory.GetProfileAsync(callerEmployeeId, callerRoles, customerKey, cancellationToken);
}
