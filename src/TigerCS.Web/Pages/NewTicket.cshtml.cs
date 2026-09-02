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
/// "+ New Ticket": Intake → department-aware customer lookup (CRM/PACT/
/// Tasleeh) → ticket creation, wired to the real endpoints
/// (POST /api/intake-records, GET /api/intake-records/{id}/customer-lookup,
/// GET /api/crm/buyers?phoneNumber={phoneNumber}, GET /api/departments,
/// POST /api/tickets).
///
/// <para>
/// <b>Step 2 is driven by the department-aware customer lookup.</b>
/// <see cref="CustomerLookupApiClient"/> (<c>CustomerLookupController</c> →
/// <c>CustomerLookupAppService</c>) is the authoritative source for which
/// integrations participate: it searches only the source(s) the intake's
/// Department enables via <c>DepartmentCustomerLookupSources</c> (all three
/// when no Department was selected; none when a Department has none
/// configured) — never an <c>if (department == X)</c> branch in this page.
/// PACT/Tasleeh results are rendered straight from that response. For the
/// Crm source, this page keeps calling the real CRM Buyer Lookup
/// (<see cref="CrmBuyerLookupApiClient"/> → <c>CrmController</c> →
/// <c>CrmBuyerLookupAppService</c> → <c>CrmBuyerHttpGateway</c> → the legacy
/// CRM's own <c>GetBuyerByPhone</c>) exactly as before — the generic
/// response's Crm entry decides only WHETHER CRM participates, because that
/// generic Crm leg is still backed by the fixture provider
/// (<c>Crm:Provider=Mock</c>) while Buyer Lookup is the real, verified CRM
/// integration; the CRM search itself stays phone-number-only, and
/// <c>Crm:SecretKey</c> stays server-to-server inside
/// <c>CrmBuyerHttpGateway</c> — this page, and the browser, never see it.
/// If the generic lookup itself cannot be reached, the page fails open to
/// the previous CRM-only behavior rather than losing CRM lookup entirely.
/// </para>
///
/// <para>
/// <b>Customer lookup no longer gates ticket creation.</b> For every source,
/// a match (Found), no match (NotFound), and an outage (Failed/Unavailable)
/// are all treated the same way for ticket creation: none of them block it,
/// and one source's failure never hides another source's results. Found
/// means the agent explicitly selects one customer's one unit — never
/// auto-selected. With no selection, Step 3 requires manually entered
/// Project and Unit Number instead, and no manual field is ever used to run
/// another lookup.
/// </para>
///
/// <para>
/// <b>A PACT/Tasleeh selection is a verified external identity, and it
/// persists.</b> A matched customer WAS verified — against that source — so
/// Step 3 presents it as "Verified via PACT", never as "not verified"
/// (manual entry, by contrast, is not externally verified). The selection
/// persists two ways on the created Ticket: the generic external
/// verification identity (<c>CustomerVerificationSource</c>/
/// <c>ExternalCustomerId</c>/<c>ExternalUnitId</c> — for PACT, the source
/// name, tenantID, and unitID; external identifiers only, no local cache
/// table and no foreign keys) plus the same human-readable Project/Unit
/// snapshot the manual path stores, prefilling Step 3's still-editable
/// manual fields.
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
    CustomerLookupApiClient customerLookupClient,
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

    /// <summary>
    /// The one CRM customer <c>GET /api/crm/buyers?phoneNumber=</c> matched —
    /// business rule: a CRM phone number belongs to exactly one customer, so
    /// this page is built for exactly one, never a list to disambiguate
    /// between. Carries every eligible Sold/Contract unit that customer owns
    /// — never auto-selected. Null until Step 2 has actually run a lookup, or
    /// when it found no match.
    /// </summary>
    public CrmBuyerMatchDto? CrmBuyerMatch { get; private set; }

    /// <summary>
    /// True when CRM answered with more than one distinct customer for this
    /// phone number (409 Conflict) — a CRM data-integrity conflict
    /// (<c>CrmBuyerLookupAppService.GetBuyerByPhoneAsync</c>'s
    /// <c>AmbiguousCustomerMatch</c> outcome), not "no match". No customer or
    /// unit is ever auto-selected for this case; the agent falls back to
    /// manual Project/Unit Number entry exactly as for no match, but sees a
    /// distinct message naming the conflict.
    /// </summary>
    public bool CrmBuyerAmbiguousMatch { get; private set; }

    /// <summary>True when CRM Buyer Lookup itself could not be reached/answered (outage, timeout, misconfiguration) rather than answering with zero matches — same "Project/Unit Number required" consequence as NotFound, but a different message.</summary>
    public bool CrmBuyerLookupUnavailable { get; private set; }

    /// <summary>
    /// The department-aware customer lookup response — one entry per source
    /// the intake's Department actually enables (see
    /// <see cref="RunCustomerLookupAsync"/>), each independently Found/
    /// NotFound/Failed so one source's outage never hides another's result.
    /// Null until Step 2 has run, or when the lookup call itself failed
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
    /// The non-CRM source entries (PACT/Tasleeh) to render, straight from the
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

    /// <summary>
    /// The PACT/Tasleeh customer+unit the agent selected on Step 2, packed
    /// exactly like the CRM radio value (see <see cref="OnPostUseExternalUnit"/>)
    /// and carried step-to-step via query string/hidden field like every
    /// other wizard value. Unpacked into the External* properties below.
    /// Display/carry-forward only past Step 3's prefill — the persisted
    /// record of the selection is the manual Project/Unit snapshot (see this
    /// type's remarks).
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
        string? externalSelection,
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
        ApplyExternalSelection(externalSelection);

        if (Step == "lookup" && !string.IsNullOrWhiteSpace(PhoneNumber))
        {
            // The department-aware lookup decides which sources participate
            // (and carries the PACT/Tasleeh results); the real CRM Buyer
            // Lookup then runs only when the Crm source is in scope.
            await RunCustomerLookupAsync(cancellationToken);
            if (CrmParticipates)
            {
                await RunCrmBuyerLookupAsync(cancellationToken);
            }
        }
        else if (Step == "create")
        {
            // A PACT/Tasleeh selection prefills the (still editable, still
            // required) manual Project/Unit fields — the existing manual
            // snapshot path is exactly how such a selection persists.
            if (CrmBuyerUnitId is null && ExternalSelection is not null)
            {
                CreateStep.ManualProjectName ??= ExternalProjectName;
                CreateStep.ManualUnitNumber ??= ExternalUnitNumber;
            }

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
    /// The department-aware customer lookup
    /// (<c>GET /api/intake-records/{id}/customer-lookup</c>) — the
    /// authoritative answer to which sources participate for this intake,
    /// straight from <c>DepartmentCustomerLookupSources</c> server-side:
    /// only the Department's configured source(s) when one was selected, all
    /// three when none was, zero when the Department configures none. Its own
    /// failure never blocks the wizard: <see cref="CustomerLookupUnavailable"/>
    /// is flagged and <see cref="CrmParticipates"/> stays true, so the page
    /// fails open to the previous CRM-only behavior.
    /// </summary>
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
    /// (401/400/502/network-unreachable — CRM outage or misconfiguration)
    /// are all handled here without blocking the wizard: only
    /// <see cref="CrmBuyerAmbiguousMatch"/>/<see cref="CrmBuyerLookupUnavailable"/>
    /// distinguish the message shown, not what the agent is allowed to do
    /// next — every one of these falls back to the same manual Project/Unit
    /// Number path on Step 3.
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
            // CrmBuyerLookupAppService's AmbiguousCustomerMatch outcome (409):
            // CRM named more than one distinct customer for this phone
            // number — a data-integrity conflict, not "no match". Never
            // auto-selects a customer/unit; the agent still falls back to
            // manual Project/Unit Number entry, but sees a message naming
            // the conflict rather than "not found".
            CrmBuyerAmbiguousMatch = true;
            CrmBuyerMatch = null;
        }
        else
        {
            CrmBuyerLookupUnavailable = true;
            CrmBuyerMatch = null;
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
    /// The agent explicitly selected one PACT/Tasleeh customer's one unit
    /// from the department-aware lookup results — the same packed-radio shape
    /// as <see cref="OnPostUseCrmBuyerUnit"/>, packing
    /// "{escaped source}:{escaped external customer id}:{escaped external
    /// unit id}:{escaped customer name}:{escaped project name}:{escaped unit
    /// number}" so ids and the display text the agent saw always travel
    /// together. Carried onward as one <c>externalSelection</c> value; Step 3
    /// prefills the manual Project/Unit fields from it (see
    /// <see cref="OnGetAsync"/>) and ticket creation persists the source/
    /// customer/unit identifiers as the Ticket's generic external
    /// verification identity (see this type's remarks).
    /// </summary>
    public IActionResult OnPostUseExternalUnit(
        long intakeRecordId, string? phoneNumber, int? departmentId, string selectedExternalUnit) =>
        RedirectToPage(new
        {
            step = "create",
            intakeRecordId,
            phoneNumber,
            departmentId,
            externalSelection = selectedExternalUnit
        });

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
        string? externalSelection,
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
        ApplyExternalSelection(externalSelection);

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

        // A PACT/Tasleeh selection persists both ways: the generic external
        // verification identity (source + the source's own customer/unit
        // ids) AND the human-readable manual Project/Unit snapshot below —
        // mutually exclusive with a CRM Buyer match, exactly as the Api
        // re-validates server-side.
        var hasExternalSelection = !hasCrmBuyerMatch && ExternalSelection is not null;

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
            ManualUnitNumber: hasCrmBuyerMatch ? null : CreateStep.ManualUnitNumber,
            CustomerVerificationSource: hasExternalSelection ? ExternalSource : null,
            ExternalCustomerId: hasExternalSelection ? ExternalCustomerId : null,
            ExternalUnitId: hasExternalSelection ? ExternalUnitId : null);

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
