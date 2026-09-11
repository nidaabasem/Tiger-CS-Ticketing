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
    GenesysCustomerLookupAppService customerLookupAppService,
    GenesysAgentContextAppService agentContextAppService) : ControllerBase
{
    /// <summary>Stable, machine-readable error codes for the agent identity mapping — carried as the <c>code</c> member of the standard ProblemDetails body.</summary>
    public static class ErrorCodes
    {
        public const string AgentNotMapped = "GENESYS_AGENT_NOT_MAPPED";
        public const string AgentInactive = "GENESYS_AGENT_INACTIVE";
    }

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

    /// <summary>Identify the Genesys agent working in Ticketing, and record them as the handler of the conversation's interaction.</summary>
    /// <remarks>
    /// The <b>agent action</b> endpoint: when an agent opens or works with
    /// Ticketing from Genesys, this resolves exactly which Ticketing user that
    /// agent is — by the immutable Genesys User ID, never by name or email —
    /// and answers with that user's id, roles and departments.
    ///
    /// <para>
    /// <b>Mapping is administrative, never automatic.</b> A Genesys User ID
    /// that no Ticketing user carries is refused with <c>403</c> and the code
    /// <c>GENESYS_AGENT_NOT_MAPPED</c>; no user is ever created here. Set the
    /// mapping on the Ticketing user (<c>AspNetUsers.GenesysUserId</c>) first.
    /// </para>
    ///
    /// <para>
    /// <b>The handler is resolved on the server.</b> The request carries no
    /// Ticketing user id, and none would be honoured: the interaction's
    /// <c>HandledByUserId</c> is whatever the Genesys User ID maps to. When
    /// <c>conversationId</c> is supplied it must resolve to an ingested
    /// conversation; the first agent resolved on an interaction is the one
    /// recorded, and a later agent does not overwrite it.
    /// </para>
    ///
    /// <para>
    /// <b>Records who handled the interaction and nothing else.</b> The
    /// ticket's assignee, department, queue, status and workflow are not
    /// changed by this call.
    /// </para>
    /// </remarks>
    /// <param name="request">The agent's Genesys identity, and the conversation being worked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The agent resolved to a Ticketing user; when a conversation was supplied, its interaction now records that user as the handler.</response>
    /// <response code="400">genesysUserId was missing or blank.</response>
    /// <response code="403">The Genesys agent is not mapped to a Ticketing user (<c>GENESYS_AGENT_NOT_MAPPED</c>), or the mapped user is deactivated (<c>GENESYS_AGENT_INACTIVE</c>).</response>
    /// <response code="404">A conversation id was supplied but no interaction exists for it.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPost("agent-context")]
    [ProducesResponseType<GenesysAgentContextResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResolveAgentContext(
        [FromBody] GenesysAgentContextRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        // The ordinary validation answer, before any lookup: an agent action
        // without the agent's id is a malformed request, not a mapping gap.
        if (string.IsNullOrWhiteSpace(request.GenesysUserId))
        {
            ModelState.AddModelError(nameof(request.GenesysUserId), "genesysUserId is required.");
            return ValidationProblem(ModelState);
        }

        var result = await agentContextAppService.ResolveAsync(
            employeeId.Value, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysAgentContextOutcome.Resolved => Ok(
                new GenesysAgentContextResponse(
                    nameof(GenesysAgentContextOutcome.Resolved),
                    result.Agent!.GenesysUserId,
                    result.Agent.UserId,
                    result.Agent.UserName,
                    result.Agent.DisplayName,
                    result.Roles ?? [],
                    result.DepartmentIds ?? [],
                    string.IsNullOrWhiteSpace(request.ConversationId) ? null : request.ConversationId.Trim(),
                    result.TicketId,
                    result.TicketNumber,
                    result.TicketInteractionId,
                    result.HandledByUserId)),

            GenesysAgentContextOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysAgentContextOutcome.AgentIdRequired => ValidationProblem(
                new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [nameof(request.GenesysUserId)] = [result.Detail ?? "genesysUserId is required."]
                })),

            GenesysAgentContextOutcome.AgentNotMapped => CodedProblem(
                ErrorCodes.AgentNotMapped,
                type: "https://tigercs.internal/problems/genesys-agent-not-mapped",
                title: "Genesys agent is not mapped to a Ticketing user",
                detail: "Genesys agent is not mapped to a Ticketing user.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysAgentContextOutcome.AgentInactive => CodedProblem(
                ErrorCodes.AgentInactive,
                type: "https://tigercs.internal/problems/genesys-agent-inactive",
                title: "The Ticketing user mapped to this Genesys agent is deactivated",
                detail: result.Detail ?? "The Ticketing user mapped to this Genesys agent is deactivated.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysAgentContextOutcome.ConversationNotFound => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-not-found",
                title: "No interaction exists for this conversation",
                detail: result.Detail ?? "This conversation never produced a ticket.",
                statusCode: StatusCodes.Status404NotFound),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// The standard ProblemDetails body every other refusal on this API uses,
    /// plus a stable machine-readable <c>code</c> member — so an integrator
    /// can branch on the code while the human-facing title/detail stay free
    /// to change. Not a second error format.
    /// </summary>
    private ObjectResult CodedProblem(string code, string type, string title, string detail, int statusCode)
    {
        var result = Problem(type: type, title: title, detail: detail, statusCode: statusCode);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
        }

        return result;
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
