using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Models;

public enum DocumentsPanelState
{
    /// <summary>Not fetched yet: site.js replaces this placeholder when the tab opens.</summary>
    Deferred,

    /// <summary>The documents of the selected unit (any group may still be empty or unavailable on its own).</summary>
    Loaded,
    UnitSelectionRequired,
    NoUnits,
    NotCrmCustomer,
    CustomerNotResolved,
    Forbidden,
    NotFound,

    /// <summary>CRM could not be reached or failed: retry may work.</summary>
    Unavailable,

    /// <summary>CRM refused or answered outside its contract: retrying will not fix it.</summary>
    Rejected,
    Error
}

/// <summary>What the Documents tab renders. Every state comes from what the Api answered; nothing is filled in.</summary>
public sealed class CustomerDocumentsPanel
{
    public required string CustomerKey { get; init; }
    public DocumentsPanelState State { get; init; } = DocumentsPanelState.Deferred;
    public CustomerDocumentsDto? Data { get; init; }
    public string? Message { get; init; }

    /// <summary>The unit asked for (the selector's value), kept even when the answer is an error so Retry repeats the same request.</summary>
    public int? RequestedLeadId { get; init; }

    public string? Notice { get; init; }
    public bool NoticeIsError { get; init; }

    /// <summary>False for read-only roles (the Api refuses their POSTs anyway): the Send button then says why instead of failing.</summary>
    public bool CanSend { get; set; } = true;

    public string TabHref(int? leadId) =>
        $"/Customers/{Uri.EscapeDataString(CustomerKey)}?tab={CustomerProfileTabs.Documents}{(leadId is { } id ? $"&unit={id}" : "")}#documents";

    public string FileHref(CustomerDocumentGroupDto group, CustomerDocumentDto document, bool download) =>
        $"/Customers/{Uri.EscapeDataString(CustomerKey)}?handler=DocumentFile&unit={Data!.SelectedLeadId}"
        + $"&type={Uri.EscapeDataString(group.DocumentType)}&record={Uri.EscapeDataString(document.RecordId)}{(download ? "&download=true" : "")}";

    public static string UnitLabel(CustomerDocumentUnitDto unit) =>
        $"{unit.UnitNumber ?? "—"}{(unit.ProjectName is null ? "" : $" · {unit.ProjectName}")}";

    /// <summary>
    /// What the agent is told after "Send to customer". The words follow the evidence: <c>Sent</c> from the delivery
    /// service means the mail server ACCEPTED the message — TigerCS has no delivery receipt, no bounce feed and no read
    /// receipt, so nothing here ever says "delivered".
    /// </summary>
    public static (string Message, bool IsError) SendNotice(ApiResult<CrmDocumentCopyResult> result, string documentName)
    {
        if (result.IsSuccess && result.Value is { } copy)
        {
            var to = copy.MaskedDestination is { } masked ? $" to {masked}" : "";
            switch (copy.Status)
            {
                case CrmDocumentCopyStatus.Sent when copy.Duplicate:
                    return ($"Not sent again: “{documentName}” was already sent{to} a moment ago. Nothing new was sent.", false);
                case CrmDocumentCopyStatus.Sent:
                    return ($"Accepted for sending: “{documentName}” was handed to the mail server{to} as an attachment. "
                            + "Accepted is not delivered — TigerCS receives no delivery or read receipt, so confirm with the customer that it arrived.", false);
                case CrmDocumentCopyStatus.Queued:
                    return ($"“{documentName}” is already being sent by an identical request. Nothing new was sent; check again shortly.", false);
                default:
                    return ($"“{documentName}” was not sent: {copy.Message ?? copy.Status.ToString()}", true);
            }
        }

        var reason = result.Outcome switch
        {
            ApiOutcome.Forbidden => result.Detail ?? "You are not allowed to send this document.",
            ApiOutcome.Unauthorized => "Your session is no longer valid. Sign in again.",
            ApiOutcome.Unreachable => "Tiger Ticketing System could not reach the ticketing service. Nothing is known to have been sent.",
            ApiOutcome.Conflict => result.Detail ?? "This request conflicts with an earlier one. Try again.",
            _ => result.Detail ?? "The document could not be sent."
        };
        return ($"“{documentName}” was not sent: {reason}", true);
    }
}

public static class CustomerProfileTabs
{
    public const string Documents = "documents";
}
