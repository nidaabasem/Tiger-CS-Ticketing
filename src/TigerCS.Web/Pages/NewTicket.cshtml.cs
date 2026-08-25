using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
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
    CategoriesApiClient categoriesClient,
    TicketsApiClient ticketsClient) : PageModel
{
    public string Step { get; private set; } = "intake";
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public string? PhoneNumber { get; private set; }
    public int? DepartmentId { get; private set; }
    public int? UnitReferenceId { get; private set; }
    public int? ContactReferenceId { get; private set; }
    public CustomerLookupResultDto? LookupResult { get; private set; }

    /// <summary>
    /// The active Categories the Category dropdown offers — scoped to
    /// <see cref="DepartmentId"/> when set, otherwise every active Category.
    /// Populated on Step 3 only (<see cref="LoadCategoriesAsync"/>).
    /// </summary>
    public IReadOnlyCollection<CategoryDto> Categories { get; private set; } = [];

    /// <summary>Set only when the Categories API call itself failed — distinct from "loaded successfully but empty".</summary>
    public string? CategoriesErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, string? phoneNumber, int? departmentId,
        int? unitReferenceId, int? contactReferenceId, CancellationToken cancellationToken)
    {
        Step = step ?? "intake";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
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
        else if (Step == "create")
        {
            await LoadCategoriesAsync(cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.PhoneNumber, Intake.DepartmentId, Intake.IsUnitRelated,
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
        // The phone number and Department carried forward from here on are
        // exactly what the Api just echoed back on the saved IntakeRecord —
        // the phone number never reformatted, the Department (if any) is what
        // will scope both the customer lookup already ran under and the
        // Category dropdown on Step 3.
        return RedirectToPage(new
        {
            step = "lookup",
            intakeRecordId = result.Value.IntakeRecordId,
            phoneNumber = result.Value.PhoneNumber,
            departmentId = result.Value.DepartmentId
        });
    }

    /// <summary>
    /// The agent selected one customer's unit from a customer-lookup match —
    /// its unit/contact reference carries forward to ticket creation.
    /// <paramref name="selectedUnitRef"/> is a single "{unitReferenceId}:{contactReferenceId}"
    /// value (a plain HTML radio button can only carry one value per option,
    /// and a customer's unit list is rendered without JavaScript — see
    /// NewTicket.cshtml) so the two ids for whichever unit the agent picked
    /// always travel together and can never be mismatched from two separate
    /// same-named radio groups.
    /// </summary>
    public IActionResult OnPostUseMatch(long intakeRecordId, string? phoneNumber, int? departmentId, string selectedUnitRef)
    {
        var parts = selectedUnitRef.Split(':');
        var unitReferenceId = int.Parse(parts[0]);
        var contactReferenceId = int.Parse(parts[1]);
        return RedirectToPage(new { step = "create", intakeRecordId, phoneNumber, departmentId, unitReferenceId, contactReferenceId });
    }

    /// <summary>No match selected — found nothing, a source failed, or the agent chose to proceed anyway. None of those blocks ticket creation.</summary>
    public IActionResult OnPostContinueWithoutMatch(long intakeRecordId, string? phoneNumber, int? departmentId) =>
        RedirectToPage(new { step = "create", intakeRecordId, phoneNumber, departmentId });

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, string? phoneNumber, int? departmentId, int? unitReferenceId, int? contactReferenceId, CancellationToken cancellationToken)
    {
        Step = "create";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;

        // The dropdown is the only way to supply a CategoryId, and it only
        // ever offers real, active, correctly-scoped options — but the
        // request is still rejected here (rather than trusted) if somehow
        // none was selected. POST /api/tickets is the actual authority: it
        // re-validates the category exists, is active, and (item below)
        // matches the IntakeRecord's own Department.
        if (CreateStep.CategoryId is not { } categoryId)
        {
            ErrorMessage = "Select a category before creating the ticket.";
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        var request = new CreateTicketRequestDto(
            intakeRecordId, unitReferenceId, contactReferenceId, categoryId, CreateStep.PriorityId, CreateStep.RequestSummary);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not create the ticket.";
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage("/TicketDetails", new { id = result.Value.TicketId });
    }

    /// <summary>
    /// Active Categories for Step 3's dropdown, scoped to
    /// <see cref="DepartmentId"/> when the Intake named one. Failure and
    /// "loaded but empty" are kept distinct (<see cref="CategoriesErrorMessage"/>
    /// vs. an empty <see cref="Categories"/>) since the view shows a
    /// different message, and neither ever falls back to a manual id field.
    /// </summary>
    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        var result = await categoriesClient.GetCategoriesAsync(DepartmentId, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            CategoriesErrorMessage = result.Detail ?? "Unable to load ticket categories. Please try again.";
            Categories = [];
        }
        else
        {
            Categories = result.Value;
        }
    }

    public sealed class IntakeInput
    {
        [Required]
        public string ChannelId { get; set; } = "Phone";
        [Required]
        public string PhoneNumber { get; set; } = string.Empty;
        /// <summary>Optional — when set, narrows customer lookup to this Department's configured source(s) instead of searching CRM+PACT+Tasleeh, and later scopes the Category dropdown to this Department only.</summary>
        public int? DepartmentId { get; set; }
        public bool IsUnitRelated { get; set; } = true;
        public string? RawUnitNumberEntered { get; set; }
        public byte? PriorityHint { get; set; }
    }

    public sealed class CreateStepInput
    {
        /// <summary>The real CategoryId of a dropdown selection — never typed in by hand. Nullable so "nothing selected" is a distinct, validatable state rather than a fake id like 0.</summary>
        [Required(ErrorMessage = "Select a category.")]
        public int? CategoryId { get; set; }
        public byte PriorityId { get; set; } = 3;
        [Required]
        public string RequestSummary { get; set; } = string.Empty;
    }
}
