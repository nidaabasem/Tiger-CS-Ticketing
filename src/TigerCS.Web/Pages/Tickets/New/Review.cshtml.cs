using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets.New;

/// <summary>
/// Step 4 — classify and submit.
///
/// <para>
/// Two paths, decided by whether verification succeeded, never by anything the
/// browser sends:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Verified</b> — a confirmed session exists, so <c>POST /api/tickets</c>
/// creates the ticket and consumes the session.
/// </item>
/// <item>
/// <b>CRM unavailable</b> — ISSUE-006's fallback. <c>POST /api/tickets/provisional</c>
/// answers <c>201</c> with a real ticket for Critical/High, and <c>200</c>
/// with the updated intake record for Medium/Low. The second is a success and
/// <b>not</b> a ticket: the interaction stays queued for CRM reconciliation,
/// and this page says so explicitly rather than showing a confirmation that
/// implies a ticket exists.
/// </item>
/// </list>
/// </summary>
public sealed class ReviewModel(
    TicketsApiClient ticketsApiClient,
    WizardStateStore stateStore) : PageModel
{
    [BindProperty]
    public int CategoryId { get; set; }

    [BindProperty]
    public byte PriorityId { get; set; }

    [BindProperty]
    public string RequestSummary { get; set; } = string.Empty;

    [BindProperty]
    public string SubmitToken { get; set; } = string.Empty;

    public WizardState? State { get; private set; }

    public ApiProblem? Problem { get; private set; }

    public Dictionary<string, string> FieldErrors { get; } = [];

    /// <summary>Set when a ticket was created. The wizard is finished at that point.</summary>
    public TicketResponseDto? CreatedTicket { get; private set; }

    /// <summary>Set on the Medium/Low CRM-outage outcome, where no ticket was created.</summary>
    public IntakeRecordResponseDto? QueuedIntakeRecord { get; private set; }

    public bool IsFallbackPath => State?.CrmUnavailable == true;

    /// <summary>Maximum request-summary length, matching the approved design's limit.</summary>
    public const int SummaryMaxLength = 2000;

    public IActionResult OnGet()
    {
        State = stateStore.Read(HttpContext);

        if (State is null)
        {
            return RedirectToPage("/Tickets/New/Intake");
        }

        // A wizard that is neither verified nor on the outage path has not
        // finished step 3.
        if (State.VerificationSessionId is null && !State.CrmUnavailable)
        {
            return RedirectToPage("/Tickets/New/Unit");
        }

        PriorityId = State.PriorityHint ?? 3;

        // Minted per render and cleared on acceptance, so a resubmit of this
        // exact form cannot create a second ticket.
        SubmitToken = Guid.NewGuid().ToString("N");
        State = State with { SubmitToken = SubmitToken };
        stateStore.Write(HttpContext, State);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        State = stateStore.Read(HttpContext);

        if (State is null)
        {
            // Already submitted (the state is cleared on success), or the
            // wizard expired. Either way there is nothing to submit, and
            // creating a ticket from a stale form is the one outcome to avoid.
            return RedirectToPage("/Tickets/Index");
        }

        if (string.IsNullOrEmpty(SubmitToken)
            || !string.Equals(SubmitToken, State.SubmitToken, StringComparison.Ordinal))
        {
            // A duplicate submission: the token was consumed by the first one.
            return RedirectToPage("/Tickets/Index");
        }

        Validate();

        if (FieldErrors.Count > 0)
        {
            return Page();
        }

        return IsFallbackPath
            ? await SubmitProvisionalAsync(cancellationToken)
            : await SubmitVerifiedAsync(cancellationToken);
    }

    private void Validate()
    {
        if (CategoryId <= 0)
        {
            FieldErrors[nameof(CategoryId)] = "Enter the category id for this request.";
        }

        if (PriorityId is < 1 or > 4)
        {
            FieldErrors[nameof(PriorityId)] = "Choose a priority.";
        }

        if (string.IsNullOrWhiteSpace(RequestSummary))
        {
            FieldErrors[nameof(RequestSummary)] = "Summarise the caller's request.";
        }
        else if (RequestSummary.Length > SummaryMaxLength)
        {
            FieldErrors[nameof(RequestSummary)] =
                $"Keep the summary to {SummaryMaxLength} characters or fewer.";
        }
    }

    private async Task<IActionResult> SubmitVerifiedAsync(CancellationToken cancellationToken)
    {
        var request = new CreateTicketFromVerificationRequestDto(
            State!.IntakeRecordId,
            State.VerificationSessionId!.Value,
            CategoryId,
            PriorityId,
            RequestSummary.Trim());

        var result = await ticketsApiClient.CreateAsync(request, cancellationToken);

        if (result.IsSuccess)
        {
            CreatedTicket = result.Required;
            stateStore.Clear(HttpContext);
            return Page();
        }

        return HandleFailure(result.Problem!);
    }

    private async Task<IActionResult> SubmitProvisionalAsync(CancellationToken cancellationToken)
    {
        var request = new CreateProvisionalTicketRequestDto(
            State!.IntakeRecordId, CategoryId, PriorityId, RequestSummary.Trim());

        var result = await ticketsApiClient.CreateProvisionalAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            return HandleFailure(result.Problem!);
        }

        var outcome = result.Required;

        if (outcome.IsFirst)
        {
            // 201 — Critical/High proceeded as a provisional ticket.
            CreatedTicket = outcome.First;
        }
        else
        {
            // 200 — Medium/Low. No ticket exists; the interaction remains an
            // intake record awaiting CRM reconciliation.
            QueuedIntakeRecord = outcome.Second;
        }

        stateStore.Clear(HttpContext);
        return Page();
    }

    private IActionResult HandleFailure(ApiProblem problem)
    {
        if (problem.StatusCode == HttpStatusCode.Unauthorized)
        {
            return RedirectToPage("/Account/Login", new { reason = "expired" });
        }

        Problem = problem;

        // 404 category-not-found is the failure this form can actually cause,
        // so it is surfaced on the field rather than only as a banner.
        if (problem.ProblemCode == "category-not-found")
        {
            FieldErrors[nameof(CategoryId)] = "No category with that id. Check it and try again.";
        }

        if (problem.ProblemCode == "priority-not-found")
        {
            FieldErrors[nameof(PriorityId)] = "That priority is not recognised.";
        }

        // A new token, so a corrected resubmission is accepted while a blind
        // browser retry of the old one is still refused.
        SubmitToken = Guid.NewGuid().ToString("N");
        State = State! with { SubmitToken = SubmitToken };
        stateStore.Write(HttpContext, State);

        return Page();
    }

    /// <summary>
    /// Whether the failure means the verification is no longer usable, in which
    /// case the only way forward is to verify again rather than retry.
    /// </summary>
    public bool RequiresReverification =>
        Problem?.ProblemCode is "verification-session-expired"
            or "verification-session-already-consumed"
            or "verification-session-not-confirmed"
            or "verification-session-not-found";
}
