using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// "+ New Ticket": Intake → customer lookup (CRM/PACT/Tasleeh) → ticket
/// creation, wired to the real endpoints (POST /api/intake-records,
/// GET /api/intake-records/{id}/customer-lookup, POST /api/tickets).
///
/// <para>
/// <b>Business-rule change: customer lookup no longer gates ticket
/// creation.</b> Every intake — unit-related or not, a match found or not —
/// goes straight from lookup to category selection; the only thing every
/// ticket requires is a valid Ticket Category. The old CRM-unit-search →
/// verification-session-confirm sequence (which blocked creation on a
/// confirmed session) is replaced by a single read-only lookup step: the
/// agent may carry a Found CRM match's unit/contact reference forward, or
/// simply continue without one.
/// </para>
///
/// State is carried step-to-step via the query string (GET) and hidden
/// form fields (POST) rather than server-side session/TempData — every
/// step is a plain, bookmarkable, refresh-safe request with no session
/// state to lose, consistent with the rest of the app's no-JS-required
/// progressive-enhancement forms.
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CustomerLookupApiClient customerLookupClient,
    TicketsApiClient ticketsClient) : PageModel
{
    public string Step { get; private set; } = "intake";
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public int? UnitReferenceId { get; private set; }
    public int? ContactReferenceId { get; private set; }
    public CustomerLookupResultDto? LookupResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, int? unitReferenceId, int? contactReferenceId, CancellationToken cancellationToken)
    {
        Step = step ?? "intake";
        IntakeRecordId = intakeRecordId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;

        if (Step == "lookup" && IntakeRecordId is { } id)
        {
            var result = await customerLookupClient.SearchAsync(id, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Detail ?? "Could not search CRM/PACT/Tasleeh for this phone number.";
            }
            else
            {
                LookupResult = result.Value;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.PhoneNumber, Intake.IsUnitRelated,
            Intake.IsUnitRelated ? Intake.RawUnitNumberEntered : null, Intake.PriorityHint);

        var result = await intakeClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not record this interaction.";
            Step = "intake";
            return Page();
        }

        // Business-rule change: customer lookup runs for every intake — a
        // non-unit-related interaction may still be a known CRM/PACT/Tasleeh
        // customer, and none of the three ever gates what happens next.
        return RedirectToPage(new { step = "lookup", intakeRecordId = result.Value.IntakeRecordId });
    }

    /// <summary>The agent selected a CRM lookup match — its unit/contact reference carries forward to ticket creation.</summary>
    public IActionResult OnPostUseMatch(long intakeRecordId, int unitReferenceId, int contactReferenceId) =>
        RedirectToPage(new { step = "create", intakeRecordId, unitReferenceId, contactReferenceId });

    /// <summary>No match selected — found nothing, a source failed, or the agent chose to proceed anyway. None of those blocks ticket creation.</summary>
    public IActionResult OnPostContinueWithoutMatch(long intakeRecordId) =>
        RedirectToPage(new { step = "create", intakeRecordId });

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, int? unitReferenceId, int? contactReferenceId, CancellationToken cancellationToken)
    {
        Step = "create";
        IntakeRecordId = intakeRecordId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;

        var request = new CreateTicketRequestDto(
            intakeRecordId, unitReferenceId, contactReferenceId, CreateStep.CategoryId, CreateStep.PriorityId, CreateStep.RequestSummary);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not create the ticket.";
            return Page();
        }

        return RedirectToPage("/TicketDetails", new { id = result.Value.TicketId });
    }

    public sealed class IntakeInput
    {
        [Required]
        public string ChannelId { get; set; } = "Phone";
        [Required]
        public string PhoneNumber { get; set; } = string.Empty;
        public bool IsUnitRelated { get; set; } = true;
        public string? RawUnitNumberEntered { get; set; }
        public byte? PriorityHint { get; set; }
    }

    public sealed class CreateStepInput
    {
        [Required, Range(1, int.MaxValue)]
        public int CategoryId { get; set; }
        public byte PriorityId { get; set; } = 3;
        [Required]
        public string RequestSummary { get; set; } = string.Empty;
    }
}
