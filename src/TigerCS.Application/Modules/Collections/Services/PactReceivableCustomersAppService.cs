using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.Services;

public sealed class PactReceivableCustomersAppService(
    CollectionsOptions collectionsOptions,
    PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    IPactReceivablesSource source,
    ILogger<PactReceivableCustomersAppService> logger)
{
    public async Task<CollectionsResult<PactReceivableCustomersDto>> ListAsync(
        CollectionsCaller caller, int? companyId = null, string? status = null,
        string? search = null, int page = 1, int pageSize = 25,
        int? year = null, int? month = null, CancellationToken cancellationToken = default)
    {
        // Authorize before reading configuration or any external customer data.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Disabled);
        var view = (status ?? "all").Trim().ToLowerInvariant();
        if (companyId is not (null or 4 or 32) || view is not ("all" or "due" or "overdue")
            || page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue
            || search?.Length > 200
            || (year is null) != (month is null) || year is < 2000 or > 2100 || month is < 1 or > 12)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose company 4 or 32, status All/Due/Overdue, page >= 1 and pageSize 1-100; year and month go together (2000-2100, 1-12); search is limited to 200 characters.");

        var today = clock.BusinessDate;
        // Reporting month (default: current Dubai month), matching EDSM's monthly receivables report:
        // OverDue = DueDate before the first day; Due = DueDate within the month, including later days.
        var reportYear = year ?? today.Year;
        var reportMonth = month ?? today.Month;
        var periodStart = new DateOnly(reportYear, reportMonth, 1);
        var periodEnd = new DateOnly(reportYear, reportMonth, DateTime.DaysInMonth(reportYear, reportMonth));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(sourceOptions.CommandTimeoutSeconds, 1, 300)));
        PactReceivablesSnapshot snapshot;
        try
        {
            snapshot = await source.ReadAsync(periodEnd, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("PACT receivables source failed ({ExceptionType}).", ex.GetType().Name);
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "PACT receivables could not be read. Please retry or contact support.");
        }

        // The only business filter: a positive Due or OverDue amount for the reporting month. Rows after the
        // month end are EDSM's "Outstanding" (not Due/OverDue) and are not part of this list.
        var live = snapshot.Items.Where(r => r.Amount > 0 && DateOnly.FromDateTime(r.DueDate) <= periodEnd).ToList();
        if (live.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The source returned a receivable without a valid company/customer identity.");

        List<PactReceivableCustomerDto> customers;
        try
        {
            // One row per apartment: company + customer + unit.
            customers = live.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Unit: r.UnitCode.Trim()))
                .Select(g => Map(g.ToList(), today, periodStart, sourceOptions.SourceStatusMap)).ToList();
        }
        catch (OverflowException)
        {
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT report contains amounts outside the supported range.");
        }
        var term = search?.Trim();
        if (companyId is { } company) customers = customers.Where(c => c.CompanyId == company).ToList();
        if (!string.IsNullOrWhiteSpace(term))
        {
            bool Match(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
            var digits = term.Where(char.IsDigit).ToArray();
            var phoneSearch = digits.Length >= 3 && !term.Any(char.IsLetter);
            customers = customers.Where(c => Match(c.FullName) || Match(c.TenantId) || Match(c.Email)
                || Match(c.Mobile) || c.Instalments.Any(i => Match(i.UnitCode))
                || (phoneSearch && new string(c.Mobile.Where(char.IsDigit).ToArray()).Contains(new string(digits), StringComparison.Ordinal)))
                .ToList();
        }
        customers = customers.Where(c => view == "all" || (view == "due" ? c.HasDue : c.HasOverdue))
            .OrderByDescending(c => c.HasOverdue).ThenBy(c => c.EarliestDueDate)
            .ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CompanyId).ThenBy(c => c.TenantId, StringComparer.Ordinal)
            .ThenBy(c => c.UnitCode, StringComparer.Ordinal).ThenBy(c => c.UnitId)
            .ToList();
        return CollectionsResult<PactReceivableCustomersDto>.Ok(new PactReceivableCustomersDto(
            today, reportYear, reportMonth, periodStart, periodEnd, snapshot.ReadAtUtc, snapshot.LegacyExclusionsApplied, [4, 32], sourceOptions.Currency, "Configured",
            customers.Count, customers.Count(c => c.HasDue), customers.Count(c => c.HasOverdue),
            page, pageSize, customers.Skip((page - 1) * pageSize).Take(pageSize).ToList()));
    }

    /// <summary>
    /// Verified meaning of the report's <c>Status</c>: the procedures emit <c>Paid</c> when the remaining Amount is zero
    /// and <c>Installment</c> otherwise. <c>Installment</c> does not distinguish unpaid from partially paid, so it is never
    /// mapped (not even by configuration) and stays Unknown. Paid with a positive remainder is contradictory: Unknown.
    /// Payment status is never derived from the due date or the amount.
    /// </summary>
    public static string PaymentStatus(string? source, decimal remaining, IReadOnlyDictionary<string, string> map)
    {
        var value = source?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("Installment", StringComparison.OrdinalIgnoreCase)) return "Unknown";
        if (value.Equals("Paid", StringComparison.OrdinalIgnoreCase)) return remaining <= 0 ? "Paid" : "Unknown";
        if (!map.TryGetValue(value, out var mapped)) return "Unknown";
        return mapped.Trim().ToLowerInvariant() switch
        {
            "unpaid" => "Unpaid",
            "partiallypaid" => "PartiallyPaid",
            "paid" when remaining <= 0 => "Paid",
            _ => "Unknown"
        };
    }

    private static PactReceivableCustomerDto Map(List<PactReceivableInstalment> rows, DateOnly today, DateOnly periodStart,
        IReadOnlyDictionary<string, string> statusMap)
    {
        var first = rows[0];
        var items = rows.OrderBy(r => r.DueDate).ThenBy(r => r.UnitCode, StringComparer.Ordinal)
            .Select(r => new PactReceivableInstalmentDto(r.UnitId, r.UnitCode, r.ProjectCode, r.VoucherNumber,
                r.ChequeNumber, DateOnly.FromDateTime(r.DueDate), r.Amount,
                DateOnly.FromDateTime(r.DueDate) < periodStart ? "OverDue" : "Due",
                PaymentStatus(r.SourceStatus, r.Amount, statusMap),
                DateOnly.FromDateTime(r.DueDate) is var d && d < today ? "Overdue" : d == today ? "DueToday" : "Upcoming",
                r.SourceStatus ?? "")).ToList();
        // These SPs join invoice rows to payment terms by tenant only. Multiple returned
        // rows on one date can be duplicated allocations OR legitimate instalments.
        // Preserve the rows; never silently DISTINCT them or sum an ambiguous date.
        var due = items.Where(i => i.ReceivablesType == "Due").ToList();
        var overdue = items.Where(i => i.ReceivablesType == "OverDue").ToList();
        static decimal? SumIfUnambiguous(List<PactReceivableInstalmentDto> bucket) =>
            bucket.GroupBy(i => i.DueDate).Any(g => g.Count() > 1) ? null : bucket.Sum(i => i.RemainingAmount);
        var dueAmount = SumIfUnambiguous(due);
        var overdueAmount = SumIfUnambiguous(overdue);
        // Due (within the month) and OverDue (before the month) are disjoint by date, so no source row
        // can sit in both buckets; the total is therefore a plain sum, and only when both are known.
        decimal? totalAmount = dueAmount is { } d && overdueAmount is { } o ? d + o : null;
        var earliest = items.Min(i => i.DueDate);
        return new PactReceivableCustomerDto(first.CompanyId,
            first.CompanyId == 4 ? "Tiger Group Dubai" : "Tiger Group Sharjah", first.TenantId.Trim(),
            first.FullName, first.Mobile, first.Email, first.UnitId, first.UnitCode.Trim(), first.ProjectCode,
            due.Count > 0, overdue.Count > 0,
            dueAmount, overdueAmount, totalAmount, dueAmount is null || overdueAmount is null ? "NeedsReview" : "Provided",
            earliest, Math.Max(0, today.DayNumber - earliest.DayNumber), items);
    }
}
