using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Collections on the Genesys boundary: what a customer owes, what they have
/// paid, who is due a payment reminder, recording a reminder, and the
/// reminder's delivery events and customer responses.
///
/// <para>
/// <b>Two callers, one contract.</b> The Genesys outbound voice bot reaches
/// these routes the same way it reaches the rest of <c>api/genesys</c> —
/// through TigerGroupWeb, signed in as a TigerCS integration service
/// account; Genesys never holds a TigerCS credential. TigerCS Web's Payment
/// tab calls the same routes server-side as the signed-in user. Every route
/// authorizes the actual caller against the explicit Collections financial
/// permissions (<see cref="CollectionsAuthorizationService"/>) on top of the
/// authenticated-staff policy; ticket permissions are not involved.
/// </para>
///
/// <para>
/// <b>No balance is ever invented.</b> Figures come from the authoritative
/// financial source on every call. While no source is integrated the
/// financial routes answer <c>503 collections-source-unavailable</c> — never
/// an empty list or a zero that could read as "nothing owed".
/// </para>
/// </summary>
[ApiController]
[Route("api/genesys/collections")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Collections)]
public class GenesysCollectionsController(
    CollectionsAccountQueryAppService queries,
    CollectionsReminderAppService reminders,
    CollectionsReminderOutcomeAppService outcomes) : ControllerBase
{
    /// <summary>A customer's accounts: remaining, overdue, due-today and future principal, payable fines and fees, amount due now, this month's remainder, the next payment, and the instalment schedule.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">Narrow to one account. Must belong to the customer.</param>
    /// <param name="crmUnitId">Narrow to the account(s) for one CRM unit. Must belong to the customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The customer's accounts, each in its own currency, with the source and as-of time.</response>
    /// <response code="400">crmCustomerId was blank or too long.</response>
    /// <response code="403">The caller lacks the Collections financial-read permission.</response>
    /// <response code="404">The source does not know the customer, or the account/unit is not the customer's.</response>
    /// <response code="503">Collections is switched off, or the financial source is unavailable.</response>
    [HttpGet("customers/{crmCustomerId}/outstanding")]
    [ProducesResponseType<CollectionsOutstandingResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetOutstanding(
        string crmCustomerId, [FromQuery] string? accountId, [FromQuery] string? crmUnitId, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        return ToResponse(await queries.GetOutstandingAsync(caller, crmCustomerId, accountId, crmUnitId, cancellationToken));
    }

    /// <summary>A customer's payment history, newest first. Posted payments only unless <c>includeUnposted</c> is set; an unposted payment never counts toward a balance.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">Narrow to one account.</param>
    /// <param name="crmUnitId">Narrow to one CRM unit.</param>
    /// <param name="includeUnposted">Also list payments pending verification, reversed or rejected — each marked <c>countsTowardBalance: false</c>.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">1–100, default 25.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of payments.</response>
    /// <response code="400">crmCustomerId blank, or paging out of range.</response>
    /// <response code="403">The caller lacks the Collections financial-read permission.</response>
    /// <response code="404">Unknown customer, or the account/unit is not the customer's.</response>
    /// <response code="503">Collections is switched off, or the financial source is unavailable.</response>
    [HttpGet("customers/{crmCustomerId}/payments")]
    [ProducesResponseType<CollectionsPaymentsResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPayments(
        string crmCustomerId,
        [FromQuery] string? accountId,
        [FromQuery] string? crmUnitId,
        [FromQuery] bool includeUnposted = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        return ToResponse(await queries.GetPaymentsAsync(caller, crmCustomerId, accountId, crmUnitId, includeUnposted, page, pageSize, cancellationToken));
    }

    /// <summary>Accounts due a scheduled reminder today, with the amount to state and the channels not yet used this cycle.</summary>
    /// <remarks>
    /// Windows: days 1–4 for principal overdue more than one month, day 15 for
    /// the current month's unpaid payment, three days before month end for an
    /// unsettled current month. Settled accounts and accounts whose source data
    /// is inconsistent are never listed. Pull this before an outbound campaign,
    /// then record each call with <c>POST /reminders</c> immediately before dialing.
    /// </remarks>
    /// <param name="reminderType">OverdueMoreThanOneMonth, CurrentMonthDue or MonthEndFollowUp.</param>
    /// <param name="channel">VoiceBot, Sms or Email.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">1–100, default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of candidates (possibly empty — no window open, or nobody due).</response>
    /// <response code="400">An unrecognized filter, or paging out of range.</response>
    /// <response code="403">The caller lacks the Collections reminder permission.</response>
    /// <response code="503">Collections is switched off, or the financial source is unavailable.</response>
    [HttpGet("reminders/candidates")]
    [ProducesResponseType<CollectionsReminderCandidatesResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListCandidates(
        [FromQuery] string? reminderType,
        [FromQuery] string? channel,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        return ToResponse(await reminders.ListCandidatesAsync(caller, reminderType, channel, page, pageSize, cancellationToken));
    }

    /// <summary>Record (and, for SMS/email, queue for dispatch) one reminder for one account, after revalidating the balance.</summary>
    /// <remarks>
    /// <b>Idempotent per account / reminder type / cycle / channel.</b> A second
    /// request in the same cycle returns the existing reminder with
    /// <c>outcome: "AlreadyExists"</c> and <c>200</c>; nothing is sent twice.
    /// A settled account is refused with <c>422</c>. VoiceBot reminders are
    /// recorded only by the Genesys integration account, right before it dials.
    /// </remarks>
    /// <param name="request">The customer, account, channel and reminder type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">This reminder already exists for the cycle — returned, nothing new queued.</response>
    /// <response code="201">The reminder was recorded with its amount (and queued for SMS/email dispatch).</response>
    /// <response code="400">A required field is missing or unrecognized, or a VoiceBot reminder from a non-integration caller.</response>
    /// <response code="403">The caller lacks the Collections reminder permission.</response>
    /// <response code="404">Unknown customer, or the account is not the customer's.</response>
    /// <response code="422">Not eligible (settled, inconsistent source, window closed, no contact detail) or the channel is not enabled.</response>
    /// <response code="503">Collections is switched off, or the financial source is unavailable.</response>
    [HttpPost("reminders")]
    [ProducesResponseType<CreateCollectionsReminderResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<CreateCollectionsReminderResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateReminder([FromBody] CreateCollectionsReminderRequestDto request, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await reminders.CreateAsync(caller, request, cancellationToken);
        return result.Outcome == CollectionsOutcome.Created
            ? Created($"/api/genesys/collections/customers/{Uri.EscapeDataString(result.Value!.Reminder.CrmCustomerId)}/reminders", result.Value)
            : ToResponse(result);
    }

    /// <summary>Report a reminder's delivery event (Sent, Delivered, Failed) or the customer's response.</summary>
    /// <remarks>
    /// <b>Idempotent on <c>eventId</c>.</b> A resend returns
    /// <c>outcome: "AlreadyRecorded"</c> with what was stored the first time.
    ///
    /// <para>
    /// A <c>CustomerResponded</c> event with a <c>conversationId</c> creates or
    /// reuses that conversation's ticket through the existing Genesys
    /// ingestion, routed to Collections by configuration, Unclassified, never
    /// resolved or closed. AlreadyPaid opens a verification follow-up and posts
    /// nothing; RequestedHuman, AiDisconnected and Disputed keep human
    /// follow-up outstanding. When the ticket cannot be created yet the
    /// response is still stored and the answer is <c>202</c>: the ticket is
    /// retried durably.
    /// </para>
    /// </remarks>
    /// <param name="reminderId">The reminder, as returned by <c>POST /reminders</c>.</param>
    /// <param name="request">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Recorded (or already recorded); any ticket it owed is linked.</response>
    /// <response code="202">Recorded; its ticket is pending and will be retried.</response>
    /// <response code="400">Missing/unrecognized eventId, outcome or response kind.</response>
    /// <response code="403">Only a configured integration account may report outcomes.</response>
    /// <response code="404">No such reminder.</response>
    /// <response code="409">The reminder was suppressed before dispatch and accepts no outcomes.</response>
    /// <response code="503">Collections is switched off.</response>
    [HttpPost("reminders/{reminderId:long}/outcomes")]
    [ProducesResponseType<RecordReminderOutcomeResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<RecordReminderOutcomeResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecordOutcome(
        long reminderId, [FromBody] RecordReminderOutcomeRequestDto request, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await outcomes.RecordAsync(caller, reminderId, request, cancellationToken);
        return result.Outcome == CollectionsOutcome.Accepted
            ? Accepted(result.Value)
            : ToResponse(result);
    }

    /// <summary>A customer's reminder history — amounts, delivery status, every event and response, and linked tickets. Read from TigerCS's own records, so it stays available while the financial source is down.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">Narrow to one account.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">1–100, default 25.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of reminders, newest first.</response>
    /// <response code="400">crmCustomerId blank, or paging out of range.</response>
    /// <response code="403">The caller lacks the Collections financial-read permission.</response>
    /// <response code="503">Collections is switched off.</response>
    [HttpGet("customers/{crmCustomerId}/reminders")]
    [ProducesResponseType<CollectionsReminderListResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetReminders(
        string crmCustomerId,
        [FromQuery] string? accountId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        return ToResponse(await queries.GetRemindersAsync(caller, crmCustomerId, accountId, page, pageSize, cancellationToken));
    }

    private IActionResult ToResponse<T>(CollectionsResult<T> result) => result.Outcome switch
    {
        CollectionsOutcome.Success or CollectionsOutcome.AlreadyExists => Ok(result.Value),
        CollectionsOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Value),
        CollectionsOutcome.Accepted => Accepted(result.Value),
        CollectionsOutcome.Disabled => Problem(
            type: "https://tigercs.internal/problems/collections-disabled",
            title: "Collections is disabled",
            detail: "Collections:Enabled is false — no Collections request is processed.",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        CollectionsOutcome.SourceUnavailable => Problem(
            type: "https://tigercs.internal/problems/collections-source-unavailable",
            title: "The financial source is unavailable",
            detail: result.Detail,
            statusCode: StatusCodes.Status503ServiceUnavailable),
        CollectionsOutcome.Forbidden => Problem(
            type: "https://tigercs.internal/problems/collections-forbidden",
            title: "Not permitted",
            detail: result.Detail,
            statusCode: StatusCodes.Status403Forbidden),
        CollectionsOutcome.ValidationFailed => Problem(
            type: "https://tigercs.internal/problems/collections-invalid-request",
            title: "The request is not valid",
            detail: result.Detail,
            statusCode: StatusCodes.Status400BadRequest),
        CollectionsOutcome.CustomerNotFound => Problem(
            type: "https://tigercs.internal/problems/collections-customer-not-found",
            title: "Customer not found",
            detail: result.Detail,
            statusCode: StatusCodes.Status404NotFound),
        CollectionsOutcome.AccountNotFound => Problem(
            type: "https://tigercs.internal/problems/collections-account-not-found",
            title: "Account not found for this customer",
            detail: result.Detail,
            statusCode: StatusCodes.Status404NotFound),
        CollectionsOutcome.ReminderNotFound => Problem(
            type: "https://tigercs.internal/problems/collections-reminder-not-found",
            title: "Reminder not found",
            detail: result.Detail,
            statusCode: StatusCodes.Status404NotFound),
        CollectionsOutcome.NotEligible => Problem(
            type: "https://tigercs.internal/problems/collections-reminder-not-eligible",
            title: "The account is not eligible for this reminder",
            detail: result.Detail,
            statusCode: StatusCodes.Status422UnprocessableEntity),
        CollectionsOutcome.ChannelNotEnabled => Problem(
            type: "https://tigercs.internal/problems/collections-channel-not-enabled",
            title: "The reminder channel is not enabled",
            detail: result.Detail,
            statusCode: StatusCodes.Status422UnprocessableEntity),
        CollectionsOutcome.Conflict => Problem(
            type: "https://tigercs.internal/problems/collections-reminder-conflict",
            title: "The reminder cannot accept this event",
            detail: result.Detail,
            statusCode: StatusCodes.Status409Conflict),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private CollectionsCaller? Caller()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return null;
        }

        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var departments = User.FindAll(TigerCsClaimTypes.DepartmentId)
            .Select(c => int.TryParse(c.Value, out var id) ? id : (int?)null)
            .OfType<int>()
            .ToArray();
        return new CollectionsCaller(employeeId, roles, departments);
    }
}
