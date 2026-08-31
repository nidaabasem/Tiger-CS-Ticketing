using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// "+ New Ticket": Intake → real CRM Buyer Lookup → ticket creation, wired
/// to the real endpoints (POST /api/intake-records,
/// GET /api/crm/buyers?phoneNumber={phoneNumber}, GET /api/departments,
/// POST /api/tickets).
///
/// <para>
/// <b>Business-rule change: this wizard's phone search calls the real CRM
/// Buyer Lookup only.</b> Step 2 calls <see cref="CrmBuyerLookupApiClient"/> —
/// <c>CrmController</c> → <c>CrmBuyerLookupAppService</c> →
/// <c>CrmBuyerHttpGateway</c> → the legacy CRM's own <c>GetBuyerByPhone</c> —
/// by phone number only. It never calls the generic CRM/PACT/Tasleeh
/// <c>CustomerLookupApiClient</c>/<c>CustomerLookupController</c> path (still
/// used elsewhere, not by this page), and it never searches CRM by Unit
/// Number or Project. <c>Crm:SecretKey</c> stays server-to-server inside
/// <c>CrmBuyerHttpGateway</c> — this page, and the browser, never see it.
/// </para>
///
/// <para>
/// <b>Customer lookup no longer gates ticket creation.</b> A CRM match
/// (Found), no match (NotFound), and a CRM outage (Unavailable) are all
/// treated the same way for ticket creation: none of them block it. Found
/// means the agent explicitly selects one Buyer's one eligible unit — never
/// auto-selected. NotFound/Unavailable both require the agent to manually
/// enter Project and Unit Number on Step 3 instead, and neither of those
/// manual fields is ever used to run another CRM lookup.
/// </para>
///
/// State is carried step-to-step via the query string (GET) and hidden
/// form fields (POST) rather than server-side session/TempData — every step
/// is a plain, bookmarkable, refresh-safe request with no session state to
/// lose, consistent with the rest of the app's no-JS-required
/// progressive-enhancement forms. The CRM Buyer identifiers/snapshot text
/// carried this way are display/carry-forward only — ticket creation
/// (<c>POST /api/tickets</c>) re-validates the CRM Buyer id 4-tuple and the
/// manual-fields-required-when-no-match rule server-side regardless of what
/// this page sends.
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CrmBuyerLookupApiClient crmBuyerLookupClient,
    DepartmentsApiClient departmentsClient,
    CategoriesApiClient categoriesClient,
    TicketsApiClient ticketsClient,
    CustomerHistoryApiClient customerHistoryClient) : PageModel
{
    public string Step { get; private set; } = "intake";
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public string? PhoneNumber { get; private set; }
    public int? DepartmentId { get; private set; }

    /// <summary>The real CRM Buyer Lookup match the agent selected on Step 2 — all four set together, or none. A distinct identifier space from the older CRM-unit-number cache (UnitReferenceId/ContactReferenceId).</summary>
    public int? CrmBuyerCustomerId { get; private set; }
    public int? CrmBuyerLeadId { get; private set; }
    public int? CrmBuyerUnitId { get; private set; }
    public int? CrmBuyerProjectId { get; private set; }

    /// <summary>Display-only snapshot text carried forward from Step 2 for Step 3's summary. Never trusted as anything but a label — ticket creation re-validates the CRM Buyer ids server-side.</summary>
    public string? CrmBuyerCustomerName { get; private set; }
    public string? CrmBuyerProjectName { get; private set; }
    public string? CrmBuyerUnitNumber { get; private set; }

    /// <summary>Every Buyer <c>GET /api/crm/buyers?phoneNumber=</c> matched (0..N), each with 0..N eligible Sold/Contract units — never auto-selected. Null until Step 2 has actually run a lookup.</summary>
    public IReadOnlyList<CrmBuyerMatchDto>? CrmBuyerMatches { get; private set; }

    /// <summary>True when CRM Buyer Lookup itself could not be reached/answered (outage, timeout, misconfiguration) rather than answering with zero matches — same "Project/Unit Number required" consequence as NotFound, but a different message.</summary>
    public bool CrmBuyerLookupUnavailable { get; private set; }

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

    /// <summary>
    /// Step 3's compact "Previous Tickets" preview — the exact CRM Buyer
    /// customer the agent just selected on Step 2, never the first raw phone
    /// search result. Null when no CRM Buyer match was selected (nothing
    /// verified to key history on yet) or when the lookup itself failed;
    /// populated only on Step 3 (<see cref="LoadPreviousTicketsAsync"/>).
    /// Sourced entirely from the Tickets table — this call never touches
    /// CRM.
    /// </summary>
    public CustomerHistoryDto? PreviousTickets { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, string? phoneNumber, int? departmentId,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        CancellationToken cancellationToken)
    {
        Step = step ?? "intake";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
        CrmBuyerCustomerId = crmBuyerCustomerId;
        CrmBuyerLeadId = crmBuyerLeadId;
        CrmBuyerUnitId = crmBuyerUnitId;
        CrmBuyerProjectId = crmBuyerProjectId;
        CrmBuyerCustomerName = crmBuyerCustomerName;
        CrmBuyerProjectName = crmBuyerProjectName;
        CrmBuyerUnitNumber = crmBuyerUnitNumber;

        if (Step == "lookup" && !string.IsNullOrWhiteSpace(PhoneNumber))
        {
            await RunCrmBuyerLookupAsync(cancellationToken);
        }
        else if (Step == "create")
        {
            await LoadCategoriesAsync(cancellationToken);
            await LoadPreviousTicketsAsync(cancellationToken);
        }
        else
        {
            await LoadDepartmentsAsync(cancellationToken);
        }

        return Page();
    }

    /// <summary>
    /// The one and only CRM search this wizard ever runs — phone number
    /// only, never Unit Number/Project/Tower. Found (200), NotFound (404),
    /// and every other outcome (401/400/502/network-unreachable — CRM
    /// outage or misconfiguration) are all handled here without blocking the
    /// wizard: only <see cref="CrmBuyerLookupUnavailable"/> distinguishes the
    /// message shown, not what the agent is allowed to do next.
    /// </summary>
    private async Task RunCrmBuyerLookupAsync(CancellationToken cancellationToken)
    {
        var result = await crmBuyerLookupClient.SearchByPhoneAsync(PhoneNumber!, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            CrmBuyerMatches = result.Value;
        }
        else if (result.Outcome == ApiOutcome.NotFound)
        {
            CrmBuyerMatches = [];
        }
        else
        {
            CrmBuyerLookupUnavailable = true;
            CrmBuyerMatches = [];
        }
    }

    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        // The wizard never collects a caller-provided unit number or an
        // Intake-specific priority hint (the real Ticket Unit comes from CRM
        // Buyer Lookup on Step 2; the real Ticket Priority is chosen once, on
        // Step 3) — every intake this page creates is recorded as
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

        // Business-rule change: CRM Buyer Lookup runs for every intake — it
        // never gates what happens next. The phone number and Department
        // carried forward from here on are exactly what the Api just echoed
        // back on the saved IntakeRecord.
        return RedirectToPage(new
        {
            step = "lookup",
            intakeRecordId = result.Value.IntakeRecordId,
            phoneNumber = result.Value.PhoneNumber,
            departmentId = result.Value.DepartmentId
        });
    }

    /// <summary>
    /// The agent explicitly selected one Buyer's one eligible unit from the
    /// real CRM Buyer Lookup results — its CRM identifiers carry forward to
    /// ticket creation. <paramref name="selectedCrmBuyerUnit"/> packs
    /// "{customerId}:{leadId}:{unitId}:{projectId}:{escaped customer
    /// name}:{escaped project name}:{escaped unit number}" into one value (a
    /// plain HTML radio button can only carry one value per option, and the
    /// unit list is rendered without JavaScript — see NewTicket.cshtml) so
    /// the ids and the display text the agent saw always travel together and
    /// can never be mismatched from two separate same-named radio groups.
    /// Every text field is <see cref="Uri.EscapeDataString(string)"/>-encoded
    /// before packing (encoding a literal ':' as %3A), so splitting on ':' is
    /// always safe.
    /// </summary>
    public IActionResult OnPostUseCrmBuyerUnit(
        long intakeRecordId, string? phoneNumber, int? departmentId, string selectedCrmBuyerUnit)
    {
        var parts = selectedCrmBuyerUnit.Split(':', 7);
        var customerId = int.Parse(parts[0]);
        var leadId = int.Parse(parts[1]);
        var unitId = int.Parse(parts[2]);
        var projectId = int.Parse(parts[3]);
        var customerName = UnescapeOrNull(parts, 4);
        var projectName = UnescapeOrNull(parts, 5);
        var unitNumber = UnescapeOrNull(parts, 6);

        return RedirectToPage(new
        {
            step = "create",
            intakeRecordId,
            phoneNumber,
            departmentId,
            crmBuyerCustomerId = customerId,
            crmBuyerLeadId = leadId,
            crmBuyerUnitId = unitId,
            crmBuyerProjectId = projectId,
            crmBuyerCustomerName = customerName,
            crmBuyerProjectName = projectName,
            crmBuyerUnitNumber = unitNumber
        });
    }

    private static string? UnescapeOrNull(string[] parts, int index)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return null;
        }

        return Uri.UnescapeDataString(parts[index]);
    }

    /// <summary>
    /// No CRM Buyer unit selected — CRM found no match, CRM was unavailable,
    /// or the agent chose to proceed without using a match CRM did find.
    /// None of those blocks ticket creation; Step 3 requires the agent to
    /// manually enter Project and Unit Number instead.
    /// </summary>
    public IActionResult OnPostContinueWithoutMatch(long intakeRecordId, string? phoneNumber, int? departmentId) =>
        RedirectToPage(new { step = "create", intakeRecordId, phoneNumber, departmentId });

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, string? phoneNumber, int? departmentId,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        CancellationToken cancellationToken)
    {
        Step = "create";
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        DepartmentId = departmentId;
        CrmBuyerCustomerId = crmBuyerCustomerId;
        CrmBuyerLeadId = crmBuyerLeadId;
        CrmBuyerUnitId = crmBuyerUnitId;
        CrmBuyerProjectId = crmBuyerProjectId;
        CrmBuyerCustomerName = crmBuyerCustomerName;
        CrmBuyerProjectName = crmBuyerProjectName;
        CrmBuyerUnitNumber = crmBuyerUnitNumber;

        var hasCrmBuyerMatch = CrmBuyerUnitId is not null;

        // Business-rule change: no verified CRM unit selected — CRM Buyer
        // Lookup found no match for this phone number, or CRM was
        // unavailable. Project and Unit Number are then both required,
        // manually entered by the agent, and never used to run another CRM
        // lookup (CRM is searched by phone number only — see
        // RunCrmBuyerLookupAsync). POST /api/tickets is the actual
        // authority and re-validates this same rule server-side.
        if (!hasCrmBuyerMatch
            && (string.IsNullOrWhiteSpace(CreateStep.ManualProjectName) || string.IsNullOrWhiteSpace(CreateStep.ManualUnitNumber)))
        {
            ErrorMessage = "Customer not found in CRM. Project and Unit Number are required.";
            await LoadCategoriesAsync(cancellationToken);
            await LoadPreviousTicketsAsync(cancellationToken);
            return Page();
        }

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
            await LoadPreviousTicketsAsync(cancellationToken);
            return Page();
        }

        var request = new CreateTicketRequestDto(
            IntakeRecordId: intakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            CategoryId: categoryId,
            PriorityId: priorityId,
            RequestSummary: CreateStep.RequestSummary,
            CrmBuyerCustomerId: hasCrmBuyerMatch ? crmBuyerCustomerId : null,
            CrmBuyerLeadId: hasCrmBuyerMatch ? crmBuyerLeadId : null,
            CrmBuyerUnitId: hasCrmBuyerMatch ? crmBuyerUnitId : null,
            CrmBuyerProjectId: hasCrmBuyerMatch ? crmBuyerProjectId : null,
            CrmBuyerCustomerName: hasCrmBuyerMatch ? crmBuyerCustomerName : null,
            CrmBuyerProjectName: hasCrmBuyerMatch ? crmBuyerProjectName : null,
            CrmBuyerUnitNumber: hasCrmBuyerMatch ? crmBuyerUnitNumber : null,
            ManualProjectName: hasCrmBuyerMatch ? null : CreateStep.ManualProjectName,
            ManualUnitNumber: hasCrmBuyerMatch ? null : CreateStep.ManualUnitNumber);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not create the ticket.");
            await LoadCategoriesAsync(cancellationToken);
            await LoadPreviousTicketsAsync(cancellationToken);
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
    /// Step 3's "Previous Tickets" preview — always the CRM Buyer customer
    /// the agent explicitly selected on Step 2 (<see cref="CrmBuyerCustomerId"/>),
    /// never re-derived from the raw phone search results. No CRM Buyer
    /// match selected (CRM not found, unavailable, or the agent proceeded
    /// without one) means there is no verified identity yet to show history
    /// for, so this is skipped entirely rather than falling back to a phone
    /// number — that fallback is Ticket Details' job, once a ticket (and its
    /// IntakeRecord link) actually exists. A failed call here never blocks
    /// the wizard; it just leaves <see cref="PreviousTickets"/> null and the
    /// page shows no preview section.
    /// </summary>
    private async Task LoadPreviousTicketsAsync(CancellationToken cancellationToken)
    {
        if (CrmBuyerCustomerId is not { } crmBuyerCustomerId)
        {
            return;
        }

        var result = await customerHistoryClient.GetByCrmCustomerIdAsync(crmBuyerCustomerId, limit: 5, cancellationToken);
        PreviousTickets = result.IsSuccess ? result.Value : null;
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
        /// never typed in by hand. Narrows the Category dropdown on Step 3 to
        /// this Department only; CRM Buyer Lookup itself is never scoped by
        /// Department — it always searches by phone number only.
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

        /// <summary>
        /// Required, together with <see cref="ManualUnitNumber"/>, only when
        /// no CRM Buyer unit was selected on Step 2 (business-rule change) —
        /// validated in <c>OnPostCreateAsync</c> rather than
        /// <see cref="RequiredAttribute"/> because the requirement is
        /// conditional, not universal. Never used to run another CRM lookup.
        /// </summary>
        public string? ManualProjectName { get; set; }

        /// <summary>Required together with <see cref="ManualProjectName"/> — see that property.</summary>
        public string? ManualUnitNumber { get; set; }
    }
}
