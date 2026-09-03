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
/// One customer a verification source matched on Step 1 — a display card,
/// never raw identifiers. <see cref="Key"/> is the round-trippable selection
/// token ("crm", or "ext:{source}:{escaped external id}") carried in the
/// query string; everything the ticket ultimately persists still travels
/// through the packed unit selections, exactly as before the redesign.
/// </summary>
public sealed record NewTicketCandidate(
    string Key, string Source, string? DisplayName, string? PhoneNumber, string? Email, int UnitsCount);

/// <summary>
/// "+ New Ticket" — redesigned as a four-step wizard (Customer → Property →
/// Issue → Review) with a persistent summary panel, wired to the same real
/// endpoints as before (POST /api/intake-records,
/// GET /api/intake-records/{id}/customer-lookup,
/// GET /api/crm/buyers?phoneNumber={phoneNumber}, GET /api/departments,
/// GET /api/categories, the customer-history/related-tickets reads, and
/// POST /api/tickets). The redesign is presentation and flow only: no
/// verification rule, identity rule, or creation rule changed.
///
/// <para>
/// <b>Step 1 (Customer)</b> records the intake (channel + phone — the
/// intake carries no Department in this flow; Department moved to the Issue
/// step, and an intake without a Department searches every configured
/// lookup source, exactly per the existing department-aware rule) and shows
/// each matched customer as a compact card with their recent-ticket
/// awareness. <b>Step 2 (Property)</b> selects the unit — CRM Buyer units
/// and PACT/Tasleeh units keep their packed-selection integrity (ids and
/// display text always travel together), manual entry stays the fallback —
/// and surfaces the existing related-tickets advisory for the selected
/// unit. <b>Step 3 (Issue)</b> chooses Department → Request Type (the
/// existing Category, scoped to the Department) → Priority → the request
/// text. <b>Step 4 (Review)</b> shows a concise summary before the one
/// create action.
/// </para>
///
/// <para>
/// <b>Customer lookup still never gates ticket creation.</b> For every
/// source, Found / NotFound / Failed all leave the agent a path forward —
/// a not-found customer continues through manual entry, and no source's
/// failure ever hides another source's results. A matched PACT/Tasleeh
/// customer IS verified — against that source — and persists exactly as
/// before: the generic external identity plus the human-readable
/// Project/Unit snapshot. The phone number is free-form and travels
/// verbatim end to end (no pattern, no normalization — the one permitted
/// transformation stays inside PactCustomerHttpGateway).
/// </para>
///
/// <para>
/// State is carried step-to-step via the query string (GET) and hidden form
/// fields (POST) rather than server-side session/TempData — every step is a
/// plain, bookmarkable, refresh-safe request. Going Back never loses the
/// selection; changing the customer naturally clears the unit (the unit
/// selection tokens belong to the customer that produced them). Lookup
/// <i>results</i> are re-read on the steps that render them (idempotent
/// reads); the agent's explicit selections are never re-derived. The
/// carried CRM/external identifiers and snapshot text are
/// display/carry-forward only — POST /api/tickets re-validates everything
/// server-side regardless of what this page sends.
/// </para>
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CustomerLookupApiClient customerLookupClient,
    CrmBuyerLookupApiClient crmBuyerLookupClient,
    DepartmentsApiClient departmentsClient,
    CategoriesApiClient categoriesClient,
    TicketsApiClient ticketsClient,
    CustomerHistoryApiClient customerHistoryClient) : PageModel
{
    public const string StepCustomer = "customer";
    public const string StepProperty = "property";
    public const string StepIssue = "issue";
    public const string StepReview = "review";
    public const string StepDone = "done";

    /// <summary>How many recent tickets the Step 1 existing-tickets awareness shows per matched customer — a glance, never the full history (that lives in the Customer Workspace).</summary>
    public const int CandidateHistoryLimit = 3;

    public string Step { get; private set; } = StepCustomer;
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public string? PhoneNumber { get; private set; }

    /// <summary>The Step 1 customer selection token: "crm", "ext:{source}:{escaped id}", or "manual". Null until the agent picks (or falls back to manual entry).</summary>
    public string? CustomerKey { get; private set; }

    /// <summary>The real CRM Buyer Lookup match the agent selected on Step 2 — all four set together, or none. A distinct identifier space from the older CRM-unit-number cache (UnitReferenceId/ContactReferenceId).</summary>
    public int? CrmBuyerCustomerId { get; private set; }
    public int? CrmBuyerLeadId { get; private set; }
    public int? CrmBuyerUnitId { get; private set; }
    public int? CrmBuyerProjectId { get; private set; }

    /// <summary>Display-only snapshot text carried forward from Step 2 for the summary panel and review. Never trusted as anything but a label — ticket creation re-validates the CRM Buyer ids server-side.</summary>
    public string? CrmBuyerCustomerName { get; private set; }
    public string? CrmBuyerProjectName { get; private set; }
    public string? CrmBuyerUnitNumber { get; private set; }

    /// <summary>
    /// The one CRM customer <c>GET /api/crm/buyers?phoneNumber=</c> matched —
    /// business rule: a CRM phone number belongs to exactly one customer, so
    /// this page is built for exactly one, never a list to disambiguate
    /// between. Carries every eligible Sold/Contract unit that customer owns
    /// — never auto-selected. Null until Step 1 has actually run a lookup, or
    /// when it found no match.
    /// </summary>
    public CrmBuyerMatchDto? CrmBuyerMatch { get; private set; }

    /// <summary>
    /// True when CRM answered with more than one distinct customer for this
    /// phone number (409 Conflict) — a CRM data-integrity conflict
    /// (<c>CrmBuyerLookupAppService.GetBuyerByPhoneAsync</c>'s
    /// <c>AmbiguousCustomerMatch</c> outcome), not "no match". No customer or
    /// unit is ever auto-selected for this case; the agent falls back to
    /// manual entry exactly as for no match, but sees a distinct message
    /// naming the conflict.
    /// </summary>
    public bool CrmBuyerAmbiguousMatch { get; private set; }

    /// <summary>True when CRM Buyer Lookup itself could not be reached/answered (outage, timeout, misconfiguration) rather than answering with zero matches — same manual-entry consequence as NotFound, but presented as "temporarily unavailable", never as "not found".</summary>
    public bool CrmBuyerLookupUnavailable { get; private set; }

    /// <summary>
    /// The department-aware customer lookup response — one entry per source
    /// the intake enables (all three for this flow's department-less
    /// intakes), each independently Found/NotFound/Failed so one source's
    /// outage never hides another's result. Null until Step 1 has run, or
    /// when the lookup call itself failed
    /// (<see cref="CustomerLookupUnavailable"/>).
    /// </summary>
    public CustomerLookupResultDto? CustomerLookup { get; private set; }

    /// <summary>True when the generic customer-lookup call itself failed — the page fails open to the previous CRM-only behavior (<see cref="CrmParticipates"/> stays true) rather than losing CRM lookup, and never blocks the wizard.</summary>
    public bool CustomerLookupUnavailable { get; private set; }

    /// <summary>
    /// Whether the Crm source participates for this intake — read from the
    /// department-aware lookup response, never from a hard-coded department
    /// check. Fails open to true when that response is unavailable, so the
    /// real CRM Buyer Lookup never silently disappears on a lookup-config
    /// outage.
    /// </summary>
    public bool CrmParticipates { get; private set; } = true;

    /// <summary>
    /// The non-CRM source entries (PACT/Tasleeh) straight from the
    /// department-aware response. The response's own Crm entry is used only
    /// as the participation signal for the real CRM Buyer Lookup — its
    /// customers are not rendered, because that generic Crm leg is still
    /// fixture-backed (Crm:Provider=Mock) while Buyer Lookup is the real CRM
    /// integration (see this type's remarks).
    /// </summary>
    public IReadOnlyList<CustomerLookupSourceResultDto> ExternalLookupSources =>
        CustomerLookup?.Sources.Where(s => !string.Equals(s.Source, "Crm", StringComparison.Ordinal)).ToList() ?? [];

    /// <summary>True when the Department has zero lookup sources configured — nothing was searched (never a silent fall-back to "search everything"), and the agent continues with manual entry.</summary>
    public bool NoLookupSourcesConfigured => CustomerLookup is { Sources.Count: 0 };

    /// <summary>Every matched customer, as Step 1 cards — the CRM Buyer (at most one) plus each PACT/Tasleeh Found customer. Empty until a search has run, or when nothing matched.</summary>
    public IReadOnlyList<NewTicketCandidate> Candidates { get; private set; } = [];

    /// <summary>
    /// Step 1's existing-ticket awareness, keyed by candidate: each matched
    /// customer's ticket counts and a few most-recent tickets (active first),
    /// from the existing identity-keyed Customer History reads — one bounded
    /// call per matched customer, never per ticket, and never a display-name
    /// or phone match. A candidate whose history call failed simply has no
    /// entry; the wizard never blocks on it.
    /// </summary>
    public IReadOnlyDictionary<string, CustomerHistoryDto> CandidateHistories { get; private set; } =
        new Dictionary<string, CustomerHistoryDto>();

    /// <summary>The Step 1 card the agent selected, re-resolved from the (re-read) lookup results on Step 2. Null on the manual path.</summary>
    public NewTicketCandidate? SelectedCandidate { get; private set; }

    /// <summary>
    /// The PACT/Tasleeh customer+unit the agent selected on Step 2, packed
    /// exactly like the CRM value (see <see cref="OnPostUseExternalUnit"/>)
    /// and carried step-to-step via query string/hidden field like every
    /// other wizard value. Unpacked into the External* properties below.
    /// Display/carry-forward only past the prefill — the persisted record of
    /// the selection is the external identity plus the manual Project/Unit
    /// snapshot (see this type's remarks).
    /// </summary>
    public string? ExternalSelection { get; private set; }

    /// <summary>"Pact" or "Tasleeh" — which source verified the Step 2 selection; persisted as the Ticket's CustomerVerificationSource.</summary>
    public string? ExternalSource { get; private set; }
    /// <summary>The source's own customer identifier (for PACT, its tenantID) — persisted on the Ticket as ExternalCustomerId (an external identifier only, never a local reference).</summary>
    public string? ExternalCustomerId { get; private set; }
    /// <summary>The source's own identifier for the selected unit (for PACT, its unitID) — persisted on the Ticket as ExternalUnitId.</summary>
    public string? ExternalUnitId { get; private set; }
    public string? ExternalCustomerName { get; private set; }
    public string? ExternalProjectName { get; private set; }
    public string? ExternalUnitNumber { get; private set; }

    /// <summary>The manually-entered property, carried as wizard values once Step 2's manual form is submitted (the manual path's equivalent of the packed selections).</summary>
    public string? ManualProjectName { get; private set; }
    public string? ManualUnitNumber { get; private set; }

    /// <summary>The Department directory the Issue step's dropdown renders — real, existing Departments only, never a typed id.</summary>
    public IReadOnlyCollection<DepartmentDto> Departments { get; private set; } = [];

    /// <summary>Set only when the Departments API call itself failed — the dropdown still renders (empty), the page just says so.</summary>
    public string? DepartmentsErrorMessage { get; private set; }

    /// <summary>
    /// The active Request Types (Categories) the Issue step offers — scoped
    /// to the selected Department when one is chosen, otherwise every active
    /// Category grouped by its Department (the pre-redesign behavior,
    /// preserved so a Department choice is a narrowing aid, never a gate).
    /// </summary>
    public IReadOnlyCollection<CategoryDto> Categories { get; private set; } = [];

    /// <summary>Set only when the Categories API call itself failed — distinct from "loaded successfully but empty".</summary>
    public string? CategoriesErrorMessage { get; private set; }

    /// <summary>
    /// Duplicate-ticket awareness: the selected customer's recent tickets
    /// for the selected unit (same verified identity, same unit snapshot,
    /// active tickets first), shown as the advisory "Related tickets" panel
    /// on Step 2 once a unit is selected. Advisory only — creation is never
    /// blocked; the agent always keeps "Continue with New Ticket". Null when
    /// there is no verified identity to key on (plain manual entry — a
    /// manual customer must never be associated with someone else's tickets)
    /// or when the lookup itself failed; a failure never blocks the wizard.
    /// </summary>
    public CustomerHistoryDto? RelatedTickets { get; private set; }

    public long? CreatedTicketId { get; private set; }
    public string? CreatedTicketNumber { get; private set; }

    // ---- Summary panel (the right-hand sticky panel) — selected display
    // values only, never technical ids, with "Not selected yet" placeholders
    // rendered by the view when these are null. ----

    public bool HasUnitSelection =>
        CrmBuyerUnitId is not null
        || ExternalSelection is not null
        || (!string.IsNullOrWhiteSpace(ManualProjectName) && !string.IsNullOrWhiteSpace(ManualUnitNumber));

    public string? SummaryCustomerName =>
        CrmBuyerCustomerName
        ?? ExternalCustomerName
        ?? SelectedCandidate?.DisplayName
        ?? (CustomerKey == "manual" ? "Manual entry" : null);

    /// <summary>The verification-source label for the summary/review — "Tiger CRM"/"PACT"/"Tasleeh", or "Manual entry" (manual entry is not externally verified; the wording never says "not verified").</summary>
    public string? SummarySourceKey =>
        CrmBuyerUnitId is not null || CustomerKey == "crm" ? "Crm"
        : ExternalSource ?? (CustomerKey is { } key && key.StartsWith("ext:", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(key.Split(':', 3)[1])
            : CustomerKey == "manual" ? "Manual" : null);

    public string? SummaryProjectName => CrmBuyerProjectName ?? ExternalProjectName ?? ManualProjectName;
    public string? SummaryUnitNumber => CrmBuyerUnitNumber ?? ExternalUnitNumber ?? ManualUnitNumber;

    public CategoryDto? SelectedCategory =>
        CreateStep.CategoryId is { } categoryId ? Categories.FirstOrDefault(c => c.CategoryId == categoryId) : null;

    public string? SummaryDepartmentName =>
        SelectedCategory?.DepartmentName
        ?? (CreateStep.DepartmentId is { } departmentId
            ? Departments.FirstOrDefault(d => d.DepartmentId == departmentId)?.Name
            : null);

    /// <summary>1-based index of the current step for the progress stepper (Review and Done both render as step 4).</summary>
    public int StepIndex => Step switch
    {
        StepProperty => 2,
        StepIssue => 3,
        StepReview or StepDone => 4,
        _ => 1
    };

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        int? departmentId, long? createdTicketId, string? createdTicketNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            step ?? StepCustomer, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        switch (Step)
        {
            case StepProperty:
                await LoadPropertyStepAsync(cancellationToken);
                break;

            case StepIssue:
                CreateStep.DepartmentId = departmentId;
                CreateStep.ManualProjectName = ManualProjectName;
                CreateStep.ManualUnitNumber = ManualUnitNumber;
                await LoadIssueStepAsync(cancellationToken);
                break;

            case StepReview:
                // Review renders only from the Issue form's own POST (the
                // entered text never travels through a URL) — a direct GET
                // lands back on the Issue step with the selection intact.
                return RedirectToPage(new
                {
                    step = StepIssue, intakeRecordId, phoneNumber, customer,
                    crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
                    crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
                    externalSelection, manualProjectName, manualUnitNumber, departmentId
                });

            case StepDone:
                if (createdTicketId is null || string.IsNullOrWhiteSpace(createdTicketNumber))
                {
                    return RedirectToPage();
                }

                CreatedTicketId = createdTicketId;
                CreatedTicketNumber = createdTicketNumber;
                break;

            default:
                await LoadCustomerStepAsync(cancellationToken);
                break;
        }

        return Page();
    }

    // ---------------------------------------------------------------
    // Step 1 — Customer
    // ---------------------------------------------------------------

    /// <summary>
    /// Records the interaction (channel + phone, verbatim) as the
    /// IntakeRecord every ticket requires, then returns to Step 1 to show
    /// the lookup results. The intake carries no Department in this flow
    /// (Department moved to the Issue step), so the department-aware lookup
    /// searches every configured source — the existing no-department rule,
    /// not a new one.
    /// </summary>
    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Step = StepCustomer;
            return Page();
        }

        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.PhoneNumber, DepartmentId: null, IsUnitRelated: false,
            RawUnitNumberEntered: null, PriorityHint: null);

        var result = await intakeClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not record this interaction.");
            Step = StepCustomer;
            return Page();
        }

        return RedirectToPage(new
        {
            step = StepCustomer,
            intakeRecordId = result.Value.IntakeRecordId,
            phoneNumber = result.Value.PhoneNumber
        });
    }

    private async Task LoadCustomerStepAsync(CancellationToken cancellationToken)
    {
        // Customer Workspace carry-forward: "+ New Ticket" from a searched/
        // selected customer arrives with ?phoneNumber=… — the search field is
        // prefilled (still free-form and editable, value preserved exactly)
        // so the agent never re-types the number; pressing Search re-verifies
        // through the exact same lookups as always.
        if (!string.IsNullOrWhiteSpace(PhoneNumber))
        {
            Intake.PhoneNumber = PhoneNumber;
        }

        if (IntakeRecordId is null || string.IsNullOrWhiteSpace(PhoneNumber))
        {
            return;
        }

        await RunLookupsAsync(cancellationToken);
        BuildCandidates();
        await LoadCandidateHistoriesAsync(cancellationToken);
    }

    /// <summary>
    /// The department-aware customer lookup decides which sources
    /// participate (and carries the PACT/Tasleeh results); the real CRM
    /// Buyer Lookup then runs only when the Crm source is in scope. Both are
    /// idempotent reads, re-run on the steps that render their results —
    /// the agent's explicit selections are what carry forward, never
    /// re-derived.
    /// </summary>
    private async Task RunLookupsAsync(CancellationToken cancellationToken)
    {
        await RunCustomerLookupAsync(cancellationToken);
        if (CrmParticipates)
        {
            await RunCrmBuyerLookupAsync(cancellationToken);
        }
    }

    private async Task RunCustomerLookupAsync(CancellationToken cancellationToken)
    {
        if (IntakeRecordId is not { } intakeRecordId)
        {
            // Deep-linked without an intake id — nothing to look up against;
            // fail open to the CRM-only behavior.
            CustomerLookupUnavailable = true;
            return;
        }

        var result = await customerLookupClient.SearchAsync(intakeRecordId, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            CustomerLookup = result.Value;
            CrmParticipates = result.Value.Sources.Any(s => string.Equals(s.Source, "Crm", StringComparison.Ordinal));
        }
        else
        {
            CustomerLookupUnavailable = true;
        }
    }

    /// <summary>
    /// The one and only CRM search this wizard ever runs — phone number
    /// only, never Unit Number/Project/Tower. Found (200), NotFound (404),
    /// an ambiguous multi-customer conflict (409), and every other outcome
    /// (401/400/502/network-unreachable) are all handled here without
    /// blocking the wizard — every one of them leaves the manual path open.
    /// </summary>
    private async Task RunCrmBuyerLookupAsync(CancellationToken cancellationToken)
    {
        var result = await crmBuyerLookupClient.SearchByPhoneAsync(PhoneNumber!, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            // Business rule: a CRM phone number belongs to exactly one
            // customer — CrmBuyerLookupAppService (TigerCS.Api) already
            // consolidates to at most one entry. Taking the first element is
            // defensive only (never trust a contract further than the wire),
            // not a "pick among several customers" decision this page makes.
            CrmBuyerMatch = result.Value.Count > 0 ? result.Value[0] : null;
        }
        else if (result.Outcome == ApiOutcome.NotFound)
        {
            CrmBuyerMatch = null;
        }
        else if (result.Outcome == ApiOutcome.Conflict)
        {
            CrmBuyerAmbiguousMatch = true;
            CrmBuyerMatch = null;
        }
        else
        {
            CrmBuyerLookupUnavailable = true;
            CrmBuyerMatch = null;
        }
    }

    private void BuildCandidates()
    {
        var candidates = new List<NewTicketCandidate>();

        if (CrmBuyerMatch is { } match)
        {
            candidates.Add(new NewTicketCandidate(
                "crm",
                "Crm",
                match.Customer.FullNameEnglish ?? match.Customer.FullNameArabic,
                match.Customer.MobileNumber ?? PhoneNumber,
                match.Customer.Email,
                match.Units.Count));
        }

        foreach (var source in ExternalLookupSources.Where(s => s.Status == "Found"))
        {
            foreach (var external in source.Customers)
            {
                candidates.Add(new NewTicketCandidate(
                    $"ext:{Uri.EscapeDataString(source.Source)}:{Uri.EscapeDataString(external.ExternalCustomerId)}",
                    source.Source,
                    external.DisplayName,
                    external.PhoneNumber ?? PhoneNumber,
                    external.Email,
                    external.Units.Count));
            }
        }

        Candidates = candidates;
        SelectedCandidate = CustomerKey is { } key ? candidates.FirstOrDefault(c => c.Key == key) : null;
    }

    /// <summary>
    /// One bounded, identity-keyed history read per matched customer — the
    /// exact stable identity each card represents (CRM customer id, or the
    /// persisted source + external customer id), never a name or phone
    /// match. Serves both the card's counts and the compact existing-tickets
    /// notice. A failed read leaves that card without the notice; the wizard
    /// never blocks on it.
    /// </summary>
    private async Task LoadCandidateHistoriesAsync(CancellationToken cancellationToken)
    {
        var histories = new Dictionary<string, CustomerHistoryDto>();
        foreach (var candidate in Candidates)
        {
            var result = candidate.Key == "crm" && CrmBuyerMatch is { } match
                ? await customerHistoryClient.GetByCrmCustomerIdAsync(
                    match.Customer.CustomerId, CandidateHistoryLimit, cancellationToken, orderActiveFirst: true)
                : await LoadExternalCandidateHistoryAsync(candidate, cancellationToken);

            if (result is { IsSuccess: true, Value: not null })
            {
                histories[candidate.Key] = result.Value;
            }
        }

        CandidateHistories = histories;
    }

    private Task<ApiResult<CustomerHistoryDto>> LoadExternalCandidateHistoryAsync(
        NewTicketCandidate candidate, CancellationToken cancellationToken)
    {
        var parts = candidate.Key.Split(':', 3);
        return customerHistoryClient.GetByExternalIdentityAsync(
            Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]),
            CandidateHistoryLimit, cancellationToken, orderActiveFirst: true);
    }

    // ---------------------------------------------------------------
    // Step 2 — Property / Unit
    // ---------------------------------------------------------------

    private async Task LoadPropertyStepAsync(CancellationToken cancellationToken)
    {
        // The unit options belong to the selected customer's own lookup
        // results, so those results are re-read here (idempotent reads);
        // once a unit is selected its packed value carries forward and no
        // further lookup runs on later steps.
        if (CustomerKey == "crm" || CustomerKey?.StartsWith("ext:", StringComparison.Ordinal) == true)
        {
            await RunLookupsAsync(cancellationToken);
            BuildCandidates();
        }

        if (HasUnitSelection && CustomerKey != "manual")
        {
            await LoadRelatedTicketsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The agent explicitly selected one Buyer's one eligible unit from the
    /// real CRM Buyer Lookup results — its CRM identifiers carry forward to
    /// ticket creation. <paramref name="selectedCrmBuyerUnit"/> packs
    /// "{customerId}:{leadId}:{unitId}:{projectId}:{escaped customer
    /// name}:{escaped project name}:{escaped unit number}" into one value so
    /// the ids and the display text the agent saw always travel together and
    /// can never be mismatched. Every text field is
    /// <see cref="Uri.EscapeDataString(string)"/>-encoded before packing
    /// (encoding a literal ':' as %3A), so splitting on ':' is always safe.
    /// </summary>
    public IActionResult OnPostUseCrmBuyerUnit(
        long intakeRecordId, string? phoneNumber, string selectedCrmBuyerUnit)
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
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer = "crm",
            crmBuyerCustomerId = customerId,
            crmBuyerLeadId = leadId,
            crmBuyerUnitId = unitId,
            crmBuyerProjectId = projectId,
            crmBuyerCustomerName = customerName,
            crmBuyerProjectName = projectName,
            crmBuyerUnitNumber = unitNumber
        });
    }

    /// <summary>
    /// The agent explicitly selected one PACT/Tasleeh customer's one unit —
    /// the same packed shape as <see cref="OnPostUseCrmBuyerUnit"/>, packing
    /// "{escaped source}:{escaped external customer id}:{escaped external
    /// unit id}:{escaped customer name}:{escaped project name}:{escaped unit
    /// number}" so ids and display text always travel together. Ticket
    /// creation persists the source/customer/unit identifiers as the
    /// Ticket's generic external verification identity plus the manual
    /// Project/Unit snapshot, exactly as before the redesign.
    /// </summary>
    public IActionResult OnPostUseExternalUnit(
        long intakeRecordId, string? phoneNumber, string? customer, string selectedExternalUnit) =>
        RedirectToPage(new
        {
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer,
            externalSelection = selectedExternalUnit
        });

    /// <summary>
    /// The manual property path — no verified unit exists (customer not
    /// found, a source without unit data, or the agent simply can't find the
    /// unit). Both fields are required together, are never used to run
    /// another lookup, and — exactly as before the redesign — a manual
    /// property carries no external identity: a manual customer must never
    /// be associated with another customer's verified records.
    /// </summary>
    public async Task<IActionResult> OnPostUseManualUnitAsync(
        long intakeRecordId, string? phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CreateStep.ManualProjectName) || string.IsNullOrWhiteSpace(CreateStep.ManualUnitNumber))
        {
            ErrorMessage = "Project and Unit Number are both required.";
            ApplyWizardState(
                StepProperty, intakeRecordId, phoneNumber, "manual",
                null, null, null, null, null, null, null, null, null, null);
            await LoadPropertyStepAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new
        {
            step = StepProperty,
            intakeRecordId,
            phoneNumber,
            customer = "manual",
            manualProjectName = CreateStep.ManualProjectName,
            manualUnitNumber = CreateStep.ManualUnitNumber
        });
    }

    /// <summary>
    /// Duplicate-ticket awareness for the selected unit: one scoped history
    /// query keyed by the exact verified identity the agent selected,
    /// narrowed to the selected unit's number snapshot, active tickets
    /// first, capped at 5. Deterministic by design; advisory only — a failed
    /// call leaves <see cref="RelatedTickets"/> null and the wizard proceeds.
    /// </summary>
    private async Task LoadRelatedTicketsAsync(CancellationToken cancellationToken)
    {
        if (CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            var result = await customerHistoryClient.GetByCrmCustomerIdAsync(
                crmBuyerCustomerId, limit: 5, cancellationToken,
                unitNumber: CrmBuyerUnitNumber, orderActiveFirst: true);
            RelatedTickets = result.IsSuccess ? result.Value : null;
            return;
        }

        if (ExternalSource is { } externalSource && ExternalCustomerId is { } externalCustomerId)
        {
            var result = await customerHistoryClient.GetByExternalIdentityAsync(
                externalSource, externalCustomerId, limit: 5, cancellationToken,
                unitNumber: ExternalUnitNumber, orderActiveFirst: true);
            RelatedTickets = result.IsSuccess ? result.Value : null;
        }
    }

    // ---------------------------------------------------------------
    // Step 3 — Issue, and Step 4 — Review & Create
    // ---------------------------------------------------------------

    private async Task LoadIssueStepAsync(CancellationToken cancellationToken)
    {
        await LoadDepartmentsAsync(cancellationToken);
        await LoadCategoriesAsync(CreateStep.DepartmentId, cancellationToken);
    }

    /// <summary>
    /// Redisplays the Issue step after the Department selection changed —
    /// the Request Type list reloads scoped to that Department, and every
    /// other entered value (priority, request text) survives because this is
    /// a plain POST round-trip of the same form. A Request Type that no
    /// longer belongs to the chosen Department is cleared rather than
    /// silently submitted against it.
    /// </summary>
    public async Task<IActionResult> OnPostIssueRefreshAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepIssue, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        await LoadIssueStepAsync(cancellationToken);

        if (CreateStep.CategoryId is { } categoryId && Categories.All(c => c.CategoryId != categoryId))
        {
            CreateStep.CategoryId = null;
        }

        return Page();
    }

    /// <summary>
    /// Validates the Issue step and renders the Review step — a POST
    /// round-trip (never a redirect), so the entered request text stays in
    /// the form post and out of any URL. Validation failures redisplay the
    /// Issue step with everything the agent entered intact.
    /// </summary>
    public async Task<IActionResult> OnPostReviewAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepIssue, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        if (ValidateIssue() is { } validationError)
        {
            ErrorMessage = validationError;
            await LoadIssueStepAsync(cancellationToken);
            return Page();
        }

        Step = StepReview;
        // The review resolves the selected Request Type/Department to their
        // display names from the same directory the Issue step used.
        await LoadIssueStepAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        long intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber,
        CancellationToken cancellationToken)
    {
        ApplyWizardState(
            StepReview, intakeRecordId, phoneNumber, customer,
            crmBuyerCustomerId, crmBuyerLeadId, crmBuyerUnitId, crmBuyerProjectId,
            crmBuyerCustomerName, crmBuyerProjectName, crmBuyerUnitNumber,
            externalSelection, manualProjectName, manualUnitNumber);

        // The dropdowns are the only way to supply a CategoryId/PriorityId —
        // never manually typed in — but the request is still rejected here
        // (rather than trusted) if somehow a value is missing. POST
        // /api/tickets is the actual authority and re-validates everything.
        if (ValidateIssue() is { } validationError)
        {
            ErrorMessage = validationError;
            Step = StepIssue;
            await LoadIssueStepAsync(cancellationToken);
            return Page();
        }

        var hasCrmBuyerMatch = CrmBuyerUnitId is not null;

        // A PACT/Tasleeh selection persists both ways: the generic external
        // verification identity (source + the source's own customer/unit
        // ids) AND the human-readable manual Project/Unit snapshot —
        // mutually exclusive with a CRM Buyer match, exactly as the Api
        // re-validates server-side.
        var hasExternalSelection = !hasCrmBuyerMatch && ExternalSelection is not null;
        var manualProject = CreateStep.ManualProjectName ?? ExternalProjectName;
        var manualUnit = CreateStep.ManualUnitNumber ?? ExternalUnitNumber;

        var request = new CreateTicketRequestDto(
            IntakeRecordId: intakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            CategoryId: CreateStep.CategoryId!.Value,
            PriorityId: CreateStep.PriorityId!.Value,
            RequestSummary: CreateStep.RequestSummary,
            CrmBuyerCustomerId: hasCrmBuyerMatch ? crmBuyerCustomerId : null,
            CrmBuyerLeadId: hasCrmBuyerMatch ? crmBuyerLeadId : null,
            CrmBuyerUnitId: hasCrmBuyerMatch ? crmBuyerUnitId : null,
            CrmBuyerProjectId: hasCrmBuyerMatch ? crmBuyerProjectId : null,
            CrmBuyerCustomerName: hasCrmBuyerMatch ? crmBuyerCustomerName : null,
            CrmBuyerProjectName: hasCrmBuyerMatch ? crmBuyerProjectName : null,
            CrmBuyerUnitNumber: hasCrmBuyerMatch ? crmBuyerUnitNumber : null,
            ManualProjectName: hasCrmBuyerMatch ? null : manualProject,
            ManualUnitNumber: hasCrmBuyerMatch ? null : manualUnit,
            CustomerVerificationSource: hasExternalSelection ? ExternalSource : null,
            ExternalCustomerId: hasExternalSelection ? ExternalCustomerId : null,
            ExternalUnitId: hasExternalSelection ? ExternalUnitId : null);

        var result = await ticketsClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? DescribeFailure(result.Outcome, "Could not create the ticket.");
            Step = StepReview;
            await LoadIssueStepAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new
        {
            step = StepDone,
            createdTicketId = result.Value.TicketId,
            createdTicketNumber = result.Value.TicketNumber
        });
    }

    /// <summary>The pre-create guards, shared by Review and Create: a property (verified or manual pair), a Request Type, a Priority, and the request text. The Api re-validates all of it.</summary>
    private string? ValidateIssue()
    {
        var hasCrmBuyerMatch = CrmBuyerUnitId is not null;
        var manualProject = CreateStep.ManualProjectName ?? ExternalProjectName;
        var manualUnit = CreateStep.ManualUnitNumber ?? ExternalUnitNumber;

        if (!hasCrmBuyerMatch && (string.IsNullOrWhiteSpace(manualProject) || string.IsNullOrWhiteSpace(manualUnit)))
        {
            return "Customer not found in CRM. Project and Unit Number are required.";
        }

        if (CreateStep.CategoryId is null)
        {
            return "Select a request type before continuing.";
        }

        if (CreateStep.PriorityId is null)
        {
            return "Select a priority before continuing.";
        }

        if (string.IsNullOrWhiteSpace(CreateStep.RequestSummary))
        {
            return "Describe the customer's request before continuing.";
        }

        return null;
    }

    // ---------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------

    private void ApplyWizardState(
        string step, long? intakeRecordId, string? phoneNumber, string? customer,
        int? crmBuyerCustomerId, int? crmBuyerLeadId, int? crmBuyerUnitId, int? crmBuyerProjectId,
        string? crmBuyerCustomerName, string? crmBuyerProjectName, string? crmBuyerUnitNumber,
        string? externalSelection, string? manualProjectName, string? manualUnitNumber)
    {
        Step = step;
        IntakeRecordId = intakeRecordId;
        PhoneNumber = phoneNumber;
        CustomerKey = string.IsNullOrWhiteSpace(customer) ? null : customer;
        CrmBuyerCustomerId = crmBuyerCustomerId;
        CrmBuyerLeadId = crmBuyerLeadId;
        CrmBuyerUnitId = crmBuyerUnitId;
        CrmBuyerProjectId = crmBuyerProjectId;
        CrmBuyerCustomerName = crmBuyerCustomerName;
        CrmBuyerProjectName = crmBuyerProjectName;
        CrmBuyerUnitNumber = crmBuyerUnitNumber;
        ManualProjectName = string.IsNullOrWhiteSpace(manualProjectName) ? null : manualProjectName;
        ManualUnitNumber = string.IsNullOrWhiteSpace(manualUnitNumber) ? null : manualUnitNumber;
        ApplyExternalSelection(externalSelection);
    }

    private void ApplyExternalSelection(string? externalSelection)
    {
        ExternalSelection = string.IsNullOrWhiteSpace(externalSelection) ? null : externalSelection;
        if (ExternalSelection is null)
        {
            return;
        }

        var parts = ExternalSelection.Split(':', 6);
        ExternalSource = UnescapeOrNull(parts, 0);
        ExternalCustomerId = UnescapeOrNull(parts, 1);
        ExternalUnitId = UnescapeOrNull(parts, 2);
        ExternalCustomerName = UnescapeOrNull(parts, 3);
        ExternalProjectName = UnescapeOrNull(parts, 4);
        ExternalUnitNumber = UnescapeOrNull(parts, 5);
    }

    private static string? UnescapeOrNull(string[] parts, int index)
    {
        if (index >= parts.Length || parts[index].Length == 0)
        {
            return null;
        }

        return Uri.UnescapeDataString(parts[index]);
    }

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

    private async Task LoadCategoriesAsync(int? departmentId, CancellationToken cancellationToken)
    {
        var result = await categoriesClient.GetCategoriesAsync(departmentId, cancellationToken);
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

        /// <summary>
        /// Free-form string, deliberately: a leading '+' (e.g.
        /// "+971501234567") is valid and the value is preserved EXACTLY as
        /// entered through UI → PageModel → API → IntakeRecord — no format
        /// annotation ([RegularExpression]/[Phone]/[DataType]), no HTML
        /// pattern/type restriction, and no reformatting anywhere upstream
        /// may be added (guarded by NewTicketModelTests/
        /// WebFrontEndCleanupTests). The one permitted transformation lives
        /// at the PACT integration boundary only
        /// (PactCustomerHttpGateway.NormalizePactPhone strips the '+' for
        /// PACT requests); CRM receives the number exactly as entered.
        /// </summary>
        [Required]
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public sealed class CreateStepInput
    {
        /// <summary>The Issue step's Department narrowing for the Request Type list — a real DepartmentId from the dropdown, never typed. The ticket's own department still derives from the selected Request Type server-side.</summary>
        public int? DepartmentId { get; set; }

        /// <summary>The real CategoryId of a dropdown selection ("Request Type") — never typed in by hand. Nullable so "nothing selected" is a distinct, validatable state rather than a fake id like 0.</summary>
        [Required(ErrorMessage = "Select a request type.")]
        public int? CategoryId { get; set; }

        /// <summary>1=Critical, 2=High, 3=Medium, 4=Low — dropdown only. Nullable so "nothing selected" is distinct and validatable.</summary>
        [Required(ErrorMessage = "Select a priority.")]
        public byte? PriorityId { get; set; }

        [Required]
        public string RequestSummary { get; set; } = string.Empty;

        /// <summary>Required together with <see cref="ManualUnitNumber"/> on the manual property path; never used to run another lookup.</summary>
        public string? ManualProjectName { get; set; }

        public string? ManualUnitNumber { get; set; }
    }
}
