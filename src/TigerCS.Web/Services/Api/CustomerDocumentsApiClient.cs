using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CrmDocuments.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/customers/{customerKey}/documents</c> routes: the Customer Details Documents tab's
/// listing, the authorized file behind each document, and "Send to customer". Never CRM directly — the Api owns the CRM
/// credential, the ownership checks and the delivery service.
/// </summary>
public sealed class CustomerDocumentsApiClient(HttpClient httpClient, ILogger<CustomerDocumentsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<CustomerDocumentsDto>> ListAsync(string customerKey, int? crmLeadId, CancellationToken cancellationToken) =>
        GetAsync<CustomerDocumentsDto>(
            $"{Base(customerKey)}{(crmLeadId is { } lead ? $"?crmLeadId={lead.ToString(CultureInfo.InvariantCulture)}" : "")}", cancellationToken);

    public Task<ApiResult<ApiFile>> GetFileAsync(
        string customerKey, int crmLeadId, string documentType, string recordId, bool download, CancellationToken cancellationToken) =>
        GetFileAsync(
            $"{Base(customerKey)}/{crmLeadId.ToString(CultureInfo.InvariantCulture)}/{Uri.EscapeDataString(documentType)}/{Uri.EscapeDataString(recordId)}/file"
            + (download ? "?download=true" : ""),
            cancellationToken);

    public Task<ApiResult<CrmDocumentCopyResult>> SendAsync(
        string customerKey, CustomerDocumentSendRequestDto request, string idempotencyKey, CancellationToken cancellationToken) =>
        PostAsync<CustomerDocumentSendRequestDto, CrmDocumentCopyResult>(
            $"{Base(customerKey)}/send", request, new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey }, cancellationToken);

    private static string Base(string customerKey) => $"api/customers/{Uri.EscapeDataString(customerKey)}/documents";
}
