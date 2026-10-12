using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.CrmDocuments.Dto;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The one HTTP shape of a document-copy outcome, shared by the Genesys <c>send-copy</c> route and the agent desk's
/// "Send to customer" route: same statuses, same stable <c>code</c>, same identifiers — so both doors report what the
/// delivery service actually did, and neither can drift from the other.
/// </summary>
internal static class CrmDocumentCopyResponses
{
    public static IActionResult ToDocumentCopyResponse(this ControllerBase controller, CrmDocumentCopyResult result)
    {
        return result.Status switch
        {
            CrmDocumentCopyStatus.Sent or CrmDocumentCopyStatus.SelectionRequired => controller.Ok(result),
            CrmDocumentCopyStatus.Queued => controller.Accepted(result),

            CrmDocumentCopyStatus.InvalidRequest => Coded(controller, result, StatusCodes.Status400BadRequest, "Invalid document request"),
            CrmDocumentCopyStatus.VerificationFailed => Coded(controller, result, StatusCodes.Status403Forbidden, "Customer verification failed"),
            CrmDocumentCopyStatus.OwnershipMismatch => Coded(controller, result, StatusCodes.Status403Forbidden, "Record does not belong to the verified customer"),
            CrmDocumentCopyStatus.IdempotencyConflict => Coded(controller, result, StatusCodes.Status409Conflict, "Idempotency key already used"),
            CrmDocumentCopyStatus.Disabled => Coded(controller, result, StatusCodes.Status503ServiceUnavailable, "Document copies are switched off"),

            CrmDocumentCopyStatus.DocumentUnavailable => Coded(
                controller, result,
                result.Code switch
                {
                    CrmDocumentCodes.DocumentNotFound => StatusCodes.Status404NotFound,
                    // CRM said 400/401/403 or sent a body that breaks its contract: an upstream defect or
                    // configuration problem, not "try again" — a 502, with the CRM-specific code.
                    CrmDocumentCodes.CrmRequestRejected
                        or CrmDocumentCodes.CrmAuthenticationFailed
                        or CrmDocumentCodes.CrmAccessDenied
                        or CrmDocumentCodes.CrmInvalidResponse => StatusCodes.Status502BadGateway,
                    // Unreachable, timed out, 500 or 503.
                    _ => StatusCodes.Status503ServiceUnavailable
                },
                result.Code == CrmDocumentCodes.DocumentNotFound ? "Document not found" : "Document source unavailable"),

            CrmDocumentCopyStatus.DeliveryFailed => Coded(
                controller, result,
                result.Code switch
                {
                    CrmDocumentCodes.DeliveryChannelNotIntegrated => StatusCodes.Status501NotImplemented,
                    CrmDocumentCodes.DeliveryDestinationUnavailable => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status502BadGateway
                },
                "Document could not be delivered"),

            _ => controller.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>The standard ProblemDetails body plus the stable machine-readable <c>code</c> and the same relevant identifiers the success body carries.</summary>
    private static ObjectResult Coded(ControllerBase controller, CrmDocumentCopyResult result, int statusCode, string title)
    {
        var problem = controller.Problem(
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
}
