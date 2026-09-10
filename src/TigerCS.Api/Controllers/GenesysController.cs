using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The inbound Genesys boundary: Genesys tells TigerCS that an inquiry
/// reached an agent, and later that the conversation ended.
///
/// <para>
/// <b>Authentication is TigerCS', not an invented Genesys scheme.</b> Genesys
/// calls these endpoints as an ordinary authenticated TigerCS service
/// account — the same JWT bearer authentication every other client of this
/// API uses (<c>POST /api/auth/login</c>), scoped to the same policy as
/// manual ticket creation because that is precisely what these endpoints do.
/// No webhook signature header, HMAC scheme or OAuth client is implemented,
/// because none has been confirmed by the Genesys team; when the real
/// mechanism is known it is added here, at the boundary, without touching
/// anything behind it.
/// </para>
///
/// <para>
/// <b>The one ingestion path.</b> Phone, website chat, WhatsApp and social
/// media all post the same normalized inquiry to
/// <see cref="Ingest"/>; there is no per-channel endpoint and no per-channel
/// ticket-creation code. Genesys never reaches Tiger CRM, PACT or Tasleeh
/// directly — customer lookup happens inside TigerCS, through the services
/// the New Ticket wizard already uses.
/// </para>
/// </summary>
[ApiController]
[Route("api/genesys")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Genesys)]
public class GenesysController(
    GenesysInquiryIngestionAppService ingestionAppService,
    GenesysConversationEndAppService conversationEndAppService) : ControllerBase
{
    /// <summary>Report a Genesys inquiry that has reached an agent — creating exactly one ticket for the conversation.</summary>
    /// <remarks>
    /// <b>Idempotent on <c>conversationId</c>.</b> The first accepted event
    /// for a conversation creates a ticket; every retry or duplicate returns
    /// that same ticket with outcome <c>AlreadyIngested</c> and creates
    /// nothing. A unique index on the conversation id makes this hold under
    /// concurrent delivery, not merely under sequential retries.
    ///
    /// <para>
    /// <b>A ringing phone creates nothing.</b> <c>event: "Ringing"</c> is
    /// accepted and acknowledged with <c>204 No Content</c> — the ticket flow
    /// starts when the agent answers (<c>"Answered"</c>), or when a text
    /// conversation reaches an agent (<c>"Started"</c>).
    /// </para>
    ///
    /// <para>
    /// The department comes from the customer's explicit website-chat choice
    /// (Leasing / Customer Service / Maintenance) when there is one, and
    /// otherwise from the configured Genesys queue → department mapping.
    /// Request Type, Category and Priority are never inferred from it — the
    /// ticket starts unclassified for the agent or the workflow to classify.
    /// </para>
    /// </remarks>
    /// <param name="request">The normalized inquiry.</param>
    /// <response code="200">The conversation was already ingested — the same ticket is returned, and nothing was created.</response>
    /// <response code="201">A ticket was created for this conversation.</response>
    /// <response code="204">Accepted, and deliberately created nothing (a ringing call).</response>
    /// <response code="400">The channel or event value was not recognized, or conversationId was blank.</response>
    /// <response code="422">The inquiry could not be turned into a ticket — no department could be resolved, the department has no Genesys category configured, or ticket creation itself was refused.</response>
    /// <response code="503">The Genesys integration is switched off (<c>Genesys:Enabled</c> is false).</response>
    [HttpPost("inquiries")]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ingest([FromBody] GenesysInquiryRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        if (!GenesysContractMapper.TryMap(request, out var inquiry, out var mappingError))
        {
            ModelState.AddModelError(nameof(request.Channel), mappingError!);
            return ValidationProblem(ModelState);
        }

        var result = await ingestionAppService.IngestAsync(employeeId.Value, inquiry, cancellationToken);

        return result.Outcome switch
        {
            GenesysIngestionOutcome.TicketCreated => Created(
                $"/api/tickets/{result.Ticket!.TicketId}",
                new GenesysInquiryAcceptedResponse(
                    nameof(GenesysIngestionOutcome.TicketCreated), inquiry.ConversationId,
                    result.Ticket.TicketId, result.Ticket.TicketNumber)),

            // The retry answer: 200 rather than 201, same body, no new ticket.
            GenesysIngestionOutcome.AlreadyIngested when result.Ticket is not null => Ok(
                new GenesysInquiryAcceptedResponse(
                    nameof(GenesysIngestionOutcome.AlreadyIngested), inquiry.ConversationId,
                    result.Ticket.TicketId, result.Ticket.TicketNumber)),

            // Accepted and, by design, nothing was created.
            GenesysIngestionOutcome.NoTicketYet => NoContent(),

            GenesysIngestionOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys inquiry is processed while the integration is switched off. Normal ticket creation is unaffected.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysIngestionOutcome.ConversationIdRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-id-required",
                title: "Genesys conversation id required",
                detail: "conversationId is what makes this call idempotent — without it the inquiry cannot be accepted.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysIngestionOutcome.DepartmentNotResolved => Problem(
                type: "https://tigercs.internal/problems/genesys-department-not-resolved",
                title: "No department could be resolved for this inquiry",
                detail: result.Detail
                    ?? "The inquiry named no department and its queue has no active mapping. Configure the queue under Administration → Genesys routing.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.DepartmentNotConfiguredForGenesys => Problem(
                type: "https://tigercs.internal/problems/genesys-department-not-configured",
                title: "The department is not configured for Genesys inquiries",
                detail: result.Detail
                    ?? "The resolved department has no active Genesys ticket category configured.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.ChannelNotConfigured => Problem(
                type: "https://tigercs.internal/problems/genesys-channel-not-configured",
                title: "The inquiry's channel is not configured",
                detail: result.Detail ?? "No active channel is configured for this Genesys channel.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.TicketCreationFailed => Problem(
                type: "https://tigercs.internal/problems/genesys-ticket-creation-failed",
                title: "The ticket could not be created for this inquiry",
                detail: $"Ticket creation was refused: {result.TicketCreationOutcome}.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Report that a Genesys conversation ended or disconnected, and store its transcript.</summary>
    /// <remarks>
    /// Called whenever a conversation finishes for <b>any</b> reason — the
    /// agent ended it, the customer closed the browser, the connection
    /// dropped, Genesys timed it out — so the interaction is always finalized
    /// and the conversation history is never lost.
    ///
    /// <para>
    /// <b>Ending a conversation never closes the ticket.</b> The interaction
    /// is marked ended; the ticket continues under the existing TigerCS
    /// workflow (a customer who asked for an NOC has an open ticket long
    /// after the chat window closed). The response includes the ticket's
    /// unchanged status to make that visible.
    /// </para>
    ///
    /// <para>
    /// Idempotent: a redelivered end event answers <c>AlreadyEnded</c>
    /// without moving the recorded end time or duplicating the transcript.
    /// </para>
    /// </remarks>
    /// <param name="request">The conversation, when and why it ended, and the transcript available up to that moment.</param>
    /// <response code="200">The conversation was finalized, or was already ended.</response>
    /// <response code="400">conversationId was blank, or a transcript message had an unknown sender or an empty body.</response>
    /// <response code="404">No interaction exists for this conversation — it never produced a ticket (for example a call that rang and was never answered).</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPost("conversations/end")]
    [ProducesResponseType<GenesysConversationEndResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EndConversation(
        [FromBody] GenesysConversationEndRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await conversationEndAppService.EndAsync(
            employeeId.Value, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysConversationEndOutcome.Ended or GenesysConversationEndOutcome.AlreadyEnded => Ok(
                new GenesysConversationEndResponse(
                    result.Outcome.ToString(),
                    request.ConversationId,
                    result.TicketId,
                    result.TicketNumber,
                    result.TicketStatus,
                    result.TranscriptMessageCount)),

            GenesysConversationEndOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys event is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysConversationEndOutcome.ConversationIdRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-id-required",
                title: "Genesys conversation id required",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysConversationEndOutcome.InvalidTranscript => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-transcript",
                title: "The transcript could not be stored",
                detail: result.Detail
                    ?? "A transcript message had an unrecognized sender or an empty body. Nothing was stored — resend the whole transcript.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysConversationEndOutcome.ConversationNotFound => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-not-found",
                title: "No interaction exists for this conversation",
                detail: "This conversation never produced a ticket — for example a call that rang and was never answered.",
                statusCode: StatusCodes.Status404NotFound),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
