using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The single customer / unit linking used by the Customer Payment tab, the New Ticket flow and the Payment Summary lookups. It never matches a customer's money by phone:
/// <list type="number">
/// <item>The phone only finds the CUSTOMER and their units. CRM is searched first; only eligible sales count (Sold / Contract, not cancelled, buyer).</item>
/// <item>Each CRM unit is tied to PACT by its verified project mapping (<see cref="ICollectionsCrmProjectMap"/>) + apartment number: <c>TP140</c> + <c>101</c> = <c>TP140-101</c>, in the company
/// <c>CollectionsTowers</c> / the mapping names. Apartment number alone is never enough; an unmapped project or an unclear company is a review state, never a guess.</item>
/// <item>The financial result is the PACT account of that company + unit (the same snapshot read as the Receivables page and the unit Payment Summary), so it does not matter whether
/// the CRM phone exists in PACT.</item>
/// <item>Only when CRM holds no eligible customer is PACT searched by phone - companies 4 / 32 through the same unit read, Leasing (company 7) as its own contract entries.</item>
/// <item>More than one customer / unit / contract = a list the caller must choose from; the first is never taken.</item>
/// </list>
/// </summary>
public sealed class CustomerUnitLinkService(
    CollectionsOptions options, PactReceivablesOptions sourceOptions, ReceivablesSnapshotOptions snapshotOptions, CollectionsCrmOwnersOptions crmOptions,
    CollectionsAuthorizationService authorization, CollectionsClock clock, ICrmBuyerLookupGateway crm, IPactCustomerLookupGateway pact,
    ICollectionsCrmProjectMap projectMap, ICollectionsTowerCatalog towers, IPactReceivablesSource source, ILogger<CustomerUnitLinkService> logger)
{
    public const int LeasingCompanyId = 7;
    private const int MaxCandidates = 25;

    public const string LeasingSourceMissingDetail =
        "No company-7 (Leasing) receivables source exists: the PACT receivables snapshot and its refresh procedures (p4AccountReceivablesV2, p32AccountReceivablesV2) cover companies 4 and 32 only, "
        + "and the repository holds no company-7 procedure. A PACTRPT procedure for company 7 with the same columns is needed. This is not a zero balance.";

    /// <summary>Customer found by a phone number the user typed (customer search, New Ticket).</summary>
    public async Task<CollectionsResult<CustomerUnitLinkResultDto>> FindByPhoneAsync(
        CollectionsCaller caller, string? phone, string? selectionId, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(caller, cancellationToken);
        if (gate is not null) return gate;
        var normalized = NormalizePhone(phone);
        if (normalized.Length == 0)
            return CollectionsResult<CustomerUnitLinkResultDto>.Fail(CollectionsOutcome.InvalidRequest, "Give a valid mobile number (for example +971501234567 or 0501234567).");
        return CollectionsResult<CustomerUnitLinkResultDto>.Ok(await LinkAsync(normalized, phone!.Trim(), null, selectionId, cancellationToken));
    }

    /// <summary>
    /// A CRM customer already known (Customer Profile, ticket): its units come from CRM through any phone TigerCS holds for it. Nothing here needs the phone to exist in PACT.
    /// </summary>
    public async Task<CollectionsResult<CustomerUnitLinkResultDto>> FindForCrmCustomerAsync(
        CollectionsCaller caller, int crmCustomerId, IReadOnlyList<string> phones, string? selectionId, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(caller, cancellationToken);
        if (gate is not null) return gate;
        var usable = phones.Select(p => (Typed: p.Trim(), Normalized: NormalizePhone(p))).Where(p => p.Normalized.Length > 0).DistinctBy(p => p.Normalized).ToList();
        if (usable.Count == 0)
            return CollectionsResult<CustomerUnitLinkResultDto>.Ok(new("", "NotSearched", "NotSearched", [], false, null,
                ["TigerCS holds no usable phone number for this CRM customer, so CRM cannot be asked for their units."]));
        return CollectionsResult<CustomerUnitLinkResultDto>.Ok(await LinkAsync(usable[0].Normalized, usable[0].Typed, (crmCustomerId, usable.Skip(1).Select(p => p.Typed).ToList()), selectionId, cancellationToken));
    }

    private async Task<CollectionsResult<CustomerUnitLinkResultDto>?> GateAsync(CollectionsCaller caller, CancellationToken cancellationToken)
    {
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<CustomerUnitLinkResultDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<CustomerUnitLinkResultDto>.Fail(CollectionsOutcome.Disabled);
        return null;
    }

    /// <summary>The one phone normalisation of Collections (E.164 where it can be confirmed); a number that cannot be confirmed but looks like one keeps its digits so CRM can still be asked.</summary>
    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var e164 = CollectionsContactNormalizer.NormalizePhone(phone);
        if (e164.Length > 0) return e164;
        return TigerCS.Domain.Modules.Ticketing.CustomerPhoneNumber.LooksLikeNumber(phone) ? TigerCS.Domain.Modules.Ticketing.CustomerPhoneNumber.Normalize(phone) : "";
    }

    private async Task<CustomerUnitLinkResultDto> LinkAsync(
        string normalized, string typed, (int CustomerId, List<string> MorePhones)? onlyCustomer, string? selectionId, CancellationToken cancellationToken)
    {
        var today = clock.BusinessDate;
        var notes = new List<string>();
        var candidates = new List<LinkedUnitCandidateDto>();
        var crmStatus = "NotFound";

        var crmResult = await crm.GetBuyerByPhoneAsync(typed, cancellationToken);
        if (onlyCustomer is { } only && !(crmResult.Buyers ?? []).Any(b => b.Customer.CustomerId == only.CustomerId))
            foreach (var more in only.MorePhones)
            {
                crmResult = await crm.GetBuyerByPhoneAsync(more, cancellationToken);
                if ((crmResult.Buyers ?? []).Any(b => b.Customer.CustomerId == only.CustomerId)) break;
            }

        List<CrmBuyerMatchDto> buyers = crmResult.Outcome == CrmBuyerLookupOutcome.Success ? [.. crmResult.Buyers ?? []] : [];
        if (onlyCustomer is { } wanted) buyers = buyers.Where(b => b.Customer.CustomerId == wanted.CustomerId).ToList();
        var byCustomer = buyers.GroupBy(b => b.Customer.CustomerId).ToList();
        if (crmResult.Outcome is CrmBuyerLookupOutcome.Unavailable or CrmBuyerLookupOutcome.Unauthorized or CrmBuyerLookupOutcome.InvalidResponse)
        {
            crmStatus = "Unavailable";
            notes.Add("CRM could not be read; its customers and units are not shown.");
        }
        else if (crmResult.Outcome == CrmBuyerLookupOutcome.AmbiguousCustomerMatch)
        {
            crmStatus = "Ambiguous";
            notes.Add("Several CRM customers share this number; none is chosen.");
        }
        else if (byCustomer.Count > 0)
        {
            var anyEligible = false;
            var ineligible = 0;
            foreach (var group in byCustomer)
            {
                var customer = group.First().Customer;
                var units = group.SelectMany(b => b.Units).GroupBy(u => u.UnitId).Select(g => g.First()).ToList();
                var eligible = units.Where(u => IsEligibleSale(u, crmOptions)).ToList();
                ineligible += units.Count - eligible.Count;
                if (eligible.Count > 0) anyEligible = true;
                candidates.AddRange(await CrmCandidatesAsync(customer, eligible, today, cancellationToken));
            }
            crmStatus = anyEligible ? "Found" : "NoEligibleUnits";
            if (ineligible > 0) notes.Add($"{ineligible} CRM unit(s) were ignored: only Sold / Contract sales that are not cancelled and belong to a buyer count.");
        }

        var pactStatus = "NotSearched";
        if (candidates.Count == 0 && onlyCustomer is null)
        {
            // No eligible CRM customer: PACT by phone, which also finds Leasing (company 7) tenants that CRM does not know.
            var lookup = await pact.SearchByMobileAsync(normalized, cancellationToken);
            switch (lookup.Outcome)
            {
                case PactCustomerLookupOutcome.Success when lookup.Customers is { Count: > 0 }:
                    pactStatus = "Found";
                    candidates.AddRange(await PactCandidatesAsync(lookup.Customers, today, notes, cancellationToken));
                    break;
                case PactCustomerLookupOutcome.NotFound or PactCustomerLookupOutcome.Success:
                    pactStatus = "NotFound";
                    break;
                default:
                    pactStatus = "Unavailable";
                    notes.Add("PACT could not be read by phone.");
                    break;
            }
        }

        if (candidates.Count > MaxCandidates)
        {
            notes.Add($"{candidates.Count - MaxCandidates} further candidates were not listed; narrow the search.");
            candidates = candidates.Take(MaxCandidates).ToList();
        }
        var selected = selectionId is null ? null : candidates.FirstOrDefault(c => c.SelectionId == selectionId)?.SelectionId;
        if (selectionId is not null && selected is null) notes.Add("The selected unit is not among the customer's current units.");
        selected ??= candidates.Count == 1 ? candidates[0].SelectionId : null;
        return new CustomerUnitLinkResultDto(normalized, crmStatus, pactStatus, candidates, candidates.Count > 1 && selected is null, selected, notes);
    }

    /// <summary>A buyer's Sold or Contract lead that is not cancelled (<see cref="CrmSaleEligibility"/>: the status name decides, the configured numbers only when no name was sent).</summary>
    public static bool IsEligibleSale(CrmBuyerUnitDto unit, CollectionsCrmOwnersOptions crmOptions) =>
        unit.UnitId > 0 && unit.CustomerType == crmOptions.BuyerCustomerType && CrmSaleEligibility.IsEligible(unit.LeadStatus, unit.LeadStatusName, crmOptions.EligibleLeadStatuses);

    // ---- CRM units ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    private async Task<List<LinkedUnitCandidateDto>> CrmCandidatesAsync(CrmCustomerDto customer, List<CrmBuyerUnitDto> units, DateOnly today, CancellationToken cancellationToken)
    {
        var result = new List<LinkedUnitCandidateDto>();
        if (units.Count == 0) return result;
        var crmContact = new SourceContact(customer.FullNameEnglish?.Trim() is { Length: > 0 } en ? en : customer.FullNameArabic?.Trim() ?? "",
            CollectionsContactNormalizer.NormalizePhone(customer.MobileNumber ?? ""), CollectionsContactNormalizer.NormalizeEmail(customer.Email ?? ""));
        IReadOnlyDictionary<int, IReadOnlyList<CrmProjectTowerMapping>> maps;
        IReadOnlyList<CollectionsTowerDto> catalog;
        try
        {
            maps = await projectMap.GetAsync(units.Select(u => u.ProjectId).Distinct().ToList(), cancellationToken);
            catalog = await towers.ListActiveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning("The CRM project mapping could not be read ({ExceptionType}).", ex.GetType().Name);
            return units.Select(u => Failed(customer, crmContact, u, "SourceError", "ProjectMappingUnreadable", "The CRM project to PACT tower mapping could not be read. Please retry.")).ToList();
        }

        foreach (var unit in units.OrderBy(u => u.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.UnitNumber, StringComparer.OrdinalIgnoreCase))
        {
            maps.TryGetValue(unit.ProjectId, out var rows);
            var distinct = (rows ?? []).DistinctBy(r => (CollectionsUnitKey.Normalize(r.ProjectCode), r.CompanyId)).ToList();
            if (distinct.Count == 0)
            {
                result.Add(Failed(customer, crmContact, unit, "MatchFailed", "ProjectMappingMissing",
                    $"CRM project {unit.ProjectId} ({unit.ProjectName}) has no verified mapping to a PACT tower, so its PACT unit cannot be identified. No amounts were looked up and none are implied."));
                continue;
            }
            if (distinct.Select(r => CollectionsUnitKey.Normalize(r.ProjectCode)).Distinct().Count() > 1)
            {
                result.Add(Failed(customer, crmContact, unit, "MatchFailed", "ProjectMappingAmbiguous",
                    $"CRM project {unit.ProjectId} maps to several PACT towers ({string.Join(", ", distinct.Select(r => r.ProjectCode))}); none is chosen."));
                continue;
            }
            var map = distinct[0];
            var key = CollectionsUnitKey.FromCrm(map.ProjectCode, unit.UnitNumber);
            if (key is null || !CollectionsUnitKey.IsListable(key))
            {
                result.Add(Failed(customer, crmContact, unit, "MatchFailed", "UnitCodeInvalid", $"CRM unit {unit.UnitNumber} of project {unit.ProjectId} does not form a valid PACT unit code."));
                continue;
            }
            var tower = CollectionsTowerNumber.Parse(key);
            var companies = map.CompanyId is { } explicitCompany ? [explicitCompany]
                : catalog.Where(t => CollectionsTowerNumber.Matches(tower, t.TowerNumber)).Select(t => t.CompanyId).Distinct().OrderBy(c => c).ToList();
            if (companies.Count > 1)
            {
                result.Add(Failed(customer, crmContact, unit, "MatchFailed", "CompanyMappingAmbiguous",
                    $"Tower {tower} exists in PACT companies {string.Join(" and ", companies)} and the CRM mapping does not say which; none is chosen.", key, tower));
                continue;
            }
            var read = await ReadUnitAsync(key, companies.Count == 1 ? companies[0] : null, today, cancellationToken);
            if (read.Error is { } error)
            {
                result.Add(Failed(customer, crmContact, unit, "SourceError", error.Reason, error.Detail, key, tower));
                continue;
            }
            var summary = CollectionsUnitPaymentSummaryAppService.Compose(key, today, read.Data!, snapshotOptions.MaxAgeMinutes, new KnownCrmCustomer(customer.CustomerId, unit.UnitId, crmContact));
            result.Add(FromSummary($"crm:{unit.UnitId}", "Crm", summary, unit.ProjectName, unit.UnitNumber, customer.CustomerId, unit.UnitId, unit.ProjectId, null, null));
        }
        return result;
    }

    private LinkedUnitCandidateDto Failed(CrmCustomerDto customer, SourceContact contact, CrmBuyerUnitDto unit, string financialStatus, string reason, string detail, string? key = null, string? tower = null) =>
        new($"crm:{unit.UnitId}", "Crm", null, tower, null, unit.ProjectName, unit.UnitNumber, key,
            financialStatus == "MatchFailed" ? "MatchFailed" : "Linked", [reason],
            new CollectionsUnitPartyDto(contact.Name, contact.Phone, contact.Email, contact.Name.Length > 0 ? "Crm" : "", contact.Phone.Length > 0 ? "Crm" : "", contact.Email.Length > 0 ? "Crm" : ""),
            customer.CustomerId, unit.UnitId, unit.ProjectId, null, null, null, null,
            financialStatus, reason, detail, null, null, null, [], clock.BusinessDate, "Missing");

    // ---- PACT contracts found by phone (companies 4 / 32 and Leasing 7) -------------------------------------------------------------------------------------------------------------

    private async Task<List<LinkedUnitCandidateDto>> PactCandidatesAsync(
        IReadOnlyList<PactCustomerMatchDto> customers, DateOnly today, List<string> notes, CancellationToken cancellationToken)
    {
        var result = new List<LinkedUnitCandidateDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int ended = 0, former = 0;
        foreach (var customer in customers)
        {
            var contact = new SourceContact(customer.DisplayName?.Trim() ?? "", CollectionsContactNormalizer.NormalizePhone(customer.PhoneNumber ?? ""), CollectionsContactNormalizer.NormalizeEmail(customer.Email ?? ""));
            foreach (var contract in customer.Contracts)
            {
                // A former tenant: the contract ended before today (Dubai). PACT's contract list carries no cancellation flag, so a cancelled apartment is recognised only by its '*' code.
                if (contract.ContractEndDate is { } end && end < today) { ended++; continue; }
                if (contract.UnitCode?.Contains('*') == true) { former++; continue; }
                var id = $"pact:{contract.CompanyId}:{customer.PactCustomerId}:{contract.ExternalUnitId}:{contract.ContractNumber}";
                if (!seen.Add(id)) continue;
                result.Add(await PactCandidateAsync(id, customer, contact, contract, today, cancellationToken));
            }
        }
        if (ended > 0) notes.Add($"{ended} contract(s) that ended before today were excluded (a former tenant's data is not used).");
        if (former > 0) notes.Add($"{former} cancelled apartment(s) (code containing '*') were excluded.");
        return result;
    }

    private async Task<LinkedUnitCandidateDto> PactCandidateAsync(
        string id, PactCustomerMatchDto customer, SourceContact contact, PactContractDto contract, DateOnly today, CancellationToken cancellationToken)
    {
        LinkedUnitCandidateDto Fail(string source, string status, string reason, string detail, string? key = null) =>
            new(id, source, contract.CompanyId, key is null ? null : CollectionsTowerNumber.Parse(key), null, contract.ProjectName, contract.UnitNumber, key,
                status == "MatchFailed" ? "MatchFailed" : "Linked", [reason],
                new CollectionsUnitPartyDto(contact.Name, contact.Phone, contact.Email, contact.Name.Length > 0 ? "Pact" : "", contact.Phone.Length > 0 ? "Pact" : "", contact.Email.Length > 0 ? "Pact" : ""),
                null, null, null, customer.PactCustomerId, int.TryParse(contract.ExternalUnitId, out var unitId) ? unitId : null, contract.ContractNumber, contract.ContractEndDate,
                status, reason, detail, null, null, null, [], today, "Missing");

        if (contract.CompanyId is null)
            return Fail("Pact", "MatchFailed", "CompanyMissing", "PACT sent the contract without a company, so its receivables cannot be looked up; none is guessed.");
        if (contract.CompanyId == LeasingCompanyId)
            return Fail("Leasing", "NoFinancialData", "LeasingReceivablesSourceMissing", LeasingSourceMissingDetail, CollectionsUnitKey.Normalize(contract.UnitCode));
        if (contract.CompanyId is not (4 or 32))
            return Fail("Pact", "NoFinancialData", "CompanyNotSupported", $"PACT company {contract.CompanyId} has no receivables source in TigerCS (supported: 4, 32 and, once it exists, 7). This is not a zero balance.");
        var key = CollectionsUnitKey.Normalize(contract.UnitCode);
        if (key is null || !CollectionsUnitKey.IsListable(key))
            return Fail("Pact", "MatchFailed", "UnitCodeMissing", "PACT sent the contract without a usable unit code, so the unit cannot be tied to its receivables.");
        var read = await ReadUnitAsync(key, contract.CompanyId, today, cancellationToken);
        if (read.Error is { } error) return Fail("Pact", "SourceError", error.Reason, error.Detail, key);
        // Only this contract's tenant: a former tenant of the same unit never contributes to it.
        var summary = CollectionsUnitPaymentSummaryAppService.Compose(key, today, read.Data!, snapshotOptions.MaxAgeMinutes, tenantId: customer.PactCustomerId);
        return FromSummary(id, "Pact", summary, contract.ProjectName, contract.UnitNumber, null, null, null, contract.ContractNumber, contract.ContractEndDate, contact);
    }

    // ---- shared ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private sealed record UnitRead(PactUnitReceivables? Data, (string Reason, string Detail)? Error);

    private async Task<UnitRead> ReadUnitAsync(string key, int? companyId, DateOnly today, CancellationToken cancellationToken)
    {
        if (!sourceOptions.Enabled || source is not IUnitReceivablesSource unitSource)
            return new(null, ("UnitReceivablesSourceNotConfigured", "The local PACT receivables snapshot is not enabled, so no unit can be read. This is not a zero balance."));
        try { return new(await unitSource.ReadUnitAsync(key, companyId, today, cancellationToken), null); }
        catch (PactReceivablesSourceException ex) { return new(null, ("PactUnavailable", ex.Message)); }
        catch (Exception ex) when (ex is DbException)
        {
            logger.LogWarning("Unit read failed ({ExceptionType}).", ex.GetType().Name);
            return new(null, ("PactUnavailable", "The unit could not be read from the PACT receivables snapshot. Please retry."));
        }
    }

    private LinkedUnitCandidateDto FromSummary(
        string id, string source, CollectionsUnitPaymentSummaryDto s, string? projectName, string? unitNumber, int? crmCustomerId, int? crmUnitId, int? crmProjectId,
        string? contractNumber, DateOnly? contractEnd, SourceContact? pactContact = null)
    {
        // A Withheld unit (several PACT accounts) is an ambiguous match, not missing data.
        var financial = s.FinancialStatus == "Withheld" ? "MatchFailed" : s.FinancialStatus;
        var reasons = s.ReviewReasons.ToList();
        var party = s.Customer;
        if (pactContact is not null && party.Name.Length + party.Mobile.Length + party.Email.Length == 0)
            party = new CollectionsUnitPartyDto(pactContact.Name, pactContact.Phone, pactContact.Email, pactContact.Name.Length > 0 ? "Pact" : "", pactContact.Phone.Length > 0 ? "Pact" : "", pactContact.Email.Length > 0 ? "Pact" : "");
        var linkStatus = financial == "MatchFailed" ? "MatchFailed" : reasons.Count > 0 ? "NeedsReview" : "Linked";
        return new(id, source, s.CompanyId, s.TowerNumber, s.TowerName, projectName, unitNumber, s.UnitKey, linkStatus, reasons, party,
            crmCustomerId ?? s.CrmCustomerId, crmUnitId ?? s.CrmUnitId, crmProjectId, s.PactTenantId, s.PactUnitId, contractNumber, contractEnd,
            financial, s.FinancialReason, s.FinancialDetail, s.Due, s.Overdue, s.Total, s.Instalments, s.AsOf, s.PactFreshness);
    }
}
