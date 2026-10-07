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
/// Directory reads retain their stored identity. Pre-ticket lookup reads may
/// associate CRM and PACT only through <c>CustomerIdentityLinker</c>'s fresh,
/// unambiguous evidence; this is not a persisted CRM-to-PACT crosswalk.
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
    PactAccountMappingCache mappingCache,
    CollectionsNextPaymentService? nextPaymentService = null)
{
    private readonly CollectionsNextPaymentService _nextPayment = nextPaymentService ?? new(edsmOptions, clock, edsm);

    public const string PactSource = "Pact";
    private const int MaxPhoneLookups = 5;
    private const decimal TotalTolerance = 0.03m;   // three independently rounded 2-dp strings

    /// <param name="caller">The authenticated caller.</param>
    /// <param name="customerKey">The TigerCS customer key, e.g. <c>ext:Pact:{tenantID}</c>.</param>
    /// <param name="includeTransactions">Also read payment-transactions types 1–3 per company (default true, as the Payment tab uses).</param>
    /// <param name="deadline">
    /// Overall budget for PACT discovery and every EDSM call together (default: the Payment tab's,
    /// <see cref="CollectionsEdsmOptions.WebReadDeadlineSeconds"/>). When it passes, what was read
    /// is returned and the rest is marked <c>DeadlineExceeded</c>; if the accounts could not even be
    /// confirmed, the answer is <see cref="CollectionsOutcome.FinanceUnavailable"/>.
    /// </param>
    /// <param name="cancellationToken">The caller's token (request aborted). Its cancellation propagates.</param>
    public async Task<CollectionsResult<CollectionsPaymentSummaryResponseDto>> GetAsync(
        CollectionsCaller caller, string customerKey, bool includeTransactions = true, TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = new ReadBudget(deadline ?? edsmOptions.ReadDeadline(CollectionsReadSurface.Web), cancellationToken);

        return await GetWithinBudgetAsync(caller, customerKey, includeTransactions, budget);
    }

    /// <summary>
    /// Pre-ticket financial read. Requires a fresh server-side lookup and an explicit
    /// financial grant; caller-supplied tenant/company mappings are never accepted.
    /// Existing directory reads keep their department visibility checks.
    /// </summary>
    public async Task<CollectionsResult<CollectionsPaymentSummaryResponseDto>> GetForLookupAsync(
        CollectionsCaller caller, string phoneNumber, string customerKey, CustomerSearchAppService search,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return Fail(CollectionsOutcome.Disabled);
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return Fail(CollectionsOutcome.Forbidden);
        if (string.IsNullOrWhiteSpace(phoneNumber) || !CustomerIdentity.TryParse(customerKey, out var identity)
            || identity.Kind == CustomerIdentityKind.Phone) return Fail(CollectionsOutcome.InvalidRequest);
        using var budget = new ReadBudget(edsmOptions.ReadDeadline(CollectionsReadSurface.Web), cancellationToken);
        try
        {
            var lookup = await search.SearchByPhoneAsync(phoneNumber, budget.Token);
            var buyer = identity.Kind == CustomerIdentityKind.Crm
                ? lookup.CrmBuyers.SingleOrDefault(b => b.Customer.CustomerId == identity.CrmBuyerCustomerId) : null;
            var pactSource = lookup.ExternalSources.FirstOrDefault(s => s.Source == PactSource);
            var pactCustomer = buyer is not null
                ? CustomerIdentityLinker.FindPactMatch(buyer, lookup.CrmBuyers, lookup.ExternalSources)
                : identity.Kind == CustomerIdentityKind.External && identity.ExternalSource == PactSource
                    ? pactSource?.Customers.SingleOrDefault(c => c.ExternalCustomerId == identity.ExternalCustomerId) : null;
            if (buyer is null && pactCustomer is null)
                return Fail(lookup.CrmStatus == "Failed" || pactSource?.Status == "Failed"
                    ? CollectionsOutcome.FinanceUnavailable : CollectionsOutcome.AccountNotFound);
            if (buyer is not null && pactCustomer is null && pactSource?.Status == "Failed")
                return Fail(CollectionsOutcome.FinanceUnavailable);
            var profile = new CustomerDirectoryProfileDto(customerKey, buyer is null ? "External" : "Crm",
                buyer?.Customer.FullNameEnglish ?? pactCustomer?.DisplayName,
                new[] { buyer?.Customer.MobileNumber, pactCustomer?.PhoneNumber, phoneNumber }
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct().ToList(), [],
                pactCustomer is null ? "Crm" : PactSource, buyer?.Customer.CustomerId,
                pactCustomer is null ? null : PactSource, pactCustomer?.ExternalCustomerId,
                0, 0, clock.UtcNow, clock.UtcNow, 0, [], [], []);
            return await GetWithinBudgetAsync(caller, customerKey, true, budget, profile);
        }
        catch (OperationCanceledException) when (budget.Expired)
        {
            return Fail(CollectionsOutcome.FinanceUnavailable, "The payment lookup deadline passed.");
        }
    }

    private async Task<CollectionsResult<CollectionsPaymentSummaryResponseDto>> GetWithinBudgetAsync(
        CollectionsCaller caller, string customerKey, bool includeTransactions, ReadBudget budget,
        CustomerDirectoryProfileDto? verifiedLookupProfile = null)
    {
        (Resolved? Value, (CollectionsOutcome Outcome, string? Detail)? Failure) resolution;
        try
        {
            resolution = await ResolveAsync(caller, customerKey, budget.Token, verifiedLookupProfile);
        }
        catch (OperationCanceledException) when (budget.Expired)
        {
            return Fail(CollectionsOutcome.FinanceUnavailable, $"No figures: {budget.Describe("before the customer's accounts were confirmed with PACT")}.");
        }

        if (resolution.Failure is { } failure)
        {
            return Fail(failure.Outcome, failure.Detail);
        }

        var r = resolution.Value!;
        var retrievedAt = clock.UtcNow;
        if (r.NotMappedDetail is { } notMapped)
        {
            var withoutCompany = r.Mapping is null ? [] : Distinct(r.Mapping).Where(c => c.CompanyId is null).Select(ToRef).ToList();
            return Ok(Response(r.Profile.CustomerKey, "NotMapped", notMapped, r.TenantKey, retrievedAt, [], withoutCompany, r.Mapping?.VerifiedAtUtc, r.MappingSource)
                with
                {
                    Completeness = CollectionsCompleteness.NoFigures,
                    IncompleteReasons = [],
                    NextPayment = CollectionsNextPaymentService.ForUnmapped(notMapped)
                });
        }

        var distinct = Distinct(r.Mapping!);
        var companies = new List<CollectionsCompanyPaymentSummaryDto>();
        foreach (var group in distinct.Where(c => c.CompanyId is not null).GroupBy(c => c.CompanyId!.Value).OrderBy(g => g.Key))
        {
            companies.Add(await CompanyAsync(group.Key, r.TenantKey!, r.TenantId, r.Mapping!.MatchedMobile!, group.Select(ToRef).ToList(),
                includeTransactions, budget));
        }

        if (companies.Any(c => c.Status is nameof(EdsmOutcome.BusinessRuleRejected) or nameof(EdsmOutcome.ValidationRejected)))
        {
            // EDSM refused a pair PACT gave us: the mapping may be out of date, so rediscover next time.
            mappingCache.Invalidate(r.CacheKey!);
        }

        var withoutCompanyId = distinct.Where(c => c.CompanyId is null).Select(ToRef).ToList();
        var reasons = IncompleteReasons(r.DiscoveryIncomplete, companies, withoutCompanyId.Count, budget);
        var completeness = !companies.Any(c => c.Status == "Available")
            ? CollectionsCompleteness.NoFigures
            : reasons.Count == 0 ? CollectionsCompleteness.Complete : CollectionsCompleteness.Partial;

        // Next payment: separate from the summary figures above, gated on confirmed EDSM semantics.
        // Asked about exactly the companies PACT confirmed for this tenant — never a guessed one.
        var nextPayment = await _nextPayment.ResolveAsync(
            distinct.Where(c => c.CompanyId is not null).Select(c => c.CompanyId!.Value).ToList(),
            r.TenantId, budget.Token, () => budget.Expired);

        return Ok(Response(r.Profile.CustomerKey, "Mapped", null, r.TenantKey, retrievedAt, companies,
            withoutCompanyId, r.Mapping!.VerifiedAtUtc, r.MappingSource)
            with { Completeness = completeness, IncompleteReasons = reasons, NextPayment = nextPayment });
    }

    /// <summary>
    /// What keeps a Mapped summary from being the whole picture. Anything listed here means the
    /// response must not be described as the customer's complete balance.
    /// </summary>
    private static List<string> IncompleteReasons(
        string? discoveryIncomplete, IReadOnlyList<CollectionsCompanyPaymentSummaryDto> companies, int contractsWithoutCompany, ReadBudget budget)
    {
        var reasons = new List<string>();
        if (discoveryIncomplete is not null)
        {
            reasons.Add(discoveryIncomplete);
        }

        if (contractsWithoutCompany > 0)
        {
            reasons.Add($"{contractsWithoutCompany} PACT contract(s) have no companyID, so EDSM cannot be asked about them.");
        }

        foreach (var c in companies)
        {
            var name = c.CompanyName is null ? $"Company {c.CompanyId}" : $"Company {c.CompanyId} ({c.CompanyName})";
            if (c.Status != "Available")
            {
                reasons.Add(c.Status == CollectionsCompleteness.DeadlineExceeded
                    ? $"{name}: not read; {budget.Describe("first")}."
                    : $"{name}: no figures ({c.Status}).");
                continue;
            }

            foreach (var list in c.Transactions.Where(l => l.Status != "Available"))
            {
                reasons.Add($"{name}: {list.TransactionType} transactions not read ({list.Status}).");
            }

            if (c.DueInstallments is { Status: not ("Available" or "Disabled" or "NotSupported" or "NotMatchable") } due)
            {
                reasons.Add($"{name}: due-installments not read ({due.Status}).");
            }
        }

        return reasons;
    }

    /// <summary>
    /// One read's overall deadline, linked to the caller's own token. Only the deadline is turned
    /// into a result; the caller's cancellation (a client disconnect) always propagates.
    /// </summary>
    private sealed class ReadBudget : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        public ReadBudget(TimeSpan deadline, CancellationToken caller)
        {
            Deadline = deadline;
            _caller = caller;
            _timer = new CancellationTokenSource(deadline);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timer.Token);
        }

        public TimeSpan Deadline { get; }

        public CancellationToken Token => _linked.Token;

        public bool Expired => _timer.IsCancellationRequested && !_caller.IsCancellationRequested;

        public string Describe(string when) =>
            $"the request's {Deadline.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s deadline passed {when}";

        /// <summary>The call's result, or null when the deadline passed before or during it.</summary>
        public async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> call) where T : class
        {
            _caller.ThrowIfCancellationRequested();
            if (Expired)
            {
                return null;
            }

            try
            {
                return await call(Token);
            }
            catch (OperationCanceledException) when (Expired)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }

    /// <summary>
    /// Read-only payment history for one of the customer's confirmed companies:
    /// EDSM payment-transactions type 1 (Paid), 2 (Due) or 3 (Outstanding).
    /// Type 4 ("All") is refused with <see cref="CollectionsOutcome.InvalidRequest"/>
    /// because, for rented companies, EDSM writes to its databases on that path.
    /// A company that is not among the tenant's PACT contracts is
    /// <see cref="CollectionsOutcome.AccountNotFound"/>. A customer without a
    /// verified PACT mapping is <see cref="CollectionsOutcome.NotMapped"/>.
    /// </summary>
    public async Task<CollectionsResult<CollectionsPaymentTransactionsResponseDto>> GetTransactionsAsync(
        CollectionsCaller caller, string customerKey, int? companyId, string? type, TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = new ReadBudget(deadline ?? edsmOptions.ReadDeadline(CollectionsReadSurface.Web), cancellationToken);
        try
        {
            return await GetTransactionsWithinAsync(caller, customerKey, companyId, type, budget);
        }
        catch (OperationCanceledException) when (budget.Expired)
        {
            return TxFail(CollectionsOutcome.FinanceUnavailable, $"No rows: {budget.Describe("before EDSM answered")}.");
        }
    }

    private async Task<CollectionsResult<CollectionsPaymentTransactionsResponseDto>> GetTransactionsWithinAsync(
        CollectionsCaller caller, string customerKey, int? companyId, string? type, ReadBudget budget)
    {
        var cancellationToken = budget.Token;
        if (!TryParseTransactionType(type, out var transactionType, out var typeError))
        {
            // Validate before any lookup: a refused type never triggers contract discovery.
            return TxFail(options.Enabled ? CollectionsOutcome.InvalidRequest : CollectionsOutcome.Disabled, typeError);
        }

        if (companyId is not > 0)
        {
            return TxFail(options.Enabled ? CollectionsOutcome.InvalidRequest : CollectionsOutcome.Disabled, "companyId is required: one of the customer's EDSM companies.");
        }

        var resolution = await ResolveAsync(caller, customerKey, cancellationToken);
        if (resolution.Failure is { } failure)
        {
            return TxFail(failure.Outcome, failure.Detail);
        }

        var r = resolution.Value!;
        if (r.NotMappedDetail is { } notMapped)
        {
            return TxFail(CollectionsOutcome.NotMapped, notMapped);
        }

        if (!Distinct(r.Mapping!).Any(c => c.CompanyId == companyId))
        {
            // With a partial discovery the company may simply be on a number PACT did not answer for.
            return r.DiscoveryIncomplete is { } partial
                ? TxFail(CollectionsOutcome.FinanceUnavailable, $"Company {companyId} could not be confirmed: {partial}")
                : TxFail(CollectionsOutcome.AccountNotFound, $"Company {companyId} is not among this customer's confirmed PACT contracts.");
        }

        if (EdsmCompanies.Find(companyId.Value) is not { } company)
        {
            return TxFail(CollectionsOutcome.FinanceUnavailable, $"EDSM does not support company {companyId}.");
        }

        var result = await edsm.GetPaymentTransactionsAsync(company.CompanyId, r.TenantKey!, r.Mapping!.MatchedMobile!, transactionType, cancellationToken);
        if (result is not { Outcome: EdsmOutcome.Success, Value: { } value })
        {
            if (result.Outcome is EdsmOutcome.BusinessRuleRejected or EdsmOutcome.ValidationRejected)
            {
                mappingCache.Invalidate(r.CacheKey!);
            }

            return TxFail(CollectionsOutcome.FinanceUnavailable, $"EDSM payment-transactions failed ({result.Outcome}): {result.Message}");
        }

        return CollectionsResult<CollectionsPaymentTransactionsResponseDto>.Ok(new CollectionsPaymentTransactionsResponseDto(
            r.Profile.CustomerKey, r.TenantKey!, company.CompanyId, company.Name, company.Model.ToString(), transactionType.ToString(), (int)transactionType,
            Caveat(company.Model, transactionType), edsmOptions.Currency, "Configured", edsm.SourceName, clock.UtcNow, SourceAsOfUtc: null,
            edsmOptions.MaxSourceDelayMinutes, r.Mapping.VerifiedAtUtc, r.MappingSource!, value.Items.Select(ToDto).ToList()));
    }

    /// <summary>Accepts Paid/Due/Outstanding or 1/2/3. 4 / "All" is refused.</summary>
    public static bool TryParseTransactionType(string? value, out EdsmTransactionType type, out string? error)
    {
        type = default;
        error = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "type is required: Paid (1), Due (2) or Outstanding (3).";
            return false;
        }

        if (text == "4" || string.Equals(text, "All", StringComparison.OrdinalIgnoreCase))
        {
            error = "type All (4) is not supported: for rented companies it writes to EDSM's databases. Use Paid (1), Due (2) or Outstanding (3).";
            return false;
        }

        type = text switch
        {
            "1" => EdsmTransactionType.Paid,
            "2" => EdsmTransactionType.Due,
            "3" => EdsmTransactionType.Outstanding,
            _ when Enum.TryParse<EdsmTransactionType>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(text, out _) => parsed,
            _ => (EdsmTransactionType)0
        };
        if (type == 0)
        {
            error = "type must be Paid (1), Due (2) or Outstanding (3).";
            return false;
        }

        return true;
    }

    private sealed record Resolved(
        CustomerDirectoryProfileDto Profile, string? TenantKey, long TenantId, string? CacheKey,
        PactAccountMapping? Mapping, string? MappingSource, string? NotMappedDetail, string? DiscoveryIncomplete = null);

    /// <summary>
    /// The shared front half of every EDSM read:
    /// <list type="number">
    /// <item>Collections enabled;</item>
    /// <item>the financial-read grant;</item>
    /// <item>the Customer Directory profile, under the caller's own visibility;</item>
    /// <item>the PACT identity;</item>
    /// <item>the verified mapping (cached, or discovered).</item>
    /// </list>
    /// </summary>
    private async Task<(Resolved? Value, (CollectionsOutcome Outcome, string? Detail)? Failure)> ResolveAsync(
        CollectionsCaller caller, string customerKey, CancellationToken cancellationToken,
        CustomerDirectoryProfileDto? verifiedLookupProfile = null)
    {
        if (!options.Enabled)
        {
            return (null, (CollectionsOutcome.Disabled, null));
        }

        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
        {
            return (null, (CollectionsOutcome.Forbidden, "Viewing customer payments requires the Collections financial-read permission."));
        }

        var profileResult = verifiedLookupProfile is null
            ? await directory.GetProfileAsync(caller.EmployeeId, caller.Roles, customerKey, cancellationToken)
            : CustomerDirectoryProfileResult.Success(verifiedLookupProfile);
        switch (profileResult.Outcome)
        {
            case CustomerDirectoryProfileOutcome.InvalidKey:
                return (null, (CollectionsOutcome.InvalidRequest, "customerKey is not a valid customer key."));
            case CustomerDirectoryProfileOutcome.Success when profileResult.Response is not null:
                break;
            default:
                return (null, (CollectionsOutcome.AccountNotFound, "Customer not found, or not visible to you."));
        }

        var profile = profileResult.Response;
        if (!string.Equals(profile.ExternalSource, PactSource, StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(profile.ExternalCustomerId, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId)
            || tenantId <= 0)
        {
            var why = profile.CrmBuyerCustomerId is { } crmId
                ? $"This customer is identified by Tiger CRM (customerId {crmId}). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists."
                : "This customer is not identified as a PACT tenant, so EDSM cannot be asked.";
            return (new Resolved(profile, null, 0, null, null, null, why), null);
        }

        var tenantKey = tenantId.ToString(CultureInfo.InvariantCulture);
        var cacheKey = PactAccountMappingCache.Key(tenantKey, profile.PhoneNumbers);
        var ttl = TimeSpan.FromMinutes(Math.Clamp(edsmOptions.PactMappingTtlMinutes, 1, 1440));
        var negativeTtl = TimeSpan.FromMinutes(Math.Clamp(edsmOptions.PactMappingNegativeTtlMinutes, 1, 60));

        var mapping = mappingCache.Get(cacheKey, ttl, negativeTtl);
        var mappingSource = "Cached";
        string? discoveryIncomplete = null;
        if (mapping is null)
        {
            var discovery = await DiscoverAsync(tenantKey, profile.PhoneNumbers, cancellationToken);
            if (discovery.Mapping is null)
            {
                // PACT unreachable: nothing is cached, so the next load has a reason to retry.
                return (null, (CollectionsOutcome.FinanceUnavailable,
                    "PACT could not be reached to confirm the customer's accounts, so EDSM figures are unavailable."));
            }

            mapping = discovery.Mapping;
            if (discovery.Failed == 0)
            {
                mappingCache.Set(cacheKey, mapping);
                mappingSource = "PactLookup";
            }
            else
            {
                // Some numbers went unanswered: what was found is real, but not the whole picture.
                // Never cached (it would hide accounts for the cache's lifetime), and never NotMapped.
                mappingSource = "PactLookupPartial";
                discoveryIncomplete =
                    $"PACT did not answer for {discovery.Failed} of the customer's {discovery.Asked} phone number(s), so other accounts may exist. This result was not cached.";
                if (!mapping.Contracts.Any(c => c.CompanyId is not null))
                {
                    return (null, (CollectionsOutcome.FinanceUnavailable,
                        $"The customer's accounts could not be confirmed: {discoveryIncomplete}"));
                }
            }
        }

        string? notMapped = mapping.Contracts.Count == 0
            ? $"PACT returned no contracts for tenant {tenantKey} under this customer's phone numbers, so no EDSM account can be confirmed."
            : mapping.Contracts.All(c => c.CompanyId is null)
                ? "PACT returned this tenant's contracts without a companyID, so no EDSM account can be confirmed."
                : null;
        return (new Resolved(profile, tenantKey, tenantId, cacheKey, mapping, mappingSource, notMapped, discoveryIncomplete), null);
    }

    private static List<PactContractDto> Distinct(PactAccountMapping mapping) =>
        mapping.Contracts.DistinctBy(c => (c.CompanyId, c.ContractNumber, c.ExternalUnitId)).ToList();

    private static CollectionsResult<CollectionsPaymentTransactionsResponseDto> TxFail(CollectionsOutcome outcome, string? detail) =>
        CollectionsResult<CollectionsPaymentTransactionsResponseDto>.Fail(outcome, detail);

    /// <summary>
    /// Contract discovery: PACT <c>v1/contracts/{mobile}</c> for each of the profile's numbers,
    /// keeping only rows whose own tenantID is the stored tenant. Mapping is null when PACT could not
    /// answer at all; <c>Failed</c> counts the numbers PACT did not answer for (timeout, outage, key).
    /// </summary>
    private async Task<(PactAccountMapping? Mapping, int Failed, int Asked)> DiscoverAsync(
        string tenantKey, IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken)
    {
        var contracts = new List<PactContractDto>();
        string? matchedMobile = null;
        var anyAnswered = false;
        var failed = 0;
        var phones = phoneNumbers.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxPhoneLookups).ToList();
        foreach (var phone in phones)
        {
            var lookup = await pact.SearchByMobileAsync(phone, cancellationToken);
            if (lookup.Outcome is PactCustomerLookupOutcome.Success or PactCustomerLookupOutcome.NotFound)
            {
                anyAnswered = true;
            }
            else
            {
                failed++;
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

        var mapping = anyAnswered || contracts.Count > 0 ? new PactAccountMapping(tenantKey, contracts, matchedMobile, clock.UtcNow) : null;
        return (mapping, failed, phones.Count);
    }

    private async Task<CollectionsCompanyPaymentSummaryDto> CompanyAsync(
        int companyId, string tenantKey, long tenantId, string mobile, IReadOnlyList<CollectionsPactContractRefDto> contracts,
        bool includeTransactions, ReadBudget budget)
    {
        if (EdsmCompanies.Find(companyId) is not { } company)
        {
            return new CollectionsCompanyPaymentSummaryDto(companyId, null, null, nameof(EdsmOutcome.NotSupported),
                $"EDSM does not support company {companyId}.", contracts, [], "NotChecked", false, [], [], null);
        }

        if (await budget.RunAsync(ct => edsm.GetPaymentSummaryAsync(companyId, tenantKey, ct)) is not { } summary)
        {
            // No figures, never zeros: the fields list stays empty.
            return new CollectionsCompanyPaymentSummaryDto(companyId, company.Name, company.Model.ToString(), CollectionsCompleteness.DeadlineExceeded,
                $"Not read: {budget.Describe("before EDSM answered for this company")}.", contracts, [], "NotChecked", false, [], [], null);
        }

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

        var transactions = edsmOptions.TransactionsEnabled && includeTransactions
            ? await TransactionsAsync(company, tenantKey, mobile, budget)
            : [];
        var due = await DueInstallmentsAsync(company, tenantId, budget);

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
        EdsmCompany company, string tenantKey, string mobile, ReadBudget budget)
    {
        var lists = new List<CollectionsEdsmTransactionListDto>();
        foreach (var type in new[] { EdsmTransactionType.Paid, EdsmTransactionType.Due, EdsmTransactionType.Outstanding })
        {
            var result = await budget.RunAsync(ct => edsm.GetPaymentTransactionsAsync(company.CompanyId, tenantKey, mobile, type, ct));
            var caveat = Caveat(company.Model, type);
            if (result is null)
            {
                lists.Add(new CollectionsEdsmTransactionListDto(type.ToString(), CollectionsCompleteness.DeadlineExceeded,
                    $"Not read: {budget.Describe("before EDSM answered")}.", caveat, []));
                continue;
            }

            lists.Add(result is { Outcome: EdsmOutcome.Success, Value: { } value }
                ? new CollectionsEdsmTransactionListDto(type.ToString(), "Available", null, caveat, value.Items.Select(ToDto).ToList())
                : new CollectionsEdsmTransactionListDto(type.ToString(), result.Outcome.ToString(), result.Message, caveat, []));
        }

        return lists;
    }

    private static string? Caveat(EdsmBusinessModel model, EdsmTransactionType type) => (model, type) switch
    {
        (EdsmBusinessModel.Rented, EdsmTransactionType.Paid) => "Refunds appear in this list as positive payments (an EDSM defect), so the list is not a ledger of payments received.",
        (EdsmBusinessModel.Rented, EdsmTransactionType.Due) => "EDSM's total for this list can differ from the summary's due amount (a fee-allocation defect). No total is taken from it.",
        _ => null
    };

    private static CollectionsEdsmTransactionDto ToDto(EdsmTransaction t) => new(
        t.Amount, t.FormattedAmount.Status.ToString(), t.FormattedAmount.Raw, t.Date, string.IsNullOrEmpty(t.DateRaw) ? null : t.DateRaw,
        t.ChequeNumber, PaymentTypeName(t.PaymentTypeId), t.PaymentTypeId);

    /// <summary>EDSM's <c>PaymentTypeEnum</c> (contract §5.3). Never inferred from the list or the cheque number.</summary>
    public static string? PaymentTypeName(int? paymentTypeId) => paymentTypeId switch
    {
        1 => "Cash",
        2 => "Cheque",
        3 => "Fees",
        4 => "Opening balance",
        5 => "Current contract amount",
        null => null,
        var other => $"Unknown ({other.Value.ToString(CultureInfo.InvariantCulture)})"
    };

    private async Task<CollectionsEdsmDueInstallmentsDto?> DueInstallmentsAsync(EdsmCompany company, long tenantId, ReadBudget budget)
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

        var result = await budget.RunAsync(ct => edsm.GetDueInstallmentsAsync(company.CompanyId, from, to, ct));
        if (result is null)
        {
            return new CollectionsEdsmDueInstallmentsDto(CollectionsCompleteness.DeadlineExceeded,
                $"Not read: {budget.Describe("before EDSM answered")}.", from, to, []);
        }

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
