using System.Globalization;
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
        string customerKey, long? crmCustomerId, string? accountId, string? notice, bool noticeIsError, CancellationToken cancellationToken,
        PaymentPanelOptions? options = null)
    {
        options ??= new PaymentPanelOptions();
        if (crmCustomerId is not { } customer)
        {
            return CustomerPaymentPanel.IsPactCustomer(customerKey)
                ? await LoadEdsmSummaryAsync(customerKey, accountId, notice, noticeIsError, options, cancellationToken)
                : new CustomerPaymentPanel { CustomerKey = customerKey, State = PaymentPanelState.NotCrmCustomer, Links = options.Links!, AllowSending = options.AllowSending };
        }

        // Unit-based first: the customer's CRM units are tied to PACT by company + tower + apartment, so a CRM phone that PACT does not hold changes nothing.
        var unitPanel = await TryUnitPanelAsync(customerKey, customer, accountId, notice, noticeIsError, options, cancellationToken);
        if (unitPanel is { State: PaymentPanelState.Forbidden or PaymentPanelState.Disabled } || HasUnitFigures(unitPanel))
            return unitPanel!;

        var legacy = await LoadAccountsAsync(customerKey, customer, accountId, notice, noticeIsError, options, cancellationToken);
        // The per-account / EDSM route is kept for what it can really show. When it shows nothing, the unit diagnosis (why exactly) replaces its generic "no figures" message.
        var legacyHasFigures = legacy.State is PaymentPanelState.Loaded or PaymentPanelState.SelectAccount || HasEdsmFigures(legacy);
        return unitPanel is not null && !legacyHasFigures ? unitPanel : legacy;
    }

    private async Task<CustomerPaymentPanel?> TryUnitPanelAsync(
        string customerKey, long customer, string? accountId, string? notice, bool noticeIsError, PaymentPanelOptions options, CancellationToken cancellationToken)
    {
        var unitLink = await collections.GetCrmCustomerUnitsAsync(customer, options.CrmLookupPhone ?? options.LookupPhoneNumber, UnitSelection(accountId, options.PreferredCrmUnitId), cancellationToken);
        return UnitPanel(unitLink, customerKey, customer, notice, noticeIsError, options);
    }

    private static bool HasUnitFigures(CustomerPaymentPanel? panel) =>
        panel is { State: PaymentPanelState.UnitSummary } && panel.UnitLink!.Candidates.Any(c => c.FinancialStatus is "Available" or "NoDues");

    /// <summary>Only an explicit unit choice (<c>crm:…</c> / <c>pact:…</c>) is a unit selection; a finance <c>accountId</c> belongs to the per-account view.</summary>
    private static string? UnitSelection(string? accountId, long? preferredCrmUnitId) =>
        accountId is not null && (accountId.StartsWith("crm:", StringComparison.Ordinal) || accountId.StartsWith("pact:", StringComparison.Ordinal))
            ? accountId
            : preferredCrmUnitId is { } unit ? $"crm:{unit.ToString(CultureInfo.InvariantCulture)}" : null;

    private static bool HasEdsmFigures(CustomerPaymentPanel panel) =>
        panel.State == PaymentPanelState.EdsmSummary && panel.PaymentSummary is { MappingStatus: "Mapped" } summary && summary.Companies.Any(c => c.Status == "Available");

    /// <summary>The panel for a unit-linking answer; null when the answer says nothing usable (no units known), so the caller falls back to the per-account view.</summary>
    private static CustomerPaymentPanel? UnitPanel(
        ApiResult<CustomerUnitLinkResultDto> result, string customerKey, long? crmCustomerId, string? notice, bool noticeIsError, PaymentPanelOptions options)
    {
        CustomerPaymentPanel Base(PaymentPanelState state) => new()
        {
            CustomerKey = customerKey, CrmCustomerId = crmCustomerId, State = state, Notice = notice, NoticeIsError = noticeIsError, Links = options.Links!, AllowSending = false
        };
        switch (result.Outcome)
        {
            case ApiOutcome.Forbidden: return Base(PaymentPanelState.Forbidden);
            case ApiOutcome.ServiceUnavailable when result.ProblemType?.EndsWith("/" + CustomerPaymentPanel.DisabledCode, StringComparison.Ordinal) == true:
                return Base(PaymentPanelState.Disabled);
            // Any other 503 says nothing about the customer's units: the per-account view reports the source state itself.
            case ApiOutcome.Success when result.Value is { Candidates.Count: > 0 } link:
                return new CustomerPaymentPanel
                {
                    CustomerKey = customerKey, CrmCustomerId = crmCustomerId, State = PaymentPanelState.UnitSummary, UnitLink = link, Notice = notice, NoticeIsError = noticeIsError,
                    Links = options.Links!, AllowSending = false
                };
            default: return null;
        }
    }

    private async Task<CustomerPaymentPanel> LoadAccountsAsync(
        string customerKey, long customer, string? accountId, string? notice, bool noticeIsError, PaymentPanelOptions options, CancellationToken cancellationToken)
    {
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
            CustomerKey = customerKey, CrmCustomerId = customer, State = state, Notice = notice, NoticeIsError = noticeIsError,
            Links = options.Links!, AllowSending = options.AllowSending
        };

        if (state is PaymentPanelState.Forbidden or PaymentPanelState.Disabled or PaymentPanelState.Error)
        {
            return basePanel;
        }

        if (options.LookupPhoneNumber is not null && state is PaymentPanelState.NoAccounts or PaymentPanelState.Unavailable)
        {
            return await LoadEdsmSummaryAsync(customerKey, accountId, notice, noticeIsError, options, cancellationToken);
        }

        if (state == PaymentPanelState.Unavailable)
        {
            // No per-account source answered. Ask the EDSM service (the same one PACT
            // customers use). If it confirms there is no verified mapping for this customer,
            // say so explicitly instead of implying a temporary outage.
            var summary = await collections.GetPaymentSummaryAsync(customerKey, cancellationToken);
            if (summary is { Outcome: ApiOutcome.Success, Value.MappingStatus: "NotMapped" })
            {
                var reminderHistory = await collections.GetRemindersAsync(customer, null, cancellationToken);
                return new CustomerPaymentPanel
                {
                    CustomerKey = customerKey, CrmCustomerId = customer, State = PaymentPanelState.NotMapped,
                    PaymentSummary = summary.Value, Reminders = reminderHistory.Value, Notice = notice, NoticeIsError = noticeIsError,
                    Links = options.Links!, AllowSending = options.AllowSending
                };
            }
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
        var scopedByUnit = false;
        if (selected is null && accountId is null && !string.IsNullOrWhiteSpace(options.PreferredUnitNumber))
        {
            // No explicit choice: scope to the account holding the unit the
            // caller is working on. Only an exact unit-number match counts;
            // anything looser would quote another unit's balance.
            selected = accounts.FirstOrDefault(a =>
                string.Equals(a.UnitNumber?.Trim(), options.PreferredUnitNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            scopedByUnit = selected is not null;
        }

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
            NoticeIsError = noticeIsError,
            Links = options.Links!,
            AllowSending = options.AllowSending,
            ScopedByUnit = scopedByUnit
        };
    }

    public Task<CustomerPaymentPanel> LoadLookupAsync(
        string phoneNumber, string customerKey, string? companyId, CancellationToken cancellationToken)
    {
        var hidden = new Dictionary<string, string> { ["phoneNumber"] = phoneNumber, ["customerKey"] = customerKey };
        var href = $"/Customers/Payments?phoneNumber={Uri.EscapeDataString(phoneNumber)}&customerKey={Uri.EscapeDataString(customerKey)}";
        var links = new PaymentPanelLinks("/Customers/Payments", hidden, "account",
            account => account is null ? href : $"{href}&account={Uri.EscapeDataString(account)}", AutoSubmit: true);
        var options = new PaymentPanelOptions(links, AllowSending: false, LookupPhoneNumber: phoneNumber);
        // A CRM customer found by this phone: the same unit-based lookup as the Customer Details Payment tab.
        if (customerKey.StartsWith("crm:", StringComparison.OrdinalIgnoreCase) && long.TryParse(customerKey.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var crmCustomerId))
            return LoadCrmLookupAsync(customerKey, crmCustomerId, companyId, options, cancellationToken);
        return LoadEdsmSummaryAsync(customerKey, companyId, null, false, options, cancellationToken);
    }

    /// <summary>Pre-ticket lookup of a CRM customer: the unit-based figures first, then the verified EDSM lookup as before; when neither shows figures the unit diagnosis explains why.</summary>
    private async Task<CustomerPaymentPanel> LoadCrmLookupAsync(string customerKey, long customer, string? account, PaymentPanelOptions options, CancellationToken cancellationToken)
    {
        var unitPanel = await TryUnitPanelAsync(customerKey, customer, account, null, false, options, cancellationToken);
        if (unitPanel is { State: PaymentPanelState.Forbidden or PaymentPanelState.Disabled } || HasUnitFigures(unitPanel)) return unitPanel!;
        var edsm = await LoadEdsmSummaryAsync(customerKey, account, null, false, options, cancellationToken);
        return unitPanel is not null && !HasEdsmFigures(edsm) ? unitPanel : edsm;
    }

    /// <summary>Reads EDSM through the directory, or a freshly verified lookup before any ticket exists.</summary>
    private async Task<CustomerPaymentPanel> LoadEdsmSummaryAsync(
        string customerKey, string? companyId, string? notice, bool noticeIsError, PaymentPanelOptions options, CancellationToken cancellationToken)
    {
        var summary = options.LookupPhoneNumber is { } phoneNumber
            ? await collections.GetLookupPaymentSummaryAsync(phoneNumber, customerKey, cancellationToken)
            : await collections.GetPaymentSummaryAsync(customerKey, cancellationToken);
        var state = summary.Outcome switch
        {
            ApiOutcome.Success when summary.Value?.MappingStatus == "NotMapped" => PaymentPanelState.NotMapped,
            ApiOutcome.Success => PaymentPanelState.EdsmSummary,
            ApiOutcome.Forbidden => PaymentPanelState.Forbidden,
            ApiOutcome.NotFound => PaymentPanelState.NoAccounts,
            ApiOutcome.ServiceUnavailable when summary.ProblemType?.EndsWith("/" + CustomerPaymentPanel.DisabledCode, StringComparison.Ordinal) == true
                => PaymentPanelState.Disabled,
            ApiOutcome.ServiceUnavailable => PaymentPanelState.Unavailable,
            _ => PaymentPanelState.Error
        };

        // The "account" of an EDSM view is a confirmed company (with its
        // tenant): the explicit choice, else the company whose verified
        // contracts include the caller's unit, else the first.
        int? selectedCompanyId = null;
        var scopedByUnit = false;
        if (state == PaymentPanelState.EdsmSummary)
        {
            var companies = summary.Value!.Companies;
            selectedCompanyId = companies.FirstOrDefault(c => c.CompanyId.ToString(CultureInfo.InvariantCulture) == companyId)?.CompanyId;
            if (selectedCompanyId is null && companyId is null && !string.IsNullOrWhiteSpace(options.PreferredExternalUnitId))
            {
                selectedCompanyId = companies
                    .FirstOrDefault(c => c.Contracts.Any(contract => string.Equals(contract.ExternalUnitId, options.PreferredExternalUnitId, StringComparison.Ordinal)))
                    ?.CompanyId;
                scopedByUnit = selectedCompanyId is not null;
            }

            selectedCompanyId ??= companies.FirstOrDefault()?.CompanyId;
        }

        var edsmPanel = new CustomerPaymentPanel
        {
            CustomerKey = customerKey,
            State = state,
            PaymentSummary = state is PaymentPanelState.EdsmSummary or PaymentPanelState.NotMapped ? summary.Value : null,
            SelectedCompanyId = selectedCompanyId,
            ScopedByUnit = scopedByUnit,
            Notice = notice,
            NoticeIsError = noticeIsError,
            Links = options.Links!,
            AllowSending = options.AllowSending
        };

        // A PACT customer found by phone whose EDSM summary shows no figures (Leasing company 7 is not an EDSM company; a contract may be unmapped): the unit-based view of THIS tenant's
        // contracts says exactly what is and is not known, instead of a generic "no figures".
        if (options.LookupPhoneNumber is { } phone && CustomerPaymentPanel.IsPactCustomer(customerKey) && !HasEdsmFigures(edsmPanel)
            && state is not (PaymentPanelState.Forbidden or PaymentPanelState.Disabled))
        {
            var tenant = Uri.UnescapeDataString(customerKey[CustomerPaymentPanel.PactKeyPrefix.Length..]);
            var lookup = await collections.LookupCustomerUnitsAsync(phone, UnitSelection(companyId, null), cancellationToken);
            if (lookup is { Outcome: ApiOutcome.Success, Value: { } link })
            {
                var mine = link.Candidates.Where(c => string.Equals(c.PactTenantId, tenant, StringComparison.Ordinal)).ToList();
                if (mine.Count > 0)
                {
                    var selected = mine.FirstOrDefault(c => c.SelectionId == link.SelectedId)?.SelectionId ?? (mine.Count == 1 ? mine[0].SelectionId : null);
                    return new CustomerPaymentPanel
                    {
                        CustomerKey = customerKey, State = PaymentPanelState.UnitSummary, UnitLink = link with { Candidates = mine, SelectedId = selected, SelectionRequired = mine.Count > 1 && selected is null },
                        Notice = notice, NoticeIsError = noticeIsError, Links = options.Links!, AllowSending = false
                    };
                }
            }
        }

        return edsmPanel;
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
            NoticeIsError = panel.NoticeIsError,
            Links = panel.Links,
            AllowSending = panel.AllowSending
        };
}
