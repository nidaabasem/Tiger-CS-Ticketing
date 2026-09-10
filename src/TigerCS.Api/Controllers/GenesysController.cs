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
    GenesysTicketUpdateAppService ticketUpdateAppService,
    GenesysCustomerLookupAppService customerLookupAppService) : ControllerBase
{
    /// <summary>Create — or reuse — the one ticket for a Genesys conversation, on any channel.</summary>
    /// <remarks>
    /// <b>Idempotent on <c>conversationId</c>.</b> The first accepted event
    /// for a conversation creates a ticket; every retry or duplicate returns
    /// that same ticket with outcome <c>AlreadyIngested</c> and creates
    /// nothing. A unique index on the conversation id makes this hold under
    /// concurrent delivery, not merely under sequential retries.
    ///
    /// <para>
    /// <b>This endpoint means exactly one thing:</b> create or reuse the
    /// ticket for this conversation. It is not an event receiver for call
    /// progress, and there is no event field — a ringing call simply never
    /// reaches TigerCS. The phone flow starts at pickup: look the caller up,
    /// then post this. Digital channels (website chat, chatbot, WhatsApp,
    /// social) post it when the conversation starts.
    /// </para>
    ///
    /// <para>
    /// The department comes from the customer's explicit website-chat choice
    /// (Leasing / Customer Service / Maintenance) when there is one, and
    /// otherwise from the configured Genesys queue → department mapping.
    /// Category, Request Type and Priority are never inferred from it: the
    /// ticket is created <b>Unclassified</b> (no category at all — never a
    /// placeholder one) and therefore starts no SLA clock. An agent
    /// classifies it afterwards via
    /// <c>POST /api/tickets/{ticketId}/classification</c>, on the same
    /// ticket, and that is when the SLA clock starts.
    /// </para>
    /// </remarks>
    /// <param name="request">The normalized inquiry.</param>
    /// <response code="200">The conversation was already ingested — the same ticket is returned, and nothing was created.</response>
    /// <response code="201">A ticket was created for this conversation.</response>
    /// <response code="400">The channel was not recognized, or conversationId was blank.</response>
    /// <response code="422">The inquiry could not be turned into a ticket — no department could be resolved, or ticket creation itself was refused.</response>
    /// <response code="503">The Genesys integration is switched off (<c>Genesys:Enabled</c> is false).</response>
    [HttpPost("tickets")]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status201Created)]
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

    /// <summary>Update the Genesys-owned side of a ticket: agent context, conversation end and transcript, and human-handoff state.</summary>
    /// <remarks>
    /// The one update contract. Every part of the body is optional and
    /// independently idempotent — send only what changed, and resend freely.
    ///
    /// <para>
    /// <b>Genesys owns the conversation; TigerCS owns the ticket.</b> This
    /// accepts conversation facts only. There is deliberately no field for
    /// category, request type, priority, status, owner, department,
    /// resolution or closure: those move through their own TigerCS
    /// operations, with their own authorization and SLA consequences.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here closes the ticket.</b> Ending a conversation finalizes
    /// the interaction and stores the transcript; the ticket carries on under
    /// the existing workflow. The response echoes <c>ticketStatus</c> on
    /// every call to make that visible.
    /// </para>
    ///
    /// <para>
    /// <c>conversationId</c> must resolve to an interaction on the ticket in
    /// the route — that cross-check is what stops a transcript being applied
    /// to the wrong ticket. A malformed transcript is refused before anything
    /// is written, so a bad update never leaves a half-stored conversation.
    /// </para>
    /// </remarks>
    /// <param name="ticketId">The ticket, as returned by <c>POST /api/genesys/tickets</c>.</param>
    /// <param name="request">The conversation facts that changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Everything supplied was applied, or was already in that state.</response>
    /// <response code="400">conversationId was blank, a transcript message had an unrecognized sender or empty body, or the handoff mode is not recognized.</response>
    /// <response code="404">No such ticket, or no interaction exists for this conversation.</response>
    /// <response code="409">The conversation belongs to a different ticket than the one in the route.</response>
    /// <response code="422">A handoff assignment was supplied but no outstanding human work exists to apply it to.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPatch("tickets/{ticketId:long}")]
    [ProducesResponseType<GenesysTicketUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateTicket(
        long ticketId, [FromBody] GenesysTicketUpdateRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketUpdateAppService.UpdateAsync(
            employeeId.Value, ticketId, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysTicketUpdateOutcome.Applied => Ok(
                new GenesysTicketUpdateResponse(
                    nameof(GenesysTicketUpdateOutcome.Applied), request.ConversationId,
                    result.TicketId!.Value, result.TicketNumber!, result.TicketStatus!,
                    result.ConversationEnded, result.TranscriptMessageCount,
                    result.HandoffStatus, result.TicketAgentHandoffId)),

            GenesysTicketUpdateOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys event is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysTicketUpdateOutcome.ConversationIdRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-id-required",
                title: "Genesys conversation id required",
                detail: "conversationId is what ties an update to its interaction — without it the update cannot be accepted.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidTranscript => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-transcript",
                title: "The transcript could not be stored",
                detail: result.Detail
                    ?? "A transcript message had an unrecognized sender or an empty body. Nothing was stored — resend the whole update.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidHandoffMode => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-handoff-mode",
                title: "Unrecognized follow-up mode",
                detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.TicketNotFound => Problem(
                type: "https://tigercs.internal/problems/ticket-not-found",
                title: "No such ticket",
                statusCode: StatusCodes.Status404NotFound),

            GenesysTicketUpdateOutcome.ConversationNotFound => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-not-found",
                title: "No interaction exists for this conversation",
                detail: result.Detail ?? "This conversation never produced a ticket.",
                statusCode: StatusCodes.Status404NotFound),

            GenesysTicketUpdateOutcome.ConversationTicketMismatch => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-ticket-mismatch",
                title: "This conversation belongs to a different ticket",
                detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.NoOpenHandoff => Problem(
                type: "https://tigercs.internal/problems/genesys-no-open-handoff",
                title: "No outstanding human work for this conversation",
                detail: result.Detail,
                statusCode: StatusCodes.Status422UnprocessableEntity),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Look a caller up by phone number when an agent picks up — customer, units, and their existing tickets.</summary>
    /// <remarks>
    /// The call-pickup flow: a ringing call does nothing, and when the agent
    /// answers Genesys calls this to find out who is on the line before
    /// creating the ticket.
    ///
    /// <para>
    /// <b>Context, not a yes/no.</b> Where a customer is found this returns
    /// the CRM Buyer with every eligible unit, the PACT/Tasleeh matches, and
    /// the tickets that already arrived from this number — with the open ones
    /// first, so an agent can see "this caller already has an NOC request
    /// running" before they speak.
    /// </para>
    ///
    /// <para>
    /// <b>Never a gate.</b> <c>found: false</c> is a normal answer and
    /// <c>200 OK</c>, not a 404 — and neither it nor a lookup source being
    /// down stops the Create Ticket call that follows.
    /// </para>
    ///
    /// <para>
    /// This reuses the same CRM Buyer Lookup and PACT/Tasleeh services the
    /// New Ticket wizard uses. There is no second CRM integration, and
    /// Genesys never reaches those systems directly. Read-only: nothing is
    /// created or persisted.
    /// </para>
    /// </remarks>
    /// <param name="phoneNumber">Required. The caller's number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">What each source found — possibly nothing, which is a normal answer.</response>
    /// <response code="400">phoneNumber was missing or blank.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpGet("customers/lookup")]
    [ProducesResponseType<GenesysCustomerLookupResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LookUpCustomer(
        [FromQuery] string phoneNumber, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            ModelState.AddModelError(nameof(phoneNumber), "phoneNumber is required.");
            return ValidationProblem(ModelState);
        }

        var result = await customerLookupAppService.LookUpAsync(phoneNumber, cancellationToken);

        return result is null
            ? Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Ok(result);
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
