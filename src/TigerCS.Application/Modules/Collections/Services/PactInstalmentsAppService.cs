using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Instalment-level Receivables list. Reads ONLY the local snapshot (never PACT); every filter - tower, due-date window (a whole month or any range),
/// payment status and the minimum outstanding amount - is applied in SQL together with the totals and the paging, so rows, counts, totals and pages agree.
/// Payment status (Unpaid / Partially paid / Fully paid) is separate from the date-based Due / Overdue classification.
/// </summary>
public sealed class PactInstalmentsAppService(
    CollectionsOptions collectionsOptions,
    PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    IPactReceivablesSource source,
    ILogger<PactInstalmentsAppService> logger)
{
    public async Task<CollectionsResult<PactInstalmentsPageDto>> ListAsync(
        CollectionsCaller caller, int? towerId = null, DateOnly? dateFrom = null, DateOnly? dateTo = null, string? paymentStatus = null,
        decimal? minAmount = null, string? search = null, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default,
        string? view = null, string? dueMonth = null, string? unitStatusFilter = null)
    {
        var total = Stopwatch.StartNew();
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.Disabled);
        if (!CollectionsPaymentFilters.TryParse(paymentStatus, out var filter)
            || page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue || search?.Length > 200
            || towerId is <= 0 || minAmount < 0 || minAmount > sourceOptions.MaxMinOutstandingAmount)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose a tower, a payment status (outstanding, unpaid, partial, paid or all), a minimum amount of 0 or more, page >= 1 and pageSize 1-100; search is limited to 200 characters.");
        // A selected due month (yyyy-MM, from a month card) narrows the list and its totals to that month; the month overview itself always covers the whole filtered window.
        DateOnly? monthFrom = null, monthTo = null;
        if (!string.IsNullOrWhiteSpace(dueMonth))
        {
            if (!DateOnly.TryParseExact(dueMonth + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var first)
                || first.Year is < 2000 or > 2100)
                return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, "dueMonth must be a month such as 2026-03.");
            (monthFrom, monthTo) = CollectionsDateRanges.Month(first.Year, first.Month);
        }
        // "units": one row per unit, grouped in SQL before paging (all matching instalments of a unit stay together); "instalments" (the default of this API): one row per instalment.
        var byUnit = string.Equals(view, "units", StringComparison.OrdinalIgnoreCase);
        if (!byUnit && !string.IsNullOrWhiteSpace(view) && !string.Equals(view, "instalments", StringComparison.OrdinalIgnoreCase))
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, "view must be units or instalments.");
        // By unit only: keep the units that have an Overdue / Due / Upcoming instalment (classified by TODAY, Dubai).
        string? unitStatus = null;
        if (!string.IsNullOrWhiteSpace(unitStatusFilter) && !string.Equals(unitStatusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            unitStatus = unitStatusFilter.Trim().ToLowerInvariant();
            if (unitStatus is not ("overdue" or "due" or "upcoming") || !byUnit)
                return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, "status must be all, overdue, due or upcoming, and needs view=units.");
        }
        if (byUnit && source is not IPactInstalmentUnitSource)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.Disabled, "The unit view needs the local receivables snapshot (Collections:ReceivablesSnapshot:UseLocalSnapshot).");
        if (source is not IPactInstalmentSource instalments)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.Disabled, "The instalment list needs the local receivables snapshot (Collections:ReceivablesSnapshot:UseLocalSnapshot).");

        var today = clock.BusinessDate;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = CollectionsDateRanges.Month(today.Year, today.Month).To;
        // Default window: 1 January of the current year through the end of the current month. Both ends are editable; a month selector sets them to a calendar month.
        var from = dateFrom ?? DateOnly.FromDateTime(sourceOptions.StartDate);
        var to = dateTo ?? monthEnd;
        if (!CollectionsDateRanges.IsSupported(from, to))
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, "From date must not be after To date, and both must be within 2000-2100.");
        var min = decimal.Round(minAmount ?? sourceOptions.DefaultMinOutstandingAmount, 4);
        var minApplies = CollectionsPaymentFilters.MinimumApplies(filter);

        var term = search?.Trim();
        string? digits = null;
        if (!string.IsNullOrEmpty(term))
        {
            var d = new string(term.Where(char.IsDigit).ToArray());
            if (d.Length >= 3 && !term.Any(char.IsLetter)) digits = d;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));
        PactInstalmentsPage result;
        IReadOnlyList<PactInstalmentUnitDto>? units = null;
        IReadOnlyList<PactInstalmentMonthDto>? months = null;
        try
        {
            // By unit classifies every instalment against TODAY (Overdue: before today, Due: today, Upcoming: after today); the other view keeps the month rule.
            var overview = new PactInstalmentsRequest(from, to, byUnit ? today : monthStart, min, CollectionsPaymentFilters.ToWire(filter),
                string.IsNullOrEmpty(term) ? null : term, digits, 1, pageSize, null, towerId, unitStatus, byUnit);
            // The list (and its totals) use the window narrowed to the selected month; Overdue is still "due before the current month" for every row.
            var request = overview;
            if (monthFrom is { } mf && monthTo is { } mt)
            {
                var narrowedFrom = mf > from ? mf : from;
                var narrowedTo = mt < to ? mt : to;
                if (narrowedFrom > narrowedTo)
                    return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, "The selected month is outside the selected dates.");
                request = overview with { From = narrowedFrom, To = narrowedTo, Page = page };
            }
            else request = overview with { Page = page };
            if (!byUnit && source is IPactInstalmentMonthSource monthSource)
                months = await monthSource.ReadInstalmentMonthsAsync(overview, budget.Token);
            if (byUnit)
            {
                var unitPage = await ((IPactInstalmentUnitSource)source).ReadInstalmentUnitsAsync(request, budget.Token);
                units = unitPage.Units;
                result = new PactInstalmentsPage(unitPage.Totals, [], unitPage.Unavailable, unitPage.Snapshot, unitPage.ReadAtUtc, unitPage.SqlMs);
            }
            else result = await instalments.ReadInstalmentsAsync(request, budget.Token);
        }
        catch (PactReceivablesScopeException ex)
        {
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local instalment read exceeded the {BudgetSeconds}s request budget.", sourceOptions.RequestBudgetSeconds);
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Local instalment source failed ({ExceptionType}).", ex.GetType().Name);
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Receivables could not be read. Please retry or contact support.");
        }

        var status = result.Snapshot;
        var views = new PaymentViewAvailabilityDto(true, status.BreakdownAvailable, status.BreakdownAvailable, status.PaidRetained, status.PaidRetained, status.UnclassifiedRows);
        if (result.Unavailable)
            return CollectionsResult<PactInstalmentsPageDto>.Fail(CollectionsOutcome.InvalidRequest, filter is CollectionsPaymentFilter.FullyPaid or CollectionsPaymentFilter.All
                ? "Fully paid instalments are not in the loaded data yet, so Fully paid and All cannot be shown. Reload the data to retain them."
                : "The source does not return the original and paid amounts, so Unpaid and Partially paid cannot be told apart. Use Outstanding.");

        var notes = new List<string>(PactReceivableCustomersAppService.WindowNotes(from, to, monthStart, monthEnd))
        {
            "Payment status and Due/Overdue are independent: an instalment can be partially paid and overdue at the same time."
        };
        if (!minApplies) notes.Add("The minimum outstanding amount is not applied to Fully paid and All, so paid rows are never hidden.");
        else notes.Add($"Only instalments whose remaining unpaid amount is at least {min:0.##} {sourceOptions.Currency} are listed and counted.");
        if (filter == CollectionsPaymentFilter.Outstanding && status.UnclassifiedRows > 0)
            notes.Add($"{status.UnclassifiedRows:N0} instalment(s) show \"Needs verification\": the source does not say whether they are unpaid or partially paid.");

        return CollectionsResult<PactInstalmentsPageDto>.Ok(new PactInstalmentsPageDto(today, monthStart, monthEnd, from, to, towerId,
            CollectionsPaymentFilters.ToWire(filter), min, minApplies, sourceOptions.Currency, result.Totals, page, pageSize, result.Rows, status, views, notes,
            result.ReadAtUtc, new ServerTimingsDto(result.SqlMs, total.Elapsed.TotalMilliseconds - result.SqlMs, total.Elapsed.TotalMilliseconds),
            byUnit ? "units" : "instalments", units, months, string.IsNullOrWhiteSpace(dueMonth) ? null : dueMonth));
    }
}
