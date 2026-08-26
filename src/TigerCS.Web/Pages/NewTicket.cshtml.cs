using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// "+ New Ticket": Intake → customer lookup (CRM/PACT/Tasleeh) → ticket
/// creation, wired to the real endpoints (POST /api/intake-records,
/// GET /api/intake-records/{id}/customer-lookup, GET /api/departments,
/// POST /api/tickets).
///
/// <para>
/// <b>Business-rule change: customer lookup no longer gates ticket
/// creation.</b> Every intake goes straight from lookup to category
/// selection; the only thing every ticket requires is a valid Ticket
/// Category. The old CRM-unit-search → verification-session-confirm
/// sequence (which blocked creation on a confirmed session) is replaced by
/// a single read-only lookup step: the agent may carry a Found customer
/// match's unit/contact reference forward, or simply continue without one.
/// </para>
///
/// <para>
/// <b>The Unit is selected from customer-lookup results, never typed in.</b>
/// Step 1 no longer asks the agent to identify a unit before lookup even
/// runs — the current lookup model already returns each matched Customer's
/// own 0..N Units, so the actual Ticket Unit (<see cref="UnitReferenceId"/>/
/// <see cref="ContactReferenceId"/>) is always one the agent picked from
/// those real, resolved results on Step 2 (<see cref="OnPostUseMatch"/>),
/// never a raw unit number the caller happened to say over the phone.
/// </para>
///
/// State is carried step-to-step via the query string (GET) and hidden
/// form fields (POST) rather than server-side session/TempData — every
/// step is a plain, bookmarkable, refresh-safe request with no session
/// state to lose, consistent with the rest of the app's no-JS-required
/// progressive-enhancement forms. <see cref="CustomerDisplayName"/> and
/// <see cref="UnitLabel"/> are carried the same way — display-only text for
/// Step 3's summary, never trusted as anything but a label (ticket creation
/// always re-validates <see cref="UnitReferenceId"/>/<see cref="ContactReferenceId"/>
/// server-side).
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CustomerLookupApiClient customerLookupClient,
    DepartmentsApiClient departmentsClient,
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

    /// <summary>Display-only — the selected Customer's name, carried forward from Step 2 for Step 3's summary. Never sent to any API; ticket creation relies only on the reference ids.</summary>
    public string? CustomerDisplayName { get; private set; }

    /// <summary>Display-only — the selected Unit's label (property/unit/status), carried forward from Step 2 for Step 3's summary. Never sent to any API.</summary>
    public string? UnitLabel { get; private set; }

    public CustomerLookupResultDto? LookupResult { get; private set; }

    /// <summary>
    /// The Department directory Step 1's dropdown renders — real, existing
    /// Departments only, never a hard-coded or manually-typed id. Populated
    /// on Step 1 only (<see cref="LoadDepartmentsAsync"/>).
    /// </summary>
    public IReadOnlyCollection<DepartmentDto> Departments { get; private set; } = [];

    /// <summary>Set only when the Departments API call itself failed — the dropdown still renders (empty), the page just says so.</summary>
    public string? DepartmentsErrorMessage { get; private set; }

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
        int? unitReferenceId, int? contactReferenceId, string? customerDisplayName, string? unitLabel,
        CancellationToken cancellationToken)
    {
        Step = step ?? "intake";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        CustomerDisplayName = customerDisplayName;
        UnitLabel = unitLabel;

        if (Step == "lookup" && IntakeRecordId is { } id)
        {
            var result = await customerLookupClient.SearchAsync(id, cancellationToken);
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not search CRM/PACT/Tasleeh for this phone number.");
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
        else
        {
            await LoadDepartmentsAsync(cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        // The wizard never collects a caller-provided unit number or an
        // Intake-specific priority hint (the real Ticket Unit comes from
        // customer lookup on Step 2; the real Ticket Priority is chosen once,
        // on Step 3) — every intake this page creates is recorded as
        // not-unit-related, with no raw unit number and no priority hint.
        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.PhoneNumber, Intake.DepartmentId, IsUnitRelated: false,
            RawUnitNumberEntered: null, PriorityHint: null);

        var result = await intakeClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not record this interaction.");
            Step = "intake";
            await LoadDepartmentsAsync(cancellationToken);
            return Page();
        }

        // Business-rule change: customer lookup runs for every intake — none
        // of CRM/PACT/Tasleeh ever gates what happens next. The phone number
        // and Department carried forward from here on are exactly what the
        // Api just echoed back on the saved IntakeRecord — the phone number
        // never reformatted, the Department (if any) is what will scope both
        // the customer lookup already ran under and the Category dropdown on
        // Step 3.
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
    /// <paramref name="selectedUnitRef"/> is a single
    /// "{unitReferenceId}:{contactReferenceId}:{escaped display label}" value
    /// (a plain HTML radio button can only carry one value per option, and a
    /// customer's unit list is rendered without JavaScript — see
    /// NewTicket.cshtml) so the ids and the label the agent saw always travel
    /// together and can never be mismatched from two separate same-named
    /// radio groups. The label is display-only, for Step 3's summary — never
    /// used for anything but text on screen.
    /// </summary>
    public IActionResult OnPostUseMatch(
        long intakeRecordId, string? phoneNumber, int? departmentId, string selectedUnitRef, string? customerDisplayName)
    {
        var parts = selectedUnitRef.Split(':', 3);
        var unitReferenceId = int.Parse(parts[0]);
        var contactReferenceId = int.Parse(parts[1]);
        var unitLabel = parts.Length > 2 ? Uri.UnescapeDataString(parts[2]) : null;

        return RedirectToPage(new
        {
            step = "create",
            intakeRecordId,
            phoneNumber,
            departmentId,
            unitReferenceId,
            contactReferenceId,
            customerDisplayName,
            unitLabel
        });
    }

    /// <summary>
    /// No Unit selected — either the agent chose "Use this customer" for a
    /// match with no eligible units (<paramref name="customerDisplayName"/>
    /// carries that customer's name forward), found nothing, a source
    /// failed, or the agent chose to proceed without any match at all. None
    /// of those blocks ticket creation.
    /// </summary>
    public IActionResult OnPostContinueWithoutMatch(
        long intakeRecordId, string? phoneNumber, int? departmentId, string? customerDisplayName = null) =>
        RedirectToPage(new { step = "create", intakeRecordId, phoneNumber, departmentId, customerDisplayName });

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, string? phoneNumber, int? departmentId, int? unitReferenceId, int? contactReferenceId,
        string? customerDisplayName, string? unitLabel, CancellationToken cancellationToken)
    {
        Step = "create";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        CustomerDisplayName = customerDisplayName;
        UnitLabel = unitLabel;

        // The dropdowns are the only way to supply a CategoryId/PriorityId —
        // never manually typed in — but the request is still rejected here
        // (rather than trusted) if somehow neither was selected. POST
        // /api/tickets is the actual authority: it re-validates the category
        // exists, is active, and (item below) matches the IntakeRecord's own
        // Department.
        if (CreateStep.CategoryId is not { } categoryId || CreateStep.PriorityId is not { } priorityId)
        {
            ErrorMessage = CreateStep.CategoryId is null
                ? "Select a category before creating the ticket."
                : "Select a priority before creating the ticket.";
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        var request = new CreateTicketRequestDto(
            intakeRecordId, unitReferenceId, contactReferenceId, categoryId, priorityId, CreateStep.RequestSummary);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not create the ticket.");
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage("/TicketDetails", new { id = result.Value.TicketId });
    }

    /// <summary>
    /// The Department directory for Step 1's dropdown. Failure and "loaded
    /// but empty" are kept distinct (<see cref="DepartmentsErrorMessage"/> vs.
    /// an empty <see cref="Departments"/>), and neither ever falls back to a
    /// manual id field — an unavailable directory just means an agent can't
    /// narrow the lookup by Department for this request, not that they type
    /// one in instead.
    /// </summary>
    private async Task LoadDepartmentsAsync(CancellationToken cancellationToken)
    {
        var result = await departmentsClient.GetDepartmentsAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            DepartmentsErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Unable to load the department list. Please try again.");
            Departments = [];
        }
        else
        {
            Departments = result.Value;
        }
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
            CategoriesErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Unable to load ticket categories. Please try again.");
            Categories = [];
        }
        else
        {
            Categories = result.Value;
        }
    }

    /// <summary>
    /// A user-facing fallback for a failed Api call whose response carried no
    /// safe "detail"/"title" text (e.g. an empty-bodied 401/403, or a
    /// connection failure) — distinct, actionable wording for the outcomes an
    /// agent can actually act on; every other outcome keeps the caller's own
    /// page-specific fallback text.
    /// </summary>
    private static string DescribeFailure(ApiOutcome outcome, string fallback) => outcome switch
    {
        ApiOutcome.Unauthorized or ApiOutcome.Forbidden => "Your session is not authorized to perform this action.",
        ApiOutcome.Unreachable => "Unable to contact the Ticketing API. Please try again shortly.",
        _ => fallback
    };

    public sealed class IntakeInput
    {
        [Required]
        public string ChannelId { get; set; } = "Phone";
        [Required]
        public string PhoneNumber { get; set; } = string.Empty;
        /// <summary>
        /// Optional — the real DepartmentId of a Step 1 dropdown selection,
        /// never typed in by hand. When set, narrows customer lookup to this
        /// Department's configured source(s) instead of searching
        /// CRM+PACT+Tasleeh, and later scopes the Category dropdown to this
        /// Department only.
        /// </summary>
        public int? DepartmentId { get; set; }
    }

    public sealed class CreateStepInput
    {
        /// <summary>The real CategoryId of a dropdown selection — never typed in by hand. Nullable so "nothing selected" is a distinct, validatable state rather than a fake id like 0.</summary>
        [Required(ErrorMessage = "Select a category.")]
        public int? CategoryId { get; set; }
        /// <summary>1=Critical, 2=High, 3=Medium, 4=Low — a dropdown selection, never typed in by hand. Nullable so "nothing selected" is distinct from a real, meaningful priority value.</summary>
        [Required(ErrorMessage = "Select a priority.")]
        public byte? PriorityId { get; set; }
        [Required]
        public string RequestSummary { get; set; } = string.Empty;
    }
}
