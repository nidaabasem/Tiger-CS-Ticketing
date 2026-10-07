using System.Globalization;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The next-payment answer for a PACT-identified customer's mapped EDSM companies
/// (docs/Collections/Next-Payment.md). It reads EDSM's <c>v1/due-installments</c> — the only
/// EDSM route with instalment-level dated rows — <b>only</b> for a company whose semantics the
/// EDSM owners have confirmed in <see cref="CollectionsNextPaymentOptions.Companies"/>
/// (docs/Collections/EDSM-Instalment-Semantics.md). Anything unconfirmed is an explicit
/// <c>Unavailable</c> with reasons, and EDSM is not called for it.
///
/// <para>
/// <b>Search.</b> Forward from the business date in <c>WindowDays</c>-day windows (consecutive windows
/// overlap by one day, and rows are de-duplicated, so an inclusive/exclusive range boundary cannot hide a
/// row) through <c>SearchHorizonDays</c>, stopping at the first window that holds an unpaid instalment —
/// the earliest unpaid date is then the earliest overall, because windows are visited in date order. It
/// never silently stops at a fixed 31 days: the horizon is configured, and reported as
/// <c>searchedThrough</c>.
/// </para>
///
/// <para>
/// <b>Never infers.</b> A row is a candidate only if its raw <c>status</c> is in the attested unpaid set, its
/// <c>chequeDueDate</c> is present, and its <c>amount</c> is present and positive. Overdue (due today or
/// earlier) is excluded by <see cref="NextPaymentCalculator"/>.
/// </para>
/// </summary>
public sealed class CollectionsNextPaymentService(
    CollectionsEdsmOptions edsmOptions, CollectionsClock clock, IEdsmCollectionsGateway edsm)
{
    /// <param name="mappedCompanyIds">The companies PACT confirmed for the tenant — nothing is guessed or added.</param>
    /// <param name="tenantId">The PACT tenant.</param>
    /// <param name="token">The read budget's token; a deadline surfaces as <paramref name="deadlineExpired"/>.</param>
    /// <param name="deadlineExpired">True when the overall deadline (not the caller) cancelled <paramref name="token"/>.</param>
    public async Task<CollectionsEdsmNextPaymentDto> ResolveAsync(
        IReadOnlyList<int> mappedCompanyIds, long tenantId, CancellationToken token, Func<bool> deadlineExpired)
    {
        var options = edsmOptions.NextPayment;
        var today = clock.BusinessDate;
        var currency = edsmOptions.Currency;

        if (!options.Enabled)
        {
            return Whole(CollectionsNextPaymentReasons.FeatureDisabled,
                "Next payment is switched off (CollectionsSource:NextPayment:Enabled).");
        }

        var companies = new List<CollectionsNextPaymentCompanyDto>();
        foreach (var companyId in mappedCompanyIds.Distinct().OrderBy(c => c))
        {
            companies.Add(await ForCompanyAsync(companyId, tenantId, today, currency, options, token, deadlineExpired));
        }

        return Aggregate(companies, today, options);
    }

    /// <summary>A customer with no verified CRM → PACT mapping: no tenant or company is guessed.</summary>
    public static CollectionsEdsmNextPaymentDto ForUnmapped(string? mappingDetail) =>
        Whole(CollectionsNextPaymentReasons.MappingNotAvailable,
            mappingDetail ?? "No verified mapping to a PACT tenant exists for this customer, so no company or tenant is asked about.");

    private static CollectionsEdsmNextPaymentDto Whole(string reason, string detail) =>
        new(CollectionsNextPaymentStatus.Unavailable, [reason], detail, IsComplete: false, null, null, null, []);

    private async Task<CollectionsNextPaymentCompanyDto> ForCompanyAsync(
        int companyId, long tenantId, DateOnly today, string currency, CollectionsNextPaymentOptions options,
        CancellationToken token, Func<bool> deadlineExpired)
    {
        static CollectionsNextPaymentCompanyDto Unavailable(
            int id, string reason, IReadOnlyList<string>? missing = null, DateOnly? through = null) =>
            new(id, CollectionsNextPaymentStatus.Unavailable, [reason], missing ?? [], through, null);

        if (EdsmCompanies.Find(companyId) is not { SupportsDueInstallments: true })
        {
            return Unavailable(companyId, CollectionsNextPaymentReasons.CompanyNotSupported);
        }

        if (!options.Companies.TryGetValue(companyId, out var attestation))
        {
            return Unavailable(companyId, CollectionsNextPaymentReasons.SemanticsNotConfirmed,
                new NextPaymentSemanticsAttestation().MissingItems());
        }

        if (attestation.MissingItems() is { Count: > 0 } missing)
        {
            return Unavailable(companyId, CollectionsNextPaymentReasons.SemanticsNotConfirmed, missing);
        }

        if (tenantId > int.MaxValue)
        {
            // EDSM's due-installments tenantID is 32-bit: rows for this tenant cannot be matched safely.
            return Unavailable(companyId, CollectionsNextPaymentReasons.TenantIdNotMatchable);
        }

        var unpaid = attestation.UnpaidStatusValues
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var horizonEnd = today.AddDays(options.EffectiveHorizonDays);
        var window = options.EffectiveWindowDays;
        var from = today;

        while (from < horizonEnd)
        {
            var to = from.AddDays(window) > horizonEnd ? horizonEnd : from.AddDays(window);

            EdsmResult<IReadOnlyList<EdsmDueInstallment>> result;
            try
            {
                result = await edsm.GetDueInstallmentsAsync(companyId, from, to, token);
            }
            catch (OperationCanceledException) when (deadlineExpired())
            {
                return Unavailable(companyId, CollectionsNextPaymentReasons.DeadlineExceeded, through: from);
            }

            if (result is not { Outcome: EdsmOutcome.Success, Value: { } rows })
            {
                return Unavailable(companyId, CollectionsNextPaymentReasons.SourceUnavailable, through: from);
            }

            var facts = rows
                .Where(r => r.CompanyId == companyId && r.TenantId == tenantId
                    && r.ChequeDueDate is not null && r.Amount is > 0m
                    && r.Status is { } status && unpaid.Contains(status.Trim()))
                .GroupBy(r => (r.UnitId, Voucher: r.VoucherNumber?.Trim(), r.ChequeNumber, r.ChequeDueDate, r.Amount, Status: r.Status!.Trim().ToUpperInvariant()))
                .Select(g => g.First())
                .Select(r => new UnpaidInstalmentFact(
                    companyId, tenantId, r.UnitId, $"{r.VoucherNumber?.Trim()}|{r.ChequeNumber}", r.ChequeDueDate!.Value, r.Amount!.Value))
                .ToList();

            if (NextPaymentCalculator.SelectNext(facts, today) is { } next)
            {
                return new CollectionsNextPaymentCompanyDto(
                    companyId, CollectionsNextPaymentStatus.Available, [], [], to,
                    new CollectionsNextPaymentItemDto(
                        companyId, tenantId, next.DueDate, next.Amount,
                        next.Amount.ToString("0.00", CultureInfo.InvariantCulture), currency,
                        next.Units.Select(u => new CollectionsNextPaymentUnitDto(u.UnitId, u.Amount, u.InstalmentCount)).ToList()));
            }

            if (to >= horizonEnd)
            {
                break;
            }

            // The next window starts on this window's last day (one-day overlap).
            from = to;
        }

        return new CollectionsNextPaymentCompanyDto(
            companyId, CollectionsNextPaymentStatus.NoneWithinHorizon, [], [], horizonEnd, null);
    }

    private static CollectionsEdsmNextPaymentDto Aggregate(
        List<CollectionsNextPaymentCompanyDto> companies, DateOnly today, CollectionsNextPaymentOptions options)
    {
        if (companies.Count == 0)
        {
            return Whole(CollectionsNextPaymentReasons.MappingNotAvailable,
                "PACT confirmed no EDSM company for this tenant, so there is nothing to ask.");
        }

        var available = companies.Where(c => c.Status == CollectionsNextPaymentStatus.Available).ToList();
        var complete = companies.All(c => c.Status != CollectionsNextPaymentStatus.Unavailable);
        var through = companies.Where(c => c.SearchedThrough is not null).Select(c => c.SearchedThrough!.Value).DefaultIfEmpty().Min();
        var reasons = companies.SelectMany(c => c.Reasons).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

        if (available.Count > 0)
        {
            // Deterministic: earliest date, then lowest company id.
            var earliest = available.Select(c => c.Next!).OrderBy(n => n.DueDate).ThenBy(n => n.CompanyId).First();
            return new CollectionsEdsmNextPaymentDto(
                CollectionsNextPaymentStatus.Available,
                complete ? [] : reasons.Append(CollectionsNextPaymentReasons.SearchIncomplete).Distinct().ToList(),
                complete ? null : "At least one mapped company could not be determined, so a later-dated company's earlier payment may be missing; this is the earliest among those that were.",
                complete, today, through == default ? null : through, earliest, companies);
        }

        if (complete)
        {
            var horizon = today.AddDays(options.EffectiveHorizonDays);
            return new CollectionsEdsmNextPaymentDto(
                CollectionsNextPaymentStatus.NoneWithinHorizon, [],
                $"No unpaid instalment falls after today through {horizon:yyyy-MM-dd}. This does not say none exist later.",
                IsComplete: true, today, horizon, null, companies);
        }

        return new CollectionsEdsmNextPaymentDto(
            CollectionsNextPaymentStatus.Unavailable, reasons,
            "A next payment cannot be stated for the mapped companies; see each company's reasons.",
            IsComplete: false, today, null, null, companies);
    }
}
