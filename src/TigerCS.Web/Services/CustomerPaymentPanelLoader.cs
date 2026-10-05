using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Services;

/// <summary>
/// Builds the Payment tab from TigerCS.Api's Collections routes. Every state
/// comes from what the Api answered: a 403 is "no permission", a 503 is
/// "switched off" or "source unavailable" (told apart by the problem type),
/// and a figure that did not arrive is never filled in as zero.
/// </summary>
public sealed class CustomerPaymentPanelLoader(CollectionsApiClient collections)
{
    public const int PageSize = 20;

    public async Task<CustomerPaymentPanel> LoadAsync(
        string customerKey, string? crmCustomerId, string? accountId, string? notice, bool noticeIsError, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(crmCustomerId))
        {
            return new CustomerPaymentPanel { CustomerKey = customerKey, State = PaymentPanelState.NotCrmCustomer };
        }

        var outstanding = await collections.GetOutstandingAsync(crmCustomerId, cancellationToken);

        var state = outstanding.Outcome switch
        {
            ApiOutcome.Success => PaymentPanelState.Loaded,
            ApiOutcome.Forbidden => PaymentPanelState.Forbidden,
            ApiOutcome.NotFound => PaymentPanelState.NoAccounts,
            ApiOutcome.ServiceUnavailable when outstanding.ProblemType == CustomerPaymentPanel.CollectionsDisabledProblem => PaymentPanelState.Disabled,
            ApiOutcome.ServiceUnavailable => PaymentPanelState.SourceUnavailable,
            _ => PaymentPanelState.Error
        };

        if (state is PaymentPanelState.Forbidden or PaymentPanelState.Disabled or PaymentPanelState.Error)
        {
            return new CustomerPaymentPanel
            {
                CustomerKey = customerKey, CrmCustomerId = crmCustomerId, State = state, StateDetail = outstanding.Detail,
                Notice = notice, NoticeIsError = noticeIsError
            };
        }

        var accounts = outstanding.Value?.Accounts ?? [];
        var selected = accounts.FirstOrDefault(a => a.AccountId == accountId) ?? accounts.FirstOrDefault();
        var scopeAccountId = selected?.AccountId;

        // Reminder history is TigerCS's own record, so it is still shown when
        // the financial source is down; payments come from the source.
        var remindersTask = collections.GetRemindersAsync(crmCustomerId, scopeAccountId, 1, PageSize, cancellationToken);
        var paymentsTask = state == PaymentPanelState.Loaded
            ? collections.GetPaymentsAsync(crmCustomerId, scopeAccountId, 1, PageSize, cancellationToken)
            : null;

        var reminders = await remindersTask;
        var payments = paymentsTask is null ? null : await paymentsTask;

        return new CustomerPaymentPanel
        {
            CustomerKey = customerKey,
            CrmCustomerId = crmCustomerId,
            State = state,
            StateDetail = outstanding.Detail,
            Outstanding = outstanding.Value,
            SelectedAccount = selected,
            Payments = payments?.Value,
            PaymentsOutcome = payments?.Outcome ?? ApiOutcome.ServiceUnavailable,
            Reminders = reminders.Value,
            RemindersOutcome = reminders.Outcome,
            Notice = notice,
            NoticeIsError = noticeIsError
        };
    }
}
