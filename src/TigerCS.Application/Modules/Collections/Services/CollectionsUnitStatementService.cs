using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.Services;

/// <param name="CompanyId">The unit's PACT company when already known (null: decided from PACT's contract rows).</param>
/// <param name="UnitKey">The normalised PACT unit code (<c>TP140-909</c>).</param>
/// <param name="PactTenantId">The tenant the collections snapshot holds for the unit, when known: it must be the tenant PACT's contracts name, otherwise nothing is shown.</param>
/// <param name="Phones">Phone numbers the customer is known by (E.164); PACT's contract list is asked with them.</param>
public sealed record UnitStatementRequest(int? CompanyId, string UnitKey, string? PactTenantId, IReadOnlyList<string> Phones)
{
    /// <summary>A mobile from CRM for the buyer, used only when the unit's own PACT contact has none (never required).</summary>
    public string? FallbackMobile { get; init; }
}

/// <summary>
/// The complete payment statement of ONE unit (Due | Outstanding | Paid | Fines | Total, and the payment rows), from the authoritative PACT source the CRM "Pact Payment" screen uses:
/// EDSM's payment summary and payment transactions (<see cref="CollectionsPaymentSummaryAppService.ReadAccountAsync"/>) - never from the Due / Overdue collections snapshot,
/// which holds unpaid remainders only.
///
/// <para><b>Unit isolation.</b> EDSM aggregates per (company, tenant), not per unit (EDSM contract: "granularity UNVERIFIED"), so it has no unit-scoped route. The statement is shown ONLY
/// when PACT's own contract list (<c>v1/contracts/{mobile}</c>, which returns ALL of a tenant's contracts / units) proves that the (company, tenant) account has exactly one unit: the
/// selected one. Another unit, a parking contract, an ended or cancelled contract of the same tenant, a contract without a unit code, or an incomplete PACT answer all withhold the
/// amounts and say why: another unit's money is never shown, and a missing figure is never shown as zero.</para>
/// </summary>
public sealed class CollectionsUnitStatementService(
    CollectionsEdsmOptions edsmOptions, CollectionsClock clock, PactContractDiscovery discovery, CollectionsPaymentSummaryAppService account,
    IEdsmCollectionsGateway edsm, IPactReceivablesSource receivables, ILogger<CollectionsUnitStatementService> logger)
{
    /// <summary>EDSM's payment-transactions route requires a Mobile parameter but uses it only for RENTED companies' "All" type (contract section 5.2); the statement asks Paid/Due/Outstanding of OWNED companies only, so a unit whose PACT and CRM contacts hold no mobile sends this neutral value. To be confirmed live.</summary>
    public const string MobileNotUsed = "0";

    public async Task<UnitPaymentStatementDto> GetAsync(UnitStatementRequest request, CancellationToken cancellationToken)
    {
        if (edsm.SourceName == "Unavailable")
            return UnitPaymentStatementDto.Withheld("NotConfigured", "The PACT payment source (EDSM) is not configured in this environment, so no payment statement can be read.", request.UnitKey, request.CompanyId);
        // A PACT unit code containing * is cancelled: no statement, and the mark is never removed to reach an active unit.
        if (!CollectionsUnitKey.IsListable(request.UnitKey))
            return UnitPaymentStatementDto.Withheld("UnitNotResolved", "A unit code containing * is cancelled in PACT: no payment statement is shown for it.", request.UnitKey, request.CompanyId);

        // 1. The account comes from the unit itself: the exact unit code in the PACT unit/account DIRECTORY (fully paid units included) gives CompanyId, TenantId and UnitId. No phone is needed.
        PactAccountDirectory? directory = null;
        if (receivables is IUnitReceivablesSource unitSource)
        {
            try { directory = await unitSource.ReadAccountDirectoryAsync(request.UnitKey, request.CompanyId, cancellationToken); }
            catch (PactReceivablesSourceException ex)
            {
                logger.LogWarning("The PACT unit/account directory could not be read for the statement ({Message}).", ex.Message);
                return UnitPaymentStatementDto.Withheld("SourceUnavailable", "The PACT unit/account directory could not be read, so the unit's account cannot be identified.", request.UnitKey, request.CompanyId);
            }
        }

        // 2. Optional extra proof, never a gate: the customer's PACT contract list (when a phone is known) lists every unit / contract of the tenant, including ones the directory does not hold.
        PactContractDiscoveryResult? found = null;
        if (request.Phones.Count > 0)
        {
            try { found = await discovery.DiscoverAsync(request.Phones, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            { logger.LogWarning("PACT contract discovery failed ({ExceptionType}); the statement continues on the unit data alone.", ex.GetType().Name); }
        }

        var scope = directory is { Matches.Count: > 0 }
            ? ResolveFromDirectory(directory, found, request, clock.BusinessDate)
            : found is { Customers.Count: > 0 } ? Resolve(found, request, clock.BusinessDate)
            : new Scope(null, UnitPaymentStatementDto.Withheld("UnitNotFound",
                $"The PACT unit/account directory (receivables procedure, settled instalments included) holds no unit {request.UnitKey}" + (request.Phones.Count == 0 ? ", and no customer phone is known to look it up in PACT's contract list." : ", and PACT's contract list for the customer's phone does not hold it either.")
                + " Units that have no payment-plan / ledger row are not in the directory. " + MissingSource + " No amount is shown.", request.UnitKey, request.CompanyId));
        if (scope.Withheld is { } withheld) return withheld;
        var s = scope.Account!;

        var contracts = s.UnitContracts.Select(c => new CollectionsPactContractRefDto(c.ContractNumber, c.ExternalUnitId, c.UnitNumber, c.ProjectName, c.UnitType)).ToList();
        var mobile = string.IsNullOrWhiteSpace(s.Mobile) ? request.FallbackMobile?.Trim().TrimStart('+') : s.Mobile;
        if (string.IsNullOrWhiteSpace(mobile))
        {
            if (s.Model == EdsmBusinessModel.Rented)
                return UnitPaymentStatementDto.Withheld("UnitNotFound", "A rented company's statement needs the tenant's mobile and none is known for this unit. No amount is shown.", request.UnitKey, s.CompanyId, s.TenantId);
            mobile = MobileNotUsed;
        }
        CollectionsCompanyPaymentSummaryDto read;
        try { read = await account.ReadAccountAsync(s.CompanyId, s.TenantId, mobile, contracts, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("The payment statement could not be read ({ExceptionType}).", ex.GetType().Name);
            return UnitPaymentStatementDto.Withheld("SourceUnavailable", "The PACT payment source could not be read.", request.UnitKey, s.CompanyId, s.TenantId);
        }
        return Compose(request.UnitKey, s, read, edsm.SourceName, edsmOptions.Currency, clock.UtcNow);
    }

    /// <summary>The exact missing source, named once so every refusal says the same thing.</summary>
    public const string MissingSource = "What is missing: a PACT query that lists EVERY unit / contract of a (CompanyId, TenantId) account, fully paid ones included, without needing the customer's mobile (for example a tenant-to-units procedure next to p4tenantStat / p32tenantStat, or an EDSM route taking tenantID + companyID). PACT's v1/contracts/{mobile} needs a mobile, and the receivables procedures omit units without a payment plan.";

    /// <summary>
    /// Pure decision from PACT's unit/account directory (unit-tested): the identity (CompanyId, TenantId, UnitId) of the exact unit code - fully paid units included - then whether the account is PROVEN to hold
    /// ONLY this unit. EDSM returns one statement per (company, tenant) with no unit parameter, so paid siblings feed Paid and Total just like open ones. Scope is proven only by COMPLETE evidence: the directory
    /// when its source is accepted as complete, or the customer's contract list when PACT answered for every phone. Anything else withholds the statement - an account total is never shown as one unit's amounts.
    /// </summary>
    public static Scope ResolveFromDirectory(PactAccountDirectory directory, PactContractDiscoveryResult? found, UnitStatementRequest request, DateOnly today)
    {
        UnitPaymentStatementDto Fail(string status, string detail, int? company = null, string? tenant = null, IReadOnlyList<string>? others = null) =>
            UnitPaymentStatementDto.Withheld(status, detail, request.UnitKey, company ?? request.CompanyId, tenant, others);

        var ids = directory.Matches.Where(i => !i.IsCancelled && CollectionsUnitKey.Normalize(i.UnitCode) == request.UnitKey && CollectionsUnitKey.IsListable(i.UnitCode)).ToList();
        if (request.PactTenantId is { Length: > 0 } expected)
        {
            ids = ids.Where(i => string.Equals(i.TenantId, expected, StringComparison.Ordinal)).ToList();
            if (ids.Count == 0)
                return new(null, Fail("IdentityMismatch", $"PACT's data for unit {request.UnitKey} belongs to a different tenant than the one the receivables snapshot holds for it, so no amount is shown."));
        }
        if (ids.Count == 0) return new(null, Fail("UnitNotFound", $"PACT's unit/account directory holds no unit {request.UnitKey}, so its account cannot be identified. No amount is shown."));
        var accounts = ids.Select(i => (i.CompanyId, i.TenantId)).Distinct().ToList();
        if (accounts.Count > 1)
            return new(null, Fail("Ambiguous", $"{accounts.Count} PACT accounts hold unit {request.UnitKey} ({string.Join(", ", accounts.Select(a => $"company {a.CompanyId} / tenant {a.TenantId}"))}); none is chosen. No amount is shown."));
        var (company, tenant) = accounts[0];
        var unitIds = ids.Select(i => i.UnitId).Distinct().ToList();
        if (unitIds.Count > 1)
            return new(null, Fail("Ambiguous", $"Unit {request.UnitKey} appears with {unitIds.Count} PACT unit ids under one account; none is chosen. No amount is shown.", company, tenant));
        if (!long.TryParse(tenant, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantNumber) || tenantNumber <= 0)
            return new(null, Fail("UnitNotFound", "PACT's tenant id for this unit is not a number, so its account cannot be asked.", company, tenant));
        if (EdsmCompanies.Find(company) is not { } edsmCompany)
            return new(null, Fail("CompanyNotSupported", $"The PACT payment source has no statement for company {company}. No amount is shown.", company, tenant));

        // Every OTHER unit of the account the sources know - open, fully paid or cancelled - feeds EDSM's per-account Paid / Due / Outstanding / Total.
        var others = directory.AccountUnits.Where(u => u.CompanyId == company && string.Equals(u.TenantId, tenant, StringComparison.Ordinal) && u.UnitId != unitIds[0])
            .Select(u => (CollectionsUnitKey.Normalize(u.UnitCode) ?? u.UnitCode) + (u.IsCancelled ? " (cancelled)" : u.OpenRows == 0 ? " (fully paid)" : "")).ToList();
        var contractList = found?.Customers.Where(c => string.Equals(c.Customer.PactCustomerId, tenant, StringComparison.Ordinal)).SelectMany(c => c.Customer.Contracts)
            .Where(c => c.CompanyId == company || c.CompanyId is null).DistinctBy(c => (c.CompanyId, c.ContractNumber, c.ExternalUnitId, c.UnitCode)).ToList() ?? [];
        others.AddRange(contractList.Where(c => CollectionsUnitKey.Normalize(c.UnitCode) != request.UnitKey).Select(c => CollectionsUnitKey.Normalize(c.UnitCode) ?? $"(contract {c.ContractNumber ?? c.ExternalUnitId} without a unit code)"));
        others = others.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (others.Count > 0)
            return new(null, Fail("NotExclusive",
                $"Source limitation: PACT's payment statement (EDSM payment-summary / payment-transactions) is kept per customer account (company {company}, tenant {tenant}) and has no unit parameter, so open AND fully paid units all feed its Paid / Total. This account also covers {string.Join(", ", others)}; its amounts cannot be separated per unit. No amount is shown for {request.UnitKey}.", company, tenant, others));

        // No other unit is known. That proves exclusivity only if the evidence is COMPLETE.
        var contractProof = found is { Complete: true } && contractList.Count > 0;
        if (!directory.IsComplete && !contractProof)
            return new(null, Fail("ScopeUnproven",
                $"The account (company {company}, tenant {tenant}) was identified for unit {request.UnitKey}, but nothing proves it holds no other unit: the unit/account directory is not accepted as complete ({directory.Source}) and PACT's contract list was not available for this customer. EDSM would return the whole account, so no amount is shown. {MissingSource}", company, tenant));

        var unitContract = new PactContractDto(unitIds[0].ToString(CultureInfo.InvariantCulture), null, CollectionsTowerOrder.UnitRest(request.UnitKey), null, null, company, null, request.UnitKey);
        return new(new ProvenAccount(company, tenant, "", [unitContract], contractProof ? "ContractList" : "UnitDirectory") { UnitId = unitIds[0], Model = edsmCompany.Model }, null);
    }

    /// <summary>The account the statement is read for: proven to belong to this unit alone.</summary>
    public sealed record ProvenAccount(int CompanyId, string TenantId, string Mobile, IReadOnlyList<PactContractDto> UnitContracts, string Proof = "ContractList")
    {
        public int? UnitId { get; init; }
        public EdsmBusinessModel? Model { get; init; }
    }

    public sealed record Scope(ProvenAccount? Account, UnitPaymentStatementDto? Withheld);

    /// <summary>Pure decision (unit-tested without any service): which (company, tenant) account holds the unit, and whether it holds ONLY that unit.</summary>
    public static Scope Resolve(PactContractDiscoveryResult found, UnitStatementRequest request, DateOnly today)
    {
        UnitPaymentStatementDto Fail(string status, string detail, int? company = null, string? tenant = null, IReadOnlyList<string>? others = null) =>
            UnitPaymentStatementDto.Withheld(status, detail, request.UnitKey, company ?? request.CompanyId, tenant, others);

        // PACT marks a cancelled unit with an asterisk anywhere in its code: it is not a unit to show, and the mark is never stripped to reach an active one.
        if (!CollectionsUnitKey.IsListable(request.UnitKey))
            return new(null, Fail("UnitNotResolved", "A unit code containing * is cancelled in PACT: no payment statement is shown for it."));
        static string? KeyOf(PactContractDto c) => CollectionsUnitKey.Normalize(c.UnitCode);
        var rows = found.Customers
            .SelectMany(x => x.Customer.Contracts.Select(c => (x.Phone, Customer: x.Customer, Contract: c)))
            .ToList();
        var unitRows = rows.Where(r => KeyOf(r.Contract) == request.UnitKey && (request.CompanyId is null || r.Contract.CompanyId == request.CompanyId)).ToList();
        if (unitRows.Count == 0)
            return new(null, found.Complete
                ? Fail("UnitNotFound", $"PACT's contract list for this customer's phone number(s) holds no contract for unit {request.UnitKey}, so no account can be tied to it. No amount is shown.")
                : Fail("Unverified", "PACT could not answer for every phone number of the customer, so the account of this unit cannot be confirmed. No amount is shown."));
        var active = unitRows.Where(r => r.Contract.ContractEndDate is null || r.Contract.ContractEndDate >= today).ToList();
        if (active.Count == 0)
            return new(null, Fail("ContractEnded", $"Every PACT contract for unit {request.UnitKey} under this customer has ended: its account belongs to a former tenant, so no amount is shown."));
        if (request.PactTenantId is { Length: > 0 } expected)
        {
            active = active.Where(r => string.Equals(r.Customer.PactCustomerId, expected, StringComparison.Ordinal)).ToList();
            if (active.Count == 0)
                return new(null, Fail("IdentityMismatch", $"PACT's contract for unit {request.UnitKey} belongs to a different tenant than the one the receivables snapshot holds for it, so no amount is shown."));
        }
        var accounts = active.Where(r => r.Contract.CompanyId is not null).Select(r => (Company: r.Contract.CompanyId!.Value, Tenant: r.Customer.PactCustomerId)).Distinct().ToList();
        if (accounts.Count == 0)
            return new(null, Fail("UnitNotFound", "PACT returned the contract of this unit without a company, so its account cannot be identified. No amount is shown."));
        if (accounts.Count > 1)
            return new(null, Fail("Ambiguous", $"{accounts.Count} PACT accounts claim unit {request.UnitKey} ({string.Join(", ", accounts.Select(a => $"company {a.Company} / tenant {a.Tenant}"))}); none is chosen. No amount is shown."));
        var (company, tenant) = accounts[0];
        if (!long.TryParse(tenant, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantNumber) || tenantNumber <= 0)
            return new(null, Fail("UnitNotFound", "PACT's tenant id for this unit is not a number, so its account cannot be asked.", company, tenant));
        if (EdsmCompanies.Find(company) is null)
            return new(null, Fail("CompanyNotSupported", $"The PACT payment source has no statement for company {company}. No amount is shown.", company, tenant));

        // EXCLUSIVITY: every contract of this tenant in this company (found by ANY of the phones) must be this unit.
        var accountRows = rows.Where(r => string.Equals(r.Customer.PactCustomerId, tenant, StringComparison.Ordinal) && (r.Contract.CompanyId == company || r.Contract.CompanyId is null))
            .Select(r => r.Contract).DistinctBy(c => (c.CompanyId, c.ContractNumber, c.ExternalUnitId, c.UnitCode)).ToList();
        var others = accountRows.Where(c => KeyOf(c) != request.UnitKey).Select(c => KeyOf(c) ?? $"(contract {c.ContractNumber ?? c.ExternalUnitId} without a unit code)").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (others.Count > 0)
            return new(null, Fail("NotExclusive",
                $"PACT's payment statement is kept per customer account, and this account also covers {string.Join(", ", others)}. Showing it for unit {request.UnitKey} would include another unit's amounts, so no amount is shown.", company, tenant, others));
        if (!found.Complete)
            return new(null, Fail("Unverified", "PACT could not answer for every phone number of the customer, so it cannot be proven that this account holds no other unit. No amount is shown.", company, tenant));
        var phone = active.First(r => string.Equals(r.Customer.PactCustomerId, tenant, StringComparison.Ordinal)).Phone;
        return new(new ProvenAccount(company, tenant, CustomerPhoneNumber.WithoutPlus(phone), active.Where(r => string.Equals(r.Customer.PactCustomerId, tenant, StringComparison.Ordinal)).Select(r => r.Contract).ToList()), null);
    }

    /// <summary>Pure composition of the statement from EDSM's account read (unit-tested): figures as stated, rows as returned, nothing added, netted or defaulted.</summary>
    public static UnitPaymentStatementDto Compose(
        string unitKey, ProvenAccount account, CollectionsCompanyPaymentSummaryDto read, string source, string currency, DateTime retrievedAtUtc)
    {
        if (read.Status != "Available")
            return UnitPaymentStatementDto.Withheld("SourceUnavailable", $"The PACT payment source did not answer for this account ({read.Status}){(string.IsNullOrWhiteSpace(read.StatusDetail) ? "" : ": " + read.StatusDetail)}. No amount is shown.",
                unitKey, account.CompanyId, account.TenantId);
        var owned = read.BusinessModel == nameof(EdsmBusinessModel.Owned);
        StatementAmountDto? Figure(string key)
        {
            var f = read.Fields.FirstOrDefault(x => x.Key == key);
            if (f is null) return null;
            if (key == "lateFines")
            {
                if (f.Status == nameof(EdsmAmountStatus.Empty)) return owned ? new("ZeroOrLess", 0m, f.Raw) : new("NotComputed", null, f.Raw);
            }
            return new(f.Status, f.Status == nameof(EdsmAmountStatus.Provided) ? f.Value : null, f.Raw);
        }
        var due = Figure("dueAmount"); var outstanding = Figure("outstandingAmount"); var paid = Figure("paidAmount"); var fines = Figure("lateFines"); var total = Figure("totalAmount");
        var order = new[] { "Paid", "Due", "Outstanding" };
        var rows = read.Transactions.Where(l => l.Status == "Available")
            .SelectMany(l => l.Items.Select(i => new StatementRowDto(i.Date, i.DateRaw, l.TransactionType, i.Amount, i.FormattedRaw, i.ChequeNumber, i.PaymentType)))
            .OrderBy(r => r.Date is null).ThenBy(r => r.Date).ThenBy(r => Array.IndexOf(order, r.Status)).ToList();
        var notes = read.Notes.ToList();
        if (account.Proof == "UnitDirectory")
            notes.Add("This is PACT's account statement (company " + account.CompanyId + ", tenant " + account.TenantId + "). The account holds no other unit in PACT's unit/account directory (open and fully paid units); the directory source was accepted as complete by configuration (Collections:ReceivablesSnapshot:AccountDirectoryComplete) and has not been compared with EDSM live by this release.");
        var missing = read.Transactions.Where(l => l.Status != "Available").Select(l => l.TransactionType).ToList();
        if (read.Transactions.Count == 0) notes.Add("Payment rows were not requested from the source.");
        foreach (var m in missing) notes.Add($"{m} payments could not be read from the source, so the payment list is incomplete.");
        var anyFigure = new[] { due, outstanding, paid, total }.Any(a => a is { Status: "Provided" });
        var status = anyFigure ? "Available" : "AmountsUnreadable";
        string? detail = anyFigure ? null : "The source answered but no figure could be read (the EDSM number format may not be configured: CollectionsSource:EdsmNumberCulture). No amount is shown.";
        return new UnitPaymentStatementDto(status, detail, unitKey, account.CompanyId, read.CompanyName, read.BusinessModel, account.TenantId, [], due, outstanding, paid, fines, total,
            owned ? "Outstanding" : "Post-dated cheques", rows, read.Transactions.Count > 0 && missing.Count == 0, notes, currency, retrievedAtUtc, source);
    }
}
