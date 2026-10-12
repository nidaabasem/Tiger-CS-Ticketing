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
public sealed record UnitStatementRequest(int? CompanyId, string UnitKey, string? PactTenantId, IReadOnlyList<string> Phones);

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
    IEdsmCollectionsGateway edsm, ILogger<CollectionsUnitStatementService> logger)
{
    public async Task<UnitPaymentStatementDto> GetAsync(UnitStatementRequest request, CancellationToken cancellationToken)
    {
        if (edsm.SourceName == "Unavailable")
            return UnitPaymentStatementDto.Withheld("NotConfigured", "The PACT payment source (EDSM) is not configured in this environment, so no payment statement can be read.", request.UnitKey, request.CompanyId);
        PactContractDiscoveryResult found;
        try { found = await discovery.DiscoverAsync(request.Phones, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("PACT contract discovery failed ({ExceptionType}).", ex.GetType().Name);
            return UnitPaymentStatementDto.Withheld("SourceUnavailable", "PACT could not be reached to confirm which account holds this unit.", request.UnitKey, request.CompanyId);
        }

        var scope = Resolve(found, request, clock.BusinessDate);
        if (scope.Withheld is { } withheld) return withheld;
        var s = scope.Account!;

        var contracts = s.UnitContracts.Select(c => new CollectionsPactContractRefDto(c.ContractNumber, c.ExternalUnitId, c.UnitNumber, c.ProjectName, c.UnitType)).ToList();
        CollectionsCompanyPaymentSummaryDto read;
        try { read = await account.ReadAccountAsync(s.CompanyId, s.TenantId, s.Mobile, contracts, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning("The payment statement could not be read ({ExceptionType}).", ex.GetType().Name);
            return UnitPaymentStatementDto.Withheld("SourceUnavailable", "The PACT payment source could not be read.", request.UnitKey, s.CompanyId, s.TenantId);
        }
        return Compose(request.UnitKey, s, read, edsm.SourceName, edsmOptions.Currency, clock.UtcNow);
    }

    /// <summary>The account the statement is read for: proven to belong to this unit alone.</summary>
    public sealed record ProvenAccount(int CompanyId, string TenantId, string Mobile, IReadOnlyList<PactContractDto> UnitContracts);

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
