using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The read side of Collections: balances, the instalment schedule, payment
/// history and reminder history for one CRM customer, optionally narrowed to
/// one account or one CRM unit.
///
/// <para>
/// Every figure is read from <see cref="ICollectionsFinancialSource"/> on
/// every call — nothing financial is cached or stored by TigerCS — and a
/// source that cannot answer is reported as
/// <see cref="CollectionsOutcome.SourceUnavailable"/>, never as a zero.
/// </para>
/// </summary>
public sealed class CollectionsAccountQueryAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsFinancialSource source,
    ICollectionsReminderRepository reminderRepository)
{
    public const int MaxPageSize = 100;

    public async Task<CollectionsResult<CollectionsOutstandingResponseDto>> GetOutstandingAsync(
        CollectionsCaller caller, string crmCustomerId, string? accountId, string? crmUnitId, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync<CollectionsOutstandingResponseDto>(caller, crmCustomerId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        var scoped = await LoadScopedAsync(crmCustomerId, accountId, crmUnitId, cancellationToken);
        if (scoped.Failure is { } failure)
        {
            return CollectionsResult<CollectionsOutstandingResponseDto>.Fail(failure.Outcome, failure.Detail);
        }

        var today = clock.Today;
        var rules = clock.Rules;
        var windows = ReminderPolicy.OpenWindows(today, rules);

        var accounts = scoped.Accounts!
            .Select(account => ToAccountDto(account, today, rules, windows, clock.IsStale(account.AsOfUtc)))
            .ToList();

        var asOf = scoped.Accounts!.Count == 0 ? clock.UtcNow : scoped.Accounts!.Min(a => a.AsOfUtc);
        var enabledChannels = Enum.GetValues<ReminderChannel>().Where(options.Channels.IsEnabled).Select(c => c.ToString()).ToList();

        return CollectionsResult<CollectionsOutstandingResponseDto>.Ok(new CollectionsOutstandingResponseDto(
            crmCustomerId.Trim(),
            source.SourceName,
            asOf,
            accounts.Any(a => a.IsStale),
            today,
            accounts,
            new CollectionsViewerDto(gate.Permissions!.CanSendReminders, enabledChannels),
            // No verified receipt or statement-of-account document API exists
            // in any connected system, so neither download is offered.
            new CollectionsDocumentsDto(
                ReceiptDownloadAvailable: false,
                StatementDownloadAvailable: false,
                Reason: "No verified receipt or statement-of-account document API is connected.")));
    }

    public async Task<CollectionsResult<CollectionsPaymentsResponseDto>> GetPaymentsAsync(
        CollectionsCaller caller, string crmCustomerId, string? accountId, string? crmUnitId, bool includeUnposted,
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync<CollectionsPaymentsResponseDto>(caller, crmCustomerId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!ValidPaging(page, pageSize, out var pagingError))
        {
            return CollectionsResult<CollectionsPaymentsResponseDto>.Fail(CollectionsOutcome.ValidationFailed, pagingError);
        }

        var scoped = await LoadScopedAsync(crmCustomerId, accountId, crmUnitId, cancellationToken);
        if (scoped.Failure is { } failure)
        {
            return CollectionsResult<CollectionsPaymentsResponseDto>.Fail(failure.Outcome, failure.Detail);
        }

        var payments = scoped.Accounts!
            .SelectMany(a => a.Payments.Select(p => (Account: a, Payment: p)))
            .Where(x => includeUnposted || x.Payment.Status == FinancialPaymentStatus.Posted)
            .OrderByDescending(x => x.Payment.PostedOn ?? x.Payment.ReceivedOn)
            .ThenByDescending(x => x.Payment.ReceivedOn)
            .ThenBy(x => x.Payment.PaymentId, StringComparer.Ordinal)
            .ToList();

        var items = payments
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new CollectionsPaymentDto(
                x.Payment.PaymentId, x.Account.AccountId, x.Account.CrmUnitId, x.Account.UnitNumber,
                x.Payment.ReceivedOn, x.Payment.PostedOn, x.Payment.Amount, x.Account.Currency,
                x.Payment.Method, x.Payment.Reference, x.Payment.Status.ToString(),
                CountsTowardBalance: x.Payment.Status == FinancialPaymentStatus.Posted,
                // Only a source-verified receipt is ever offered, and no
                // document API is connected — see GetOutstandingAsync.
                ReceiptAvailable: false))
            .ToList();

        var asOf = scoped.Accounts!.Count == 0 ? clock.UtcNow : scoped.Accounts!.Min(a => a.AsOfUtc);

        return CollectionsResult<CollectionsPaymentsResponseDto>.Ok(new CollectionsPaymentsResponseDto(
            crmCustomerId.Trim(), source.SourceName, asOf, clock.IsStale(asOf), items, payments.Count, page, pageSize));
    }

    /// <summary>
    /// Reminder history comes from TigerCS's own records (what was sent and
    /// what happened), so it does not need the financial source and stays
    /// readable while the source is down.
    /// </summary>
    public async Task<CollectionsResult<CollectionsReminderListResultDto>> GetRemindersAsync(
        CollectionsCaller caller, string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync<CollectionsReminderListResultDto>(caller, crmCustomerId, cancellationToken);
        if (gate.Failure is not null)
        {
            return gate.Failure;
        }

        if (!ValidPaging(page, pageSize, out var pagingError))
        {
            return CollectionsResult<CollectionsReminderListResultDto>.Fail(CollectionsOutcome.ValidationFailed, pagingError);
        }

        var (items, total) = await reminderRepository.ListForCustomerAsync(
            crmCustomerId.Trim(), string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim(), page, pageSize, cancellationToken);

        return CollectionsResult<CollectionsReminderListResultDto>.Ok(
            new CollectionsReminderListResultDto(items.Select(CollectionsMapper.ToDto).ToList(), total, page, pageSize));
    }

    internal static CollectionsAccountDto ToAccountDto(
        FinancialAccountSnapshot account, DateOnly today, ReminderRuleSettings rules, IReadOnlyList<ReminderWindow> windows, bool isStale)
    {
        var balance = AccountBalanceCalculator.Calculate(account, today);
        var manual = ReminderPolicy.Evaluate(account, ReminderType.Manual, today, rules);
        var openWindows = windows
            .Where(w => ReminderPolicy.Evaluate(account, w.Type, today, rules).IsEligible)
            .Select(w => w.Type.ToString())
            .ToList();

        var valid = balance.Consistency != BalanceConsistency.InvalidSourceData;

        return new CollectionsAccountDto(
            account.AccountId,
            account.CrmUnitId,
            account.UnitNumber,
            account.ProjectName,
            account.Currency,
            account.AsOfUtc,
            isStale,
            balance.Consistency.ToString(),
            balance.Problems,
            valid
                ? new CollectionsBalanceDto(
                    balance.RemainingUnpaidPrincipal!.Value,
                    balance.OverduePrincipal!.Value,
                    balance.PrincipalDueToday!.Value,
                    balance.FuturePrincipal!.Value,
                    balance.PayableFinesAndFees!.Value,
                    balance.AmountDueNow!.Value,
                    balance.CurrentMonthRemaining!.Value,
                    balance.NextPayment is { } next ? new CollectionsNextPaymentDto(next.InstalmentId, next.DueDate, next.Amount) : null)
                : null,
            new CollectionsReminderEligibilityDto(manual.IsEligible, manual.Amount, manual.Reason, openWindows),
            valid
                ? account.Instalments
                    .OrderBy(i => i.DueDate).ThenBy(i => i.Sequence)
                    .Select(i => new CollectionsInstalmentDto(
                        i.InstalmentId, i.Sequence, i.DueDate, i.PrincipalAmount,
                        i.PrincipalAmount - i.PrincipalOutstanding, i.PrincipalOutstanding, InstalmentStatus(i, today)))
                    .ToList()
                : [],
            valid
                ? account.Charges.Select(c => new CollectionsChargeDto(c.ChargeId, c.Kind, c.Description, c.Amount, c.Outstanding, c.DueDate, c.IsPayable)).ToList()
                : []);
    }

    private static string InstalmentStatus(FinancialInstalment i, DateOnly today) =>
        i.PrincipalOutstanding == 0m ? "Paid"
        : i.DueDate < today ? "Overdue"
        : i.DueDate == today ? "DueToday"
        : i.PrincipalOutstanding < i.PrincipalAmount ? "PartiallyPaid"
        : "Upcoming";

    private async Task<(CollectionsPermissions? Permissions, CollectionsResult<T>? Failure)> GateAsync<T>(
        CollectionsCaller caller, string crmCustomerId, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return (null, CollectionsResult<T>.Fail(CollectionsOutcome.Disabled));
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReadFinancials)
        {
            return (permissions, CollectionsResult<T>.Fail(CollectionsOutcome.Forbidden, "Reading customer financials requires the Collections financial-read permission."));
        }

        if (string.IsNullOrWhiteSpace(crmCustomerId) || crmCustomerId.Trim().Length > CollectionsReminder.IdentifierMaxLength)
        {
            return (permissions, CollectionsResult<T>.Fail(CollectionsOutcome.ValidationFailed, "crmCustomerId is required."));
        }

        return (permissions, null);
    }

    private async Task<(IReadOnlyList<FinancialAccountSnapshot>? Accounts, (CollectionsOutcome Outcome, string? Detail)? Failure)> LoadScopedAsync(
        string crmCustomerId, string? accountId, string? crmUnitId, CancellationToken cancellationToken)
    {
        IReadOnlyList<FinancialAccountSnapshot>? accounts;
        try
        {
            accounts = await source.GetCustomerAccountsAsync(crmCustomerId.Trim(), cancellationToken);
        }
        catch (CollectionsFinancialSourceUnavailableException ex)
        {
            return (null, (CollectionsOutcome.SourceUnavailable, ex.Message));
        }

        if (accounts is null)
        {
            return (null, (CollectionsOutcome.CustomerNotFound, $"The financial source has no customer '{crmCustomerId.Trim()}'."));
        }

        // Scoping is enforced, not merely filtered: an account or unit that
        // is not this customer's is a 404, never an empty list that could be
        // read as "nothing owed".
        var scoped = accounts.Where(a => string.Equals(a.CrmCustomerId, crmCustomerId.Trim(), StringComparison.Ordinal)).ToList();

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            scoped = scoped.Where(a => string.Equals(a.AccountId, accountId.Trim(), StringComparison.Ordinal)).ToList();
            if (scoped.Count == 0)
            {
                return (null, (CollectionsOutcome.AccountNotFound, $"Account '{accountId.Trim()}' does not belong to customer '{crmCustomerId.Trim()}'."));
            }
        }

        if (!string.IsNullOrWhiteSpace(crmUnitId))
        {
            scoped = scoped.Where(a => string.Equals(a.CrmUnitId, crmUnitId.Trim(), StringComparison.Ordinal)).ToList();
            if (scoped.Count == 0)
            {
                return (null, (CollectionsOutcome.AccountNotFound, $"Unit '{crmUnitId.Trim()}' has no account for customer '{crmCustomerId.Trim()}'."));
            }
        }

        return (scoped, null);
    }

    internal static bool ValidPaging(int page, int pageSize, out string? error)
    {
        error = page < 1 ? "page must be 1 or more."
            : pageSize is < 1 or > MaxPageSize ? $"pageSize must be between 1 and {MaxPageSize}."
            : null;
        return error is null;
    }
}
