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
        CancellationToken cancellationToken = default)
    {
        // Authorize before reading configuration or any external customer data.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Forbidden);
        if (!collectionsOptions.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.Disabled);
        var view = (status ?? "all").Trim().ToLowerInvariant();
        if (companyId is not (null or 4 or 32) || view is not ("all" or "due" or "overdue")
            || page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue
            || search?.Length > 200)
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose company 4 or 32, status All/Due/Overdue, page >= 1 and pageSize 1-100; search is limited to 200 characters.");

        var today = clock.BusinessDate;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(sourceOptions.CommandTimeoutSeconds, 1, 300)));
        PactReceivablesSnapshot snapshot;
        try
        {
            snapshot = await source.ReadAsync(today, budget.Token);
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

        var live = snapshot.Items.Where(r => r.Amount > 0 && DateOnly.FromDateTime(r.DueDate) <= today).ToList();
        if (live.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            return CollectionsResult<PactReceivableCustomersDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The source returned a receivable without a valid company/customer identity.");

        List<PactReceivableCustomerDto> customers;
        try
        {
            customers = live.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim()))
                .Select(g => Map(g.ToList(), today)).ToList();
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
            .ToList();
        return CollectionsResult<PactReceivableCustomersDto>.Ok(new PactReceivableCustomersDto(
            today, snapshot.ReadAtUtc, snapshot.LegacyExclusionsApplied, [4, 32], sourceOptions.Currency, "Configured",
            customers.Count, customers.Count(c => c.HasDue), customers.Count(c => c.HasOverdue),
            page, pageSize, customers.Skip((page - 1) * pageSize).Take(pageSize).ToList()));
    }

    private static PactReceivableCustomerDto Map(List<PactReceivableInstalment> rows, DateOnly today)
    {
        var first = rows[0];
        var items = rows.OrderBy(r => r.DueDate).ThenBy(r => r.UnitCode, StringComparer.Ordinal)
            .Select(r => new PactReceivableInstalmentDto(r.UnitId, r.UnitCode, r.ProjectCode, r.VoucherNumber,
                r.ChequeNumber, DateOnly.FromDateTime(r.DueDate), r.Amount,
                DateOnly.FromDateTime(r.DueDate) < today ? "Overdue" : "Due")).ToList();
        // These SPs join invoice rows to payment terms by tenant only. Multiple returned
        // rows on one date can be duplicated allocations OR legitimate instalments.
        // Preserve the rows; never silently DISTINCT them or sum an ambiguous date.
        var due = items.Where(i => i.Status == "Due").ToList();
        var overdue = items.Where(i => i.Status == "Overdue").ToList();
        static decimal? SumIfUnambiguous(List<PactReceivableInstalmentDto> bucket) =>
            bucket.GroupBy(i => i.DueDate).Any(g => g.Count() > 1) ? null : bucket.Sum(i => i.Amount);
        var dueAmount = SumIfUnambiguous(due);
        var overdueAmount = SumIfUnambiguous(overdue);
        var earliest = items.Min(i => i.DueDate);
        return new PactReceivableCustomerDto(first.CompanyId,
            first.CompanyId == 4 ? "Tiger Group Dubai" : "Tiger Group Sharjah", first.TenantId.Trim(),
            first.FullName, first.Mobile, first.Email, due.Count > 0, overdue.Count > 0,
            dueAmount, overdueAmount, dueAmount is null || overdueAmount is null ? "NeedsReview" : "Provided",
            earliest, today.DayNumber - earliest.DayNumber, items);
    }
}
