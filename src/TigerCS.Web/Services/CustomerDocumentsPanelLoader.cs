using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Services;

/// <summary>Builds the Documents tab from TigerCS.Api's <c>/api/customers/{key}/documents</c>: each Api outcome becomes its own state.</summary>
public sealed class CustomerDocumentsPanelLoader(CustomerDocumentsApiClient documents)
{
    public static CustomerDocumentsPanel Deferred(string customerKey, int? leadId) =>
        new() { CustomerKey = customerKey, State = DocumentsPanelState.Deferred, RequestedLeadId = leadId };

    public async Task<CustomerDocumentsPanel> LoadAsync(
        string customerKey, int? leadId, string? notice, bool noticeIsError, CancellationToken cancellationToken)
    {
        var result = await documents.ListAsync(customerKey, leadId, cancellationToken);

        CustomerDocumentsPanel Panel(DocumentsPanelState state, string? message = null) => new()
        {
            CustomerKey = customerKey, State = state, Message = message, RequestedLeadId = leadId, Notice = notice, NoticeIsError = noticeIsError
        };

        if (result.IsSuccess && result.Value is { } data)
        {
            var state = data.Status switch
            {
                "Ready" => DocumentsPanelState.Loaded,
                "UnitSelectionRequired" => DocumentsPanelState.UnitSelectionRequired,
                "NoUnits" => DocumentsPanelState.NoUnits,
                "NotCrmCustomer" => DocumentsPanelState.NotCrmCustomer,
                "CustomerNotResolved" => DocumentsPanelState.CustomerNotResolved,
                _ => DocumentsPanelState.Error
            };
            return new CustomerDocumentsPanel
            {
                CustomerKey = customerKey, State = state, Data = data, Message = data.Message, RequestedLeadId = leadId ?? data.SelectedLeadId,
                Notice = notice, NoticeIsError = noticeIsError
            };
        }

        return result.Outcome switch
        {
            ApiOutcome.Forbidden => Panel(DocumentsPanelState.Forbidden, result.Detail ?? "That unit is not one of this customer's units, or you don't have permission to view their documents."),
            ApiOutcome.NotFound or ApiOutcome.ValidationError => Panel(DocumentsPanelState.NotFound, "This customer could not be found."),
            ApiOutcome.ServiceUnavailable or ApiOutcome.Unreachable => Panel(DocumentsPanelState.Unavailable, result.Detail),
            ApiOutcome.BadGateway => Panel(DocumentsPanelState.Rejected, result.Detail),
            _ => Panel(DocumentsPanelState.Error, result.Detail)
        };
    }
}
