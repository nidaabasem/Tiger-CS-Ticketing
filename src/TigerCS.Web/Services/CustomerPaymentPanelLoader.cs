using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Services;

/// <summary>
/// Builds the Payment tab from TigerCS.Api's <c>/api/collections</c> routes.
/// Every state comes from what the Api answered: 403 is "no permission",
/// 503 is "switched off" or "finance unavailable" (told apart by the error
/// code), 404 is "no linked account", and a figure that did not arrive is
/// never filled in as zero.
/// </summary>
public sealed class CustomerPaymentPanelLoader(CollectionsApiClient collections)
{
    private static readonly string[] ReminderTypes = ["OverdueMonthly", "CurrentMonth", "MonthEndFollowUp"];

    public async Task<CustomerPaymentPanel> LoadAsync(
        string customerKey, long? crmCustomerId, string? accountId, string? notice, bool noticeIsError, CancellationToken cancellationToken)
    {
        if (crmCustomerId is not { } customer)
        {
            return new CustomerPaymentPanel { CustomerKey = customerKey, State = PaymentPanelState.NotCrmCustomer };
        }

        var outstanding = await collections.GetOutstandingAsync(customer, cancellationToken);
        var state = outstanding.Outcome switch
        {
            ApiOutcome.Success => PaymentPanelState.Loaded,
            ApiOutcome.Forbidden => PaymentPanelState.Forbidden,
            ApiOutcome.NotFound => PaymentPanelState.NoAccounts,
            ApiOutcome.ServiceUnavailable when outstanding.ProblemType?.EndsWith("/" + CustomerPaymentPanel.DisabledCode, StringComparison.Ordinal) == true
                => PaymentPanelState.Disabled,
            ApiOutcome.ServiceUnavailable => PaymentPanelState.Unavailable,
            _ => PaymentPanelState.Error
        };

        var basePanel = new CustomerPaymentPanel
        {
            CustomerKey = customerKey, CrmCustomerId = customer, State = state, Notice = notice, NoticeIsError = noticeIsError
        };

        if (state is PaymentPanelState.Forbidden or PaymentPanelState.Disabled or PaymentPanelState.Error)
        {
            return basePanel;
        }

        if (state != PaymentPanelState.Loaded)
        {
            // Reminder history is TigerCS's own record — still shown when the
            // finance source is unavailable or holds no account.
            var history = await collections.GetRemindersAsync(customer, null, cancellationToken);
            return Copy(basePanel, state, reminders: history.Value);
        }

        var accounts = outstanding.Value!.Accounts;
        var selected = accounts.Count == 1
            ? accounts[0]
            : accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (selected is null)
        {
            return Copy(basePanel, PaymentPanelState.SelectAccount, outstanding.Value);
        }

        var instalmentsTask = collections.GetInstalmentsAsync(customer, selected.AccountId, cancellationToken);
        var historyTask = collections.GetPaymentHistoryAsync(customer, selected.AccountId, cancellationToken);
        var remindersTask = collections.GetRemindersAsync(customer, selected.AccountId, cancellationToken);

        var candidates = new List<CollectionsReminderCandidateDto>();
        var canSend = true;
        foreach (var type in ReminderTypes)
        {
            var result = await collections.GetCandidatesAsync(type, customer, selected.AccountId, cancellationToken);
            if (result.Outcome == ApiOutcome.Forbidden)
            {
                canSend = false;
                break;
            }

            candidates.AddRange(result.Value?.Items ?? []);
        }

        return new CustomerPaymentPanel
        {
            CustomerKey = customerKey,
            CrmCustomerId = customer,
            State = PaymentPanelState.Loaded,
            Outstanding = outstanding.Value,
            SelectedAccount = selected,
            Instalments = (await instalmentsTask).Value,
            History = (await historyTask).Value,
            Reminders = (await remindersTask).Value,
            Candidates = candidates,
            CanSend = canSend,
            Notice = notice,
            NoticeIsError = noticeIsError
        };
    }

    private static CustomerPaymentPanel Copy(
        CustomerPaymentPanel panel, PaymentPanelState state, CollectionsOutstandingResponseDto? outstanding = null,
        CollectionsReminderHistoryResponseDto? reminders = null) =>
        new()
        {
            CustomerKey = panel.CustomerKey,
            CrmCustomerId = panel.CrmCustomerId,
            State = state,
            Outstanding = outstanding,
            Reminders = reminders,
            Notice = panel.Notice,
            NoticeIsError = panel.NoticeIsError
        };
}
