using System.Globalization;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// EDSM's figures for a TigerCS customer (docs/Collections/EDSM_Collections_Contract.md).
///
/// <para>
/// <b>Account resolution.</b> EDSM keys every route by the (<c>companyID</c>,
/// <c>tenantID</c>) pair. It answers an unknown tenant with 200 and zeros, so
/// an EDSM response never proves that an account exists. Existence and
/// ownership come only from PACT's <c>v1/contracts/{mobile}</c>:
/// <list type="bullet">
/// <item>TigerCS persists a PACT customer as <c>ext:Pact:{tenantID}</c>.</item>
/// <item>The customer's phone numbers are looked up in PACT, and only rows whose own
/// <c>tenantID</c> equals the stored tenant are kept. Each pair is taken from the
/// same row, and the tenant id always comes from <c>tenantID</c>, never from a
/// unit or contract reference. For Parking units, <c>UserUnit.RefId</c> is the
/// ContractID, so it is never used.</item>
/// <item>EDSM is asked only for those pairs: one summary per company, with that
/// company's contracts listed.</item>
/// </list>
/// A CRM-identified customer is <c>NotMapped</c>: no verified CRM → PACT
/// crosswalk exists (tickets store one identity or the other).
/// </para>
///
/// <para>
/// <b>Field definitions</b> differ for owned companies (4, 32) and rented
/// companies (25, 7, 20). They are mapped separately, from EDSM's C# code.
/// TigerCS shows EDSM's figures; it never recomputes, adds or nets them, and
/// none of them feed reminder eligibility.
/// </para>
///
/// <para>
/// <b>Side effect, accepted knowingly:</b> PACT's <c>v1/contracts/{mobile}</c>
/// writes customer-type and <c>UserUnit</c> rows in EDSM (contract §8.3). TigerCS's
/// existing customer lookup already makes this call. This service adds one per
/// Payment tab load and phone number.
/// </para>
/// </summary>
public sealed class CollectionsPaymentSummaryAppService(
    CollectionsOptions options,
    CollectionsEdsmOptions edsmOptions,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsCustomerProfiles directory,
    IPactCustomerLookupGateway pact,
    IEdsmCollectionsGateway edsm,
    PactAccountMappingCache mappingCache)
{
    public const string PactSource = "Pact";
    private const int MaxPhoneLookups = 5;
    private const decimal TotalTolerance = 0.03m;   // three independently rounded 2-dp strings

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
                ? $"This customer is identified by Tiger CRM (customerId {crmId}). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists."
                : "This customer is not identified as a PACT tenant, so EDSM cannot be asked.";
            return Ok(Response(profile.CustomerKey, "NotMapped", why, null, retrievedAt, [], []));
        }

        var tenantKey = tenantId.ToString(CultureInfo.InvariantCulture);
        var cacheKey = PactAccountMappingCache.Key(tenantKey, profile.PhoneNumbers);
        var ttl = TimeSpan.FromMinutes(Math.Clamp(edsmOptions.PactMappingTtlMinutes, 1, 1440));
        var negativeTtl = TimeSpan.FromMinutes(Math.Clamp(edsmOptions.PactMappingNegativeTtlMinutes, 1, 60));

        var mapping = mappingCache.Get(cacheKey, ttl, negativeTtl);
        var mappingSource = "Cached";
        if (mapping is null)
        {
            mapping = await DiscoverAsync(tenantKey, profile.PhoneNumbers, cancellationToken);
            if (mapping is null)
            {
                // PACT unreachable: nothing is cached, so the next load has a reason to retry.
                return Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT could not be reached to confirm the customer's accounts, so EDSM figures are unavailable.");
            }

            mappingCache.Set(cacheKey, mapping);
            mappingSource = "PactLookup";
        }

        var contracts = mapping.Contracts;
        var matchedMobile = mapping.MatchedMobile;
        if (contracts.Count == 0)
        {
            return Ok(Response(profile.CustomerKey, "NotMapped",
                $"PACT returned no contracts for tenant {tenantKey} under this customer's phone numbers, so no EDSM account can be confirmed.",
                tenantKey, retrievedAt, [], [], mapping.VerifiedAtUtc, mappingSource));
        }

        var distinct = contracts.DistinctBy(c => (c.CompanyId, c.ContractNumber, c.ExternalUnitId)).ToList();
        var companies = new List<CollectionsCompanyPaymentSummaryDto>();
        foreach (var group in distinct.Where(c => c.CompanyId is not null).GroupBy(c => c.CompanyId!.Value).OrderBy(g => g.Key))
        {
            companies.Add(await CompanyAsync(group.Key, tenantKey, tenantId, matchedMobile!, group.Select(ToRef).ToList(), cancellationToken));
        }

        if (companies.Any(c => c.Status is nameof(EdsmOutcome.BusinessRuleRejected) or nameof(EdsmOutcome.ValidationRejected)))
        {
            // EDSM refused a pair PACT gave us: the mapping may be out of date, so rediscover next time.
            mappingCache.Invalidate(cacheKey);
        }

        return Ok(Response(profile.CustomerKey, companies.Count == 0 ? "NotMapped" : "Mapped",
            companies.Count == 0 ? "PACT returned this tenant's contracts without a companyID, so no EDSM account can be confirmed." : null,
            tenantKey, retrievedAt, companies, distinct.Where(c => c.CompanyId is null).Select(ToRef).ToList(), mapping.VerifiedAtUtc, mappingSource));
    }

    /// <summary>
    /// Contract discovery: PACT <c>v1/contracts/{mobile}</c> for each of the profile's numbers,
    /// keeping only rows whose own tenantID is the stored tenant. Null when PACT could not answer at all.
    /// </summary>
    private async Task<PactAccountMapping?> DiscoverAsync(string tenantKey, IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken)
    {
        var contracts = new List<PactContractDto>();
        string? matchedMobile = null;
        var anyAnswered = false;
        foreach (var phone in phoneNumbers.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxPhoneLookups))
        {
            var lookup = await pact.SearchByMobileAsync(phone, cancellationToken);
            if (lookup.Outcome is PactCustomerLookupOutcome.Success or PactCustomerLookupOutcome.NotFound)
            {
                anyAnswered = true;
            }

            var mine = (lookup.Customers ?? [])
                .Where(c => string.Equals(c.PactCustomerId, tenantKey, StringComparison.Ordinal))
                .SelectMany(c => c.Contracts)
                .ToList();
            if (mine.Count > 0)
            {
                matchedMobile ??= CustomerPhoneNumber.WithoutPlus(phone);
                contracts.AddRange(mine);
            }
        }

        return anyAnswered || contracts.Count > 0 ? new PactAccountMapping(tenantKey, contracts, matchedMobile, clock.UtcNow) : null;
    }

    private async Task<CollectionsCompanyPaymentSummaryDto> CompanyAsync(
        int companyId, string tenantKey, long tenantId, string mobile, IReadOnlyList<CollectionsPactContractRefDto> contracts, CancellationToken cancellationToken)
    {
        if (EdsmCompanies.Find(companyId) is not { } company)
        {
            return new CollectionsCompanyPaymentSummaryDto(companyId, null, null, nameof(EdsmOutcome.NotSupported),
                $"EDSM does not support company {companyId}.", contracts, [], "NotChecked", false, [], [], null);
        }

        var summary = await edsm.GetPaymentSummaryAsync(companyId, tenantKey, cancellationToken);
        if (summary.Outcome != EdsmOutcome.Success || summary.Value is not { } s)
        {
            return new CollectionsCompanyPaymentSummaryDto(companyId, company.Name, company.Model.ToString(), summary.Outcome.ToString(),
                summary.Message, contracts, [], "NotChecked", false, [], [], null);
        }

        var fields = company.Model == EdsmBusinessModel.Owned ? OwnedFields(s) : RentedFields(s);
        var notes = new List<string>();

        var parts = new[] { s.TotalAmount, s.PaidAmount, s.DueAmount, s.OutstandingAmount };
        var totalCheck = parts.All(a => a.Status == EdsmAmountStatus.Provided)
            ? Math.Abs(s.TotalAmount.Value!.Value - (s.PaidAmount.Value!.Value + s.DueAmount.Value!.Value + s.OutstandingAmount.Value!.Value)) <= TotalTolerance
                ? "Consistent" : "Inconsistent"
            : "NotChecked";
        if (totalCheck == "Inconsistent")
        {
            notes.Add("EDSM's total does not equal paid + due + outstanding. Treat these figures with caution and report it to the EDSM owners.");
        }

        var provided = fields.Where(f => f.Status == nameof(EdsmAmountStatus.Provided)).ToList();
        var allZero = provided.Count > 0 && provided.All(f => f.Value == 0m);
        if (allZero)
        {
            notes.Add("EDSM returns zeros both for a zero balance and for a tenant it does not know. This account was confirmed through PACT contracts, not by these zeros.");
        }

        if (fields.Any(f => f.Status == nameof(EdsmAmountStatus.FormatNotConfigured)))
        {
            notes.Add("No EDSM number culture is configured (CollectionsSource:EdsmNumberCulture), so EDSM's formatted amounts are not read.");
        }

        if (company.Model == EdsmBusinessModel.Rented && s.LateFines.Status != EdsmAmountStatus.Empty)
        {
            notes.Add("EDSM's source code never sets late fines for rented companies, yet a value was returned. It is shown as received.");
        }

        var transactions = edsmOptions.TransactionsEnabled
            ? await TransactionsAsync(company, tenantKey, mobile, cancellationToken)
            : [];
        var due = await DueInstallmentsAsync(company, tenantId, cancellationToken);

        return new CollectionsCompanyPaymentSummaryDto(companyId, company.Name, company.Model.ToString(), "Available", null,
            contracts, fields, totalCheck, allZero, notes, transactions, due);
    }

    // ---- Owned (CompanyId 4, 32): contract §3.4, ReportsService.PaymentSummary.cs:9-43 ----
    private static IReadOnlyList<CollectionsEdsmFieldDto> OwnedFields(EdsmPaymentSummary s) =>
    [
        Field("paidAmount", "Paid", "Sum of all credits received (rows with Credit > 0).", s.PaidAmount),
        Field("dueAmount", "Due", "Unpaid remainder (Debit − Credit) of instalments whose cheque due date is on or before EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.", s.DueAmount),
        Field("outstandingAmount", "Not yet due", "Unpaid remainder (Debit − Credit) of instalments due after EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.", s.OutstandingAmount),
        Field("lateFines", "Late fines", "EDSM late fines (debits minus credits on rows whose description contains \"fine\"), shown only when above zero.", s.LateFines,
            s.LateFines.Status == EdsmAmountStatus.Empty ? "ZeroOrLess" : null),
        Field("totalAmount", "Total", "Paid + due + not yet due. Excludes late fines.", s.TotalAmount),
    ];

    // ---- Rented (CompanyId 25, 7, 20): contract §3.5, ReportsService.PaymentSummary.cs:44-173 ----
    private static IReadOnlyList<CollectionsEdsmFieldDto> RentedFields(EdsmPaymentSummary s) =>
    [
        Field("paidAmount", "Paid", "Posted receipts (excluding JVP, JRN, JVA and PDPV vouchers), minus refunds (IPV, PPV), plus fees paid (JRN), plus any opening-balance credit.", s.PaidAmount),
        Field("dueAmount", "Due", "Bounced cheques due on or before EDSM's server date (net of adjustments), plus unpaid fees, plus any opening-balance debit. Negative when fee payments exceed fee charges.", s.DueAmount),
        Field("outstandingAmount", "Post-dated cheques", "Post-dated cheques held (status pdc).", s.OutstandingAmount),
        Field("lateFines", "Late fines", "Not computed by EDSM for rented companies.", s.LateFines,
            s.LateFines.Status == EdsmAmountStatus.Empty ? "NotComputedForRented" : null),
        Field("totalAmount", "Total", "Paid + due + post-dated cheques.", s.TotalAmount),
    ];

    private static CollectionsEdsmFieldDto Field(string key, string label, string definition, EdsmAmount amount, string? meaning = null) =>
        new(key, label, definition, amount.Status.ToString(), amount.Value, amount.Raw, meaning);

    private async Task<IReadOnlyList<CollectionsEdsmTransactionListDto>> TransactionsAsync(
        EdsmCompany company, string tenantKey, string mobile, CancellationToken cancellationToken)
    {
        var lists = new List<CollectionsEdsmTransactionListDto>();
        foreach (var type in new[] { EdsmTransactionType.Paid, EdsmTransactionType.Due, EdsmTransactionType.Outstanding })
        {
            var result = await edsm.GetPaymentTransactionsAsync(company.CompanyId, tenantKey, mobile, type, cancellationToken);
            var caveat = (company.Model, type) switch
            {
                (EdsmBusinessModel.Rented, EdsmTransactionType.Paid) => "Refunds appear in this list as positive payments (an EDSM defect), so the list is not a ledger of payments received.",
                (EdsmBusinessModel.Rented, EdsmTransactionType.Due) => "EDSM's total for this list can differ from the summary's due amount (a fee-allocation defect). No total is taken from it.",
                _ => null
            };
            lists.Add(result is { Outcome: EdsmOutcome.Success, Value: { } value }
                ? new CollectionsEdsmTransactionListDto(type.ToString(), "Available", null, caveat, value.Items.Select(ToDto).ToList())
                : new CollectionsEdsmTransactionListDto(type.ToString(), result.Outcome.ToString(), result.Message, caveat, []));
        }

        return lists;
    }

    private static CollectionsEdsmTransactionDto ToDto(EdsmTransaction t) => new(
        t.Amount, t.FormattedAmount.Status.ToString(), t.FormattedAmount.Raw, t.Date, string.IsNullOrEmpty(t.DateRaw) ? null : t.DateRaw,
        t.ChequeNumber, t.PaymentTypeId switch
        {
            1 => "Cash",
            2 => "Cheque",
            3 => "Fees",
            4 => "Opening balance",
            5 => "Contract amount",
            null => null,
            var other => other.Value.ToString(CultureInfo.InvariantCulture)
        });

    private async Task<CollectionsEdsmDueInstallmentsDto?> DueInstallmentsAsync(EdsmCompany company, long tenantId, CancellationToken cancellationToken)
    {
        var today = clock.BusinessDate;
        var from = today.AddDays(-Math.Clamp(edsmOptions.DueInstallmentsLookbackDays, 0, 366));
        var to = today.AddDays(Math.Clamp(edsmOptions.DueInstallmentsLookaheadDays, 0, 366));

        if (!edsmOptions.DueInstallmentsEnabled)
        {
            return new CollectionsEdsmDueInstallmentsDto("Disabled", "Due-installments is off (CollectionsSource:DueInstallmentsEnabled).", from, to, []);
        }

        if (!company.SupportsDueInstallments)
        {
            return new CollectionsEdsmDueInstallmentsDto(nameof(EdsmOutcome.NotSupported), $"EDSM due-installments does not support company {company.CompanyId}.", from, to, []);
        }

        if (tenantId > int.MaxValue)
        {
            return new CollectionsEdsmDueInstallmentsDto("NotMatchable", "This tenant id exceeds EDSM's 32-bit due-installments tenantID, so rows cannot be matched.", from, to, []);
        }

        var result = await edsm.GetDueInstallmentsAsync(company.CompanyId, from, to, cancellationToken);
        if (result is not { Outcome: EdsmOutcome.Success, Value: { } rows })
        {
            return new CollectionsEdsmDueInstallmentsDto(result.Outcome.ToString(), result.Message, from, to, []);
        }

        // Company-wide response: keep only this company's rows for this tenant.
        var mine = rows
            .Where(r => r.CompanyId == company.CompanyId && r.TenantId == tenantId)
            .OrderBy(r => r.ChequeDueDate)
            .Select(r => new CollectionsEdsmDueInstallmentDto(r.UnitId, r.VoucherNumber?.Trim(), r.ChequeNumber, r.ChequeDueDate, r.Amount, r.Status))
            .ToList();
        return new CollectionsEdsmDueInstallmentsDto("Available", null, from, to, mine);
    }

    private static CollectionsPactContractRefDto ToRef(PactContractDto c) => new(c.ContractNumber, c.ExternalUnitId, c.UnitNumber, c.ProjectName, c.UnitType);

    private CollectionsPaymentSummaryResponseDto Response(
        string customerKey, string mappingStatus, string? detail, string? tenantId, DateTime retrievedAt,
        IReadOnlyList<CollectionsCompanyPaymentSummaryDto> companies, IReadOnlyList<CollectionsPactContractRefDto> withoutCompany,
        DateTime? mappingVerifiedAt = null, string? mappingSource = null) =>
        new(customerKey, mappingStatus, detail, tenantId, edsm.SourceName, retrievedAt, SourceAsOfUtc: null,
            edsmOptions.Currency, "Configured", EdsmAmountParser.ResolveCulture(edsmOptions.EdsmNumberCulture)?.Name,
            edsmOptions.SourceCacheMinutes, edsmOptions.MaxSourceDelayMinutes, mappingVerifiedAt, mappingSource, companies, withoutCompany);

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
