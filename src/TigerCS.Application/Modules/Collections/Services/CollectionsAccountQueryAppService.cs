using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The read side: outstanding amounts, instalments, posted payment history and
/// reminder history for one CRM customer, scoped to an account or unit.
///
/// <para>
/// Every figure is read from <see cref="ICollectionsFinancialSource"/> on
/// every call — nothing financial is cached or stored by TigerCS — and a
/// source that cannot answer is <see cref="CollectionsOutcome.FinanceUnavailable"/>,
/// never a zero. An account or unit that is not the customer's is
/// <see cref="CollectionsOutcome.AccountNotFound"/>, never an empty list.
/// </para>
/// </summary>
public sealed class CollectionsAccountQueryAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsFinancialSource source,
    ICollectionsReminderRepository reminderRepository)
{
    public async Task<CollectionsResult<CollectionsOutstandingResponseDto>> GetOutstandingAsync(
        CollectionsCaller caller, long crmCustomerId, string? accountId, long? unitId, string? cursor, int? pageSize,
        CancellationToken cancellationToken = default)
    {
        if (await GateAsync<CollectionsOutstandingResponseDto>(caller, crmCustomerId, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!CollectionsCursor.TryRead(cursor, pageSize, out var offset, out var size, out var pagingError))
        {
            return CollectionsResult<CollectionsOutstandingResponseDto>.Fail(CollectionsOutcome.InvalidRequest, pagingError);
        }

        var scoped = await LoadScopedAsync(crmCustomerId, accountId, unitId, cancellationToken);
        if (scoped.Failure is { } failure)
        {
            return CollectionsResult<CollectionsOutstandingResponseDto>.Fail(failure.Outcome, failure.Detail);
        }

        var businessDate = clock.BusinessDate;
        var all = scoped.Accounts!.OrderBy(a => a.AccountId, StringComparer.Ordinal).ToList();
        var page = all.Skip(offset).Take(size).ToList();
        var accounts = page.Select(a => ToAccountDto(a, businessDate)).ToList();

        var asOf = page.Count == 0 ? clock.UtcNow : page.Min(a => a.AsOfUtc);
        return CollectionsResult<CollectionsOutstandingResponseDto>.Ok(new CollectionsOutstandingResponseDto(
            crmCustomerId, businessDate, asOf,
            accounts.Any(a => a.DataStatus == "Stale") ? "Stale" : "Current",
            source.SourceName, accounts,
            CollectionsCursor.Next(offset, size, offset + size < all.Count)));
    }

    public async Task<CollectionsResult<CollectionsInstalmentsResponseDto>> GetInstalmentsAsync(
        CollectionsCaller caller, long crmCustomerId, string? accountId, long? unitId, string? cursor, int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var account = await SingleAccountAsync<CollectionsInstalmentsResponseDto>(caller, crmCustomerId, accountId, unitId, cursor, pageSize, cancellationToken);
        if (account.Failure is not null)
        {
            return account.Failure;
        }

        var (snapshot, offset, size) = account.Value!.Value;
        var businessDate = clock.BusinessDate;
        var balance = AccountBalanceCalculator.Calculate(snapshot, businessDate);
        if (!balance.HasFigures)
        {
            // Invalid source data: nothing is shown as a figure, here as on the balance.
            return CollectionsResult<CollectionsInstalmentsResponseDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The financial source returned invalid instalment data: " + string.Join(" ", balance.Problems));
        }

        var rows = snapshot.Instalments.OrderBy(i => i.DueDate).ThenBy(i => i.InstalmentId, StringComparer.Ordinal).ToList();
        var items = rows.Skip(offset).Take(size).Select(i => new CollectionsInstalmentDto(
            i.InstalmentId, i.DueDate, CollectionsMoney.Two(i.ScheduledAmount), CollectionsMoney.Two(i.ScheduledAmount - i.RemainingAmount),
            CollectionsMoney.Two(i.RemainingAmount),
            InstalmentStatus(i, businessDate), i.RemainingAmount > 0m && i.DueDate < businessDate)).ToList();

        return CollectionsResult<CollectionsInstalmentsResponseDto>.Ok(new CollectionsInstalmentsResponseDto(
            crmCustomerId, snapshot.AccountId, snapshot.Currency, snapshot.AsOfUtc, DataStatus(snapshot, balance), "instalments",
            items, CollectionsCursor.Next(offset, size, offset + size < rows.Count)));
    }

    public async Task<CollectionsResult<CollectionsPaymentHistoryResponseDto>> GetPaymentHistoryAsync(
        CollectionsCaller caller, long crmCustomerId, string? accountId, long? unitId, DateOnly? fromDate, DateOnly? toDate,
        string? cursor, int? pageSize, CancellationToken cancellationToken = default)
    {
        if (fromDate is { } from && toDate is { } to && from > to)
        {
            return CollectionsResult<CollectionsPaymentHistoryResponseDto>.Fail(CollectionsOutcome.InvalidRequest, "fromDate must not be after toDate.");
        }

        var account = await SingleAccountAsync<CollectionsPaymentHistoryResponseDto>(caller, crmCustomerId, accountId, unitId, cursor, pageSize, cancellationToken);
        if (account.Failure is not null)
        {
            return account.Failure;
        }

        var (snapshot, offset, size) = account.Value!.Value;
        var balance = AccountBalanceCalculator.Calculate(snapshot, clock.BusinessDate);

        // Posted payments only: unverified proof, reversals and rejections are
        // never shown as payments and never reduce anything.
        var rows = snapshot.Payments
            .Where(p => p.Status == FinancialPaymentStatus.Posted)
            .Where(p => (fromDate is null || p.PaymentDate >= fromDate) && (toDate is null || p.PaymentDate <= toDate))
            .OrderByDescending(p => p.PaymentDate).ThenBy(p => p.PaymentId, StringComparer.Ordinal)
            .ToList();

        var items = rows.Skip(offset).Take(size).Select(p => new CollectionsPaymentDto(
            p.PaymentId, p.PaymentDate, CollectionsMoney.Two(p.Amount), p.Method, p.Status.ToString(), p.ReceiptNumber, p.ReceiptAvailable,
            p.Allocations.Select(a => new CollectionsPaymentAllocationDto(a.InstalmentId, CollectionsMoney.Two(a.Amount),
                a.AccountId is { } other && other != snapshot.AccountId ? other : null)).ToList())).ToList();

        return CollectionsResult<CollectionsPaymentHistoryResponseDto>.Ok(new CollectionsPaymentHistoryResponseDto(
            crmCustomerId, snapshot.AccountId, snapshot.Currency, snapshot.AsOfUtc, DataStatus(snapshot, balance), "history",
            items, CollectionsCursor.Next(offset, size, offset + size < rows.Count)));
    }

    /// <summary>
    /// Reminder history is TigerCS's own record of what was quoted and what
    /// happened, so it does not need the financial source and stays readable
    /// while the source is down.
    /// </summary>
    public async Task<CollectionsResult<CollectionsReminderHistoryResponseDto>> GetRemindersAsync(
        CollectionsCaller caller, long crmCustomerId, string? accountId, string? cursor, int? pageSize, CancellationToken cancellationToken = default)
    {
        if (await GateAsync<CollectionsReminderHistoryResponseDto>(caller, crmCustomerId, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!CollectionsCursor.TryRead(cursor, pageSize, out var offset, out var size, out var pagingError))
        {
            return CollectionsResult<CollectionsReminderHistoryResponseDto>.Fail(CollectionsOutcome.InvalidRequest, pagingError);
        }

        var account = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        var (items, hasMore) = await reminderRepository.ListForCustomerAsync(crmCustomerId, account, offset, size, cancellationToken);

        return CollectionsResult<CollectionsReminderHistoryResponseDto>.Ok(new CollectionsReminderHistoryResponseDto(
            crmCustomerId, account, items.Select(CollectionsMapper.ToHistoryDto).ToList(), CollectionsCursor.Next(offset, size, hasMore)));
    }

    internal CollectionsAccountDto ToAccountDto(FinancialAccountSnapshot account, DateOnly businessDate)
    {
        var b = AccountBalanceCalculator.Calculate(account, businessDate);
        return new CollectionsAccountDto(
            account.AccountId, account.UnitId, account.TowerName, account.UnitNumber, account.Currency, account.AsOfUtc,
            DataStatus(account, b),
            CollectionsMoney.Two(b.RemainingPrincipalAmount), CollectionsMoney.Two(b.OverduePrincipalAmount),
            CollectionsMoney.Two(b.DueTodayPrincipalAmount), CollectionsMoney.Two(b.FuturePrincipalAmount),
            CollectionsMoney.Two(b.PayablePenaltyAmount), CollectionsMoney.Two(b.PayableFeeAmount), CollectionsMoney.Two(b.AppliedCreditAmount),
            CollectionsMoney.Two(b.AmountDueNow), CollectionsMoney.Two(b.CurrentMonthRemainingAmount),
            b.OldestUnpaidDueDate,
            b.NextPayment is { } next ? new CollectionsNextPaymentDto(next.InstalmentId, next.DueDate, CollectionsMoney.Two(next.RemainingAmount)) : null,
            b.Problems);
    }

    internal string DataStatus(FinancialAccountSnapshot account, AccountBalance balance) => balance.Consistency switch
    {
        BalanceConsistency.InvalidSourceData => "Invalid",
        BalanceConsistency.Mismatch => "Inconsistent",
        _ => clock.IsStale(account.AsOfUtc) ? "Stale" : "Current"
    };

    private static string InstalmentStatus(FinancialInstalment i, DateOnly businessDate) =>
        i.RemainingAmount == 0m ? "Paid"
        : i.RemainingAmount < i.ScheduledAmount ? "PartiallyPaid"
        : i.DueDate < businessDate ? "Overdue"
        : i.DueDate == businessDate ? "DueToday"
        : "Upcoming";

    private async Task<(CollectionsResult<T>? Failure, (FinancialAccountSnapshot Account, int Offset, int Size)? Value)> SingleAccountAsync<T>(
        CollectionsCaller caller, long crmCustomerId, string? accountId, long? unitId, string? cursor, int? pageSize, CancellationToken cancellationToken)
    {
        if (await GateAsync<T>(caller, crmCustomerId, cancellationToken) is { } refused)
        {
            return (refused, null);
        }

        if (!CollectionsCursor.TryRead(cursor, pageSize, out var offset, out var size, out var pagingError))
        {
            return (CollectionsResult<T>.Fail(CollectionsOutcome.InvalidRequest, pagingError), null);
        }

        var scoped = await LoadScopedAsync(crmCustomerId, accountId, unitId, cancellationToken);
        if (scoped.Failure is { } failure)
        {
            return (CollectionsResult<T>.Fail(failure.Outcome, failure.Detail), null);
        }

        // One account per answer: amounts in different currencies, or across
        // contracts, are never merged into one list.
        if (scoped.Accounts!.Count > 1)
        {
            return (CollectionsResult<T>.Fail(CollectionsOutcome.InvalidRequest,
                "accountId is required: this customer has more than one account in scope."), null);
        }

        return (null, (scoped.Accounts[0], offset, size));
    }

    private async Task<CollectionsResult<T>?> GateAsync<T>(CollectionsCaller caller, long crmCustomerId, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return CollectionsResult<T>.Fail(CollectionsOutcome.Disabled);
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReadFinancials)
        {
            return CollectionsResult<T>.Fail(CollectionsOutcome.Forbidden, "Viewing customer payments requires the Collections financial-read permission.");
        }

        return crmCustomerId <= 0
            ? CollectionsResult<T>.Fail(CollectionsOutcome.InvalidRequest, "crmCustomerId must be a positive CRM customer id.")
            : null;
    }

    private async Task<(IReadOnlyList<FinancialAccountSnapshot>? Accounts, (CollectionsOutcome Outcome, string? Detail)? Failure)> LoadScopedAsync(
        long crmCustomerId, string? accountId, long? unitId, CancellationToken cancellationToken)
    {
        IReadOnlyList<FinancialAccountSnapshot>? accounts;
        try
        {
            accounts = await source.GetCustomerAccountsAsync(crmCustomerId, cancellationToken);
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            return (null, (CollectionsOutcome.FinanceUnavailable, ex.Message));
        }

        // Same answer for "no such customer" and "not this customer's": the
        // response never reveals whether another customer's account exists.
        const string notFound = "No accessible account matches this customer.";
        var scoped = (accounts ?? []).Where(a => a.CrmCustomerId == crmCustomerId).ToList();
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            scoped = scoped.Where(a => string.Equals(a.AccountId, accountId.Trim(), StringComparison.Ordinal)).ToList();
        }

        if (unitId is { } unit)
        {
            scoped = scoped.Where(a => a.UnitId == unit).ToList();
        }

        return scoped.Count == 0 ? (null, (CollectionsOutcome.AccountNotFound, notFound)) : (scoped, null);
    }
}
