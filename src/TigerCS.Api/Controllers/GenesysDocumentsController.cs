using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The chatbot's "send me a copy of my document" request:
/// <c>POST /api/genesys/documents/send-copy</c>.
///
/// <para>
/// <b>Same door as every other Genesys route.</b> Genesys never calls TigerCS
/// directly: Genesys authenticates to TigerGroupWeb (OAuth client
/// credentials), which forwards the request here as the TigerCS integration
/// service account, under the same JWT bearer authentication and the same
/// <c>CustomerVerification</c> policy as <c>/api/genesys/*</c>. There is no
/// anonymous document endpoint and no document download endpoint at all — the
/// document is delivered to the customer, never returned to the caller.
/// </para>
///
/// <para>
/// <b>Prerequisites outside this repository</b> (see
/// <c>docs/Genesys/Document-Copy-API.md</c>): the TigerGroupWeb proxy must
/// forward this route, and Tiger CRM must publish the document operations
/// behind <c>ICrmDocumentGateway</c> — until then the API answers
/// <c>DocumentUnavailable</c> / <c>DOCUMENT_SOURCE_UNAVAILABLE</c>.
/// </para>
/// </summary>
[ApiController]
[Route("api/genesys/documents")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Genesys)]
public class GenesysDocumentsController(CrmDocumentCopyAppService documentCopyAppService) : ControllerBase
{
    /// <summary>Send the verified customer a copy of one of their documents.</summary>
    /// <remarks>
    /// Identity comes from <c>verificationSessionId</c> (a confirmed session from
    /// <c>POST /api/verification-sessions</c>, method Otp or AuthenticatedDigitalUser, owned by
    /// the calling service account) — never from a phone number or customer id, which this
    /// request does not accept. The document goes to the customer's email on record in CRM, as
    /// an attachment; the response never contains the document or the full address.
    ///
    /// <para>
    /// <b>Idempotent.</b> Send an <c>Idempotency-Key</c> header and reuse it on every retry of the
    /// same request: a replay of a sent request answers <c>Sent</c> with <c>duplicate: true</c> and
    /// sends nothing; a replay while the first is still running answers <c>Queued</c> (202). Use a
    /// new key for a new request, including the follow-up call that carries the customer's
    /// <c>recordId</c> choice.
    /// </para>
    /// </remarks>
    /// <param name="request">Verification session, document type, and — when asked to choose — the chosen record.</param>
    /// <param name="idempotencyKey">Required. 1–128 characters of letters, digits and <c>. _ : -</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">status <c>Sent</c> (also for a replay, with <c>duplicate: true</c>), or <c>SelectionRequired</c> with <c>choices</c> (nothing sent).</response>
    /// <response code="202">status <c>Queued</c> — an identical request is still being processed; nothing new was sent.</response>
    /// <response code="400">INVALID_REQUEST — missing/invalid Idempotency-Key, verificationSessionId, documentType or deliveryChannel.</response>
    /// <response code="401">Missing or invalid bearer token.</response>
    /// <response code="403">VERIFICATION_FAILED (unknown, foreign, unconfirmed, expired or weak-method session) or RECORD_OWNERSHIP_MISMATCH (unit or record is not the verified customer's).</response>
    /// <response code="404">DOCUMENT_NOT_FOUND — the customer has no such document.</response>
    /// <response code="409">IDEMPOTENCY_KEY_REUSED — the key was already used for a different request.</response>
    /// <response code="422">DELIVERY_DESTINATION_UNAVAILABLE — CRM holds no valid email for the customer.</response>
    /// <response code="501">DELIVERY_CHANNEL_NOT_INTEGRATED — WhatsApp/SMS were requested; only Email is integrated.</response>
    /// <response code="502">DELIVERY_FAILED or DOCUMENT_TOO_LARGE — the document was found but not delivered (<c>retryable</c> says whether to retry with the same key).</response>
    /// <response code="503">DOCUMENT_SOURCE_UNAVAILABLE (CRM has no document endpoint / is unreachable) or DOCUMENT_COPY_DISABLED.</response>
    [HttpPost("send-copy")]
    [ProducesResponseType<CrmDocumentCopyResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<CrmDocumentCopyResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status501NotImplemented)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SendCopy(
        [FromBody] CrmDocumentCopyRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await documentCopyAppService.SendAsync(employeeId.Value, request, idempotencyKey, cancellationToken);

        return result.Status switch
        {
            CrmDocumentCopyStatus.Sent or CrmDocumentCopyStatus.SelectionRequired => Ok(result),
            CrmDocumentCopyStatus.Queued => Accepted(result),

            CrmDocumentCopyStatus.InvalidRequest => Coded(result, StatusCodes.Status400BadRequest, "Invalid document request"),
            CrmDocumentCopyStatus.VerificationFailed => Coded(result, StatusCodes.Status403Forbidden, "Customer verification failed"),
            CrmDocumentCopyStatus.OwnershipMismatch => Coded(result, StatusCodes.Status403Forbidden, "Record does not belong to the verified customer"),
            CrmDocumentCopyStatus.IdempotencyConflict => Coded(result, StatusCodes.Status409Conflict, "Idempotency key already used"),
            CrmDocumentCopyStatus.Disabled => Coded(result, StatusCodes.Status503ServiceUnavailable, "Document copies are switched off"),

            CrmDocumentCopyStatus.DocumentUnavailable => Coded(
                result,
                result.Code == CrmDocumentCodes.DocumentSourceUnavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status404NotFound,
                result.Code == CrmDocumentCodes.DocumentSourceUnavailable ? "Document source unavailable" : "Document not found"),

            CrmDocumentCopyStatus.DeliveryFailed => Coded(
                result,
                result.Code switch
                {
                    CrmDocumentCodes.DeliveryChannelNotIntegrated => StatusCodes.Status501NotImplemented,
                    CrmDocumentCodes.DeliveryDestinationUnavailable => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status502BadGateway
                },
                "Document could not be delivered"),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>The standard ProblemDetails body plus the stable machine-readable <c>code</c> and the same relevant identifiers the success body carries.</summary>
    private ObjectResult Coded(CrmDocumentCopyResult result, int statusCode, string title)
    {
        var problem = Problem(
            type: $"https://tigercs.internal/problems/{(result.Code ?? "document-copy").ToLowerInvariant().Replace('_', '-')}",
            title: title, detail: result.Message, statusCode: statusCode);

        if (problem.Value is ProblemDetails details)
        {
            details.Extensions["code"] = result.Code;
            details.Extensions["outcome"] = result.Status.ToString();
            if (result.DocumentType is not null) details.Extensions["documentType"] = result.DocumentType;
            if (result.RecordId is not null) details.Extensions["recordId"] = result.RecordId;
            if (result.DeliveryChannel is not null) details.Extensions["deliveryChannel"] = result.DeliveryChannel;
            if (result.DeliveryRequestId is not null) details.Extensions["deliveryRequestId"] = result.DeliveryRequestId;
            if (result.Retryable is not null) details.Extensions["retryable"] = result.Retryable;
        }

        return problem;
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
