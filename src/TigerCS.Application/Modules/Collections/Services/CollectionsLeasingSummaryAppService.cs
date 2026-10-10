using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Leasing path of the Payment Summary: a customer with no unit tied to an eligible CRM record is searched in PACT BY MOBILE under <b>CompanyId = 7</b>.
/// The mobile is normalised first (<c>+971…</c>, <c>971…</c>, <c>00971…</c>, <c>05…</c> are one number). Every returned contract/unit is its own entry keyed by
/// company + tenant + unit + contract, so amounts are never mixed. Ended contracts (a former tenant) are excluded and so is every non-Leasing company.
/// One lookup per call - it is a detail read, never run per list row.
/// </summary>
public sealed class CollectionsLeasingSummaryAppService(
    CollectionsOptions options, CollectionsAuthorizationService authorization, CollectionsClock clock, IPactCustomerLookupGateway pact)
{
    public const int LeasingCompanyId = 7;

    public async Task<CollectionsResult<CollectionsLeasingSummaryDto>> GetAsync(CollectionsCaller caller, string? mobile, CancellationToken cancellationToken = default)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<CollectionsLeasingSummaryDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<CollectionsLeasingSummaryDto>.Fail(CollectionsOutcome.Disabled);
        var normalized = CollectionsContactNormalizer.NormalizePhone(mobile ?? "");
        if (normalized.Length == 0)
            return CollectionsResult<CollectionsLeasingSummaryDto>.Fail(CollectionsOutcome.InvalidRequest, "Give a valid mobile number (for example +971501234567 or 0501234567).");
        var lookup = await pact.SearchByMobileAsync(normalized, cancellationToken);
        if (lookup.Outcome is PactCustomerLookupOutcome.Unauthorized or PactCustomerLookupOutcome.InvalidResponse or PactCustomerLookupOutcome.Unavailable)
            return CollectionsResult<CollectionsLeasingSummaryDto>.Fail(CollectionsOutcome.FinanceUnavailable, "PACT could not be read. Please retry.");
        return CollectionsResult<CollectionsLeasingSummaryDto>.Ok(Compose(normalized, clock.BusinessDate, lookup));
    }

    /// <summary>Pure selection (unit-tested): company 7 only, ended contracts dropped, one entry per tenant + unit + contract, nothing merged.</summary>
    public static CollectionsLeasingSummaryDto Compose(string mobileNormalized, DateOnly today, PactCustomerLookupResult lookup)
    {
        var notes = new List<string>
        {
            "Amounts for Leasing (company 7) are unavailable: no company-7 receivables source is configured. This is not a zero balance."
        };
        if (lookup.Outcome == PactCustomerLookupOutcome.NotFound || lookup.Customers is not { Count: > 0 })
            return new(mobileNormalized, "NotFound", [], 0, 0, notes);
        var contracts = new List<CollectionsLeasingContractDto>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        int ended = 0, other = 0;
        foreach (var customer in lookup.Customers)
            foreach (var c in customer.Contracts)
            {
                if (c.CompanyId != LeasingCompanyId) { other++; continue; }
                if (c.ContractEndDate is { } end && end < today) { ended++; continue; }
                if (!seen.Add($"{customer.PactCustomerId}|{c.ExternalUnitId}|{c.ContractNumber}")) continue;
                contracts.Add(new(LeasingCompanyId, customer.PactCustomerId, customer.DisplayName?.Trim() ?? "",
                    CollectionsContactNormalizer.NormalizePhone(customer.PhoneNumber ?? mobileNormalized), CollectionsContactNormalizer.NormalizeEmail(customer.Email ?? ""),
                    c.ExternalUnitId, c.UnitNumber, c.ContractNumber, c.ProjectName, c.UnitType, c.ContractEndDate,
                    "NoFinancialData", "No company-7 receivables source is configured.", null, null, null));
            }
        if (ended > 0) notes.Add("Contracts that ended before today were excluded (a former tenant's data is not used).");
        return new(mobileNormalized, contracts.Count > 0 ? "Found" : "NotFound", contracts, ended, other, notes);
    }
}
