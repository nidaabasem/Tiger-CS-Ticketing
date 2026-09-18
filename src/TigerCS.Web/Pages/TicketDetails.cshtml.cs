using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// One entry in the Activity feed — built from real, separately-fetched
/// facts: the ticket's own CreatedAtUtc, its notes, its escalations and, since
/// the approved Reopen rule, its lifecycle history
/// (<c>GET /api/tickets/{id}/history</c>) and typed workflow events. Nothing
/// here is synthesized.
/// </summary>
/// <param name="Detail">A second line of already-resolved context (e.g. a reopen's department move and new SLA deadline). Never parsed by the view.</param>
public sealed record ActivityEntry(
    DateTime TimestampUtc, string Kind, string Actor, string Description, string? Note, bool IsHuman, string? Detail = null);

public sealed class TicketDetailsModel(
    TicketsApiClient ticketsApiClient,
    TicketSlaApiClient slaApiClient,
    UsersApiClient usersApiClient,
    TicketNameResolver nameResolver) : PageModel
{
    public long TicketId { get; private set; }

    /// <summary>
    /// The Tickets view (tab, filters, page) the agent came from, as the
    /// Tickets page remembered it — Ticket Details is a child workspace of
    /// Tickets, and its breadcrumb and back links lead back to that exact
    /// list. Falls back to the bare Tickets workspace when nothing is
    /// remembered (a deep link, a fresh session).
    /// </summary>
    public TicketsContext ReturnContext { get; private set; } = TicketsContext.Default;
    public ApiOutcome Outcome { get; private set; }
    public TicketDetailDto? Ticket { get; private set; }
    public TicketSlaSummaryResponseDto? Sla { get; private set; }
    public IReadOnlyList<TicketNoteResponseDto> Notes { get; private set; } = [];
    public IReadOnlyList<TicketEscalationResponseDto> Escalations { get; private set; } = [];

    /// <summary>
    /// Customer History — this ticket's customer's other tickets, verified
    /// (CrmBuyerCustomerId) or unverified (phone-snapshot fallback), the
    /// current ticket already excluded. Null only when the call itself
    /// failed; an empty <see cref="CustomerHistoryDto.Tickets"/> is a normal,
    /// successful "no previous tickets" result. Never a live CRM call.
    /// </summary>
    public CustomerHistoryDto? CustomerHistory { get; private set; }

    /// <summary>Approvals / Dependencies (Workflow/Automation phase 3) — cycles, requestable requirements, and the derived maintenance/prerequisite states. Null when the call failed; a view with empty lists is the normal "nothing configured" result and renders no section.</summary>
    public TicketApprovalsViewDto? Approvals { get; private set; }

    /// <summary>
    /// Conversation History (Genesys integration phase 1) — every call/chat
    /// recorded against this ticket, oldest first, each with its transcript.
    /// Empty is the normal result for a ticket created by hand; null only
    /// when the call itself failed.
    /// </summary>
    public IReadOnlyList<TicketInteractionDto> Interactions { get; private set; } = [];

    /// <summary>True when the interactions call failed, so the tab says so instead of implying the ticket has no conversations.</summary>
    public bool InteractionsUnavailable { get; private set; }

    // ---- The customer behind this ticket (the Customer summary card). ----
    // Identity comes from the ticket's own persisted facts as the Api's
    // customer-history read resolves them: CRM Buyer id, else the external
    // verification pair, else the intake phone — the same key the Customers
    // directory uses, so the card's link opens exactly that customer.

    /// <summary>The Customers directory key of this ticket's customer, or null when the ticket carries no customer identity at all.</summary>
    public string? CustomerKey { get; private set; }

    /// <summary>The Customer Profile for this ticket's customer, carrying this ticket so the profile can lead back — null when there is no identity to open.</summary>
    public string? CustomerProfileHref =>
        CustomerKey is null ? null : $"/Customers/{Uri.EscapeDataString(CustomerKey)}?fromTicket={TicketId}";

    /// <summary>The best persisted customer name: the CRM Buyer snapshot, else what the originating call/chat reported, else the history's own name.</summary>
    public string? CustomerDisplayName { get; private set; }

    /// <summary>The customer's phone: the intake snapshot the history read reports, else the originating interaction's caller number.</summary>
    public string? CustomerPhone { get; private set; }
    public string? DepartmentName { get; private set; }
    public string? OwnerName { get; private set; }
    public IReadOnlyList<DepartmentUserDto> AssignableEmployees { get; private set; } = [];

    /// <summary>
    /// Whether the viewer holds transfer authority (the Api's own CS Manager
    /// role set, or the System Administrator override) — the only case in
    /// which a Transfer control, and any department to transfer to, is shown.
    /// </summary>
    public bool CanTransfer { get; private set; }

    /// <summary>
    /// The departments the Transfer picker offers, by name: the ACTIVE
    /// directory minus the ticket's current department (the Api rejects both
    /// an inactive target and a no-op transfer), and only ever populated for a
    /// viewer who <see cref="CanTransfer"/>. The picker binds each name to its
    /// existing DepartmentId, so nobody types a number and the transfer
    /// contract is unchanged.
    /// </summary>
    public IReadOnlyList<DepartmentDto> TransferTargets { get; private set; } = [];

    /// <summary>True when the viewer may transfer but the department directory could not be loaded — the form says so instead of offering an empty picker.</summary>
    public bool TransferTargetsUnavailable { get; private set; }

    /// <summary>
    /// Departments the Reopen form offers. Unlike <see cref="TransferTargets"/>
    /// this deliberately INCLUDES the ticket's current department: reopening
    /// into the department that closed it is a legitimate choice the approved
    /// rule allows, and the API accepts it. Populated only for a viewer who
    /// <see cref="CanReopen"/> and only when the ticket is reopen-eligible.
    /// </summary>
    public IReadOnlyList<DepartmentDto> ReopenTargets { get; private set; } = [];

    /// <summary>True when the Reopen form should be offered but the department directory could not be loaded — it says so rather than offering an empty picker.</summary>
    public bool ReopenTargetsUnavailable { get; private set; }

    /// <summary>Whether the viewer's roles allow Reopen at all — the Api still decides, including whether they may act on this ticket.</summary>
    public bool CanReopen { get; private set; }
    public CurrentUser? Viewer { get; private set; }
    public TicketNameResolver NameResolver => nameResolver;
    public IReadOnlyList<ActivityEntry> ActivityFeed { get; private set; } = [];

    public string? ActionError { get; private set; }
    public string? OpenSection { get; private set; }

    [TempData]
    public string? ActionSuccess { get; set; }

    [BindProperty] public AssignInput Assign { get; set; } = new();
    [BindProperty] public TransferInput Transfer { get; set; } = new();
    [BindProperty] public StatusInput Status { get; set; } = new();
    [BindProperty] public ResolveInput Resolve { get; set; } = new();
    [BindProperty] public RowVersionInput Close { get; set; } = new();
    [BindProperty] public ReopenInput Reopen { get; set; } = new();
    [BindProperty] public EscalateInput Escalate { get; set; } = new();
    [BindProperty] public NoteInput Note { get; set; } = new();
    [BindProperty] public ApprovalRequestInput ApprovalRequest { get; set; } = new();
    [BindProperty] public ApprovalDecisionInput ApprovalDecision { get; set; } = new();
    [BindProperty] public WorkflowEventInput WorkflowEvent { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long id, int? reopen, CancellationToken cancellationToken)
    {
        TicketId = id;
        // ?reopen=1 arrives from a customer-workspace "Reopen" link — open
        // the reopen panel so the agent lands directly on the action.
        if (reopen == 1)
        {
            OpenSection = "reopen";
        }

        await LoadAsync(cancellationToken);
        if (Ticket is null && Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Assign.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("assign", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.AssignAsync(id, new AssignTicketRequestDto(Assign.AssignedEmployeeId, rowVersion), cancellationToken);
        return await HandleMutationAsync(result, "assign", "Ticket assigned.", cancellationToken);
    }

    public async Task<IActionResult> OnPostTransferAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Transfer.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("transfer", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.TransferAsync(
            id, new TransferTicketRequestDto(Transfer.TargetDepartmentId, Transfer.Reason, rowVersion), cancellationToken);
        return await HandleMutationAsync(result, "transfer", "Ticket transferred.", cancellationToken);
    }

    public async Task<IActionResult> OnPostChangeStatusAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Status.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("status", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        // The pending reason travels with the status change (required by the
        // API for either Pending target since the structured-pending phase).
        var result = await ticketsApiClient.ChangeStatusAsync(
            id, new ChangeStatusRequestDto(Status.NewStatus, rowVersion, Status.PendingReason), cancellationToken);
        return await HandleMutationAsync(result, "status", "Status updated.", cancellationToken);
    }

    public async Task<IActionResult> OnPostRequestApprovalAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.RequestApprovalAsync(
            id, new RequestApprovalRequestDto(ApprovalRequest.ApprovalType, ApprovalRequest.Comment), cancellationToken);
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, "Approval requested.", cancellationToken);
    }

    public async Task<IActionResult> OnPostDecideApprovalAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.DecideApprovalAsync(
            id, ApprovalDecision.ApprovalId,
            new DecideApprovalRequestDto(ApprovalDecision.Decision, ApprovalDecision.Comment), cancellationToken);
        var message = string.Equals(ApprovalDecision.Decision, "Reject", StringComparison.OrdinalIgnoreCase)
            ? "Approval rejected."
            : "Approval granted.";
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, message, cancellationToken);
    }

    public async Task<IActionResult> OnPostRecordEventAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.RecordWorkflowEventAsync(
            id, new RecordWorkflowEventRequestDto(WorkflowEvent.EventType, WorkflowEvent.Note), cancellationToken);
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, "Recorded.", cancellationToken);
    }

    private async Task<IActionResult> HandleApprovalActionAsync(
        bool isSuccess, ApiOutcome outcome, string? detail, string successMessage, CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            ActionSuccess = successMessage;
            return RedirectToPage(new { id = TicketId });
        }

        ActionError = DescribeError(outcome, detail);
        OpenSection = "approvals";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Resolve.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("resolve", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.ResolveAsync(
            id,
            new ResolveTicketRequestDto(Resolve.ResolutionOutcome, Resolve.ResolutionNote, null, Resolve.DuplicateOfTicketId, rowVersion),
            cancellationToken);
        return await HandleMutationAsync(result, "resolve", "Ticket resolved.", cancellationToken);
    }

    public async Task<IActionResult> OnPostCloseAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Close.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("close", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.CloseAsync(id, new CloseTicketRequestDto(rowVersion), cancellationToken);
        return await HandleMutationAsync(result, "close", "Ticket closed.", cancellationToken);
    }

    public async Task<IActionResult> OnPostReopenAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(Reopen.Reason))
        {
            return await ReloadWithErrorAsync("reopen", "Enter a reason before reopening.", cancellationToken);
        }

        if (Reopen.TargetDepartmentId <= 0)
        {
            return await ReloadWithErrorAsync("reopen", "Choose the department that will take this ticket on.", cancellationToken);
        }

        if (!TryDecodeRowVersion(Reopen.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("reopen", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.ReopenAsync(
            id, new ReopenTicketRequestDto(Reopen.Reason, Reopen.TargetDepartmentId, rowVersion), cancellationToken);

        // Stays on this page: the redirect re-reads the ticket, so the agent
        // sees In Progress, the department that now owns it, the resulting
        // assignee (or the department queue), the new Resolution SLA deadline
        // and the reopen in Activity — all as served facts, not as a message.
        return await HandleMutationAsync(result, "reopen", "Ticket reopened — it is In Progress again.", cancellationToken);
    }

    public async Task<IActionResult> OnPostEscalateAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Escalate.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("escalate", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var triggerType = Escalate.Level == 4 ? "ManualLevel4" : "ManualFlag";
        var result = await slaApiClient.EscalateAsync(
            id, new ManualEscalationRequestDto(Escalate.Level, triggerType, Escalate.Note, rowVersion), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = $"Escalated to level {Escalate.Level}.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = "escalate";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAddNoteAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(Note.NoteText))
        {
            ActionError = "Enter a note before saving.";
            OpenSection = "note";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await ticketsApiClient.AddNoteAsync(id, new CreateNoteRequestDto(Note.NoteText), cancellationToken);
        if (result.IsSuccess)
        {
            ActionSuccess = "Note added.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = "note";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> HandleMutationAsync(
        ApiResult<TicketDetailDto> result, string section, string successMessage, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            ActionSuccess = successMessage;
            return RedirectToPage(new { id = TicketId });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = section;
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> ReloadWithErrorAsync(string section, string message, CancellationToken cancellationToken)
    {
        ActionError = message;
        OpenSection = section;
        await LoadAsync(cancellationToken);
        return Page();
    }

    private static bool TryDecodeRowVersion(string? base64, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        try
        {
            rowVersion = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DescribeError(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Conflict => "This ticket changed since you loaded it. The latest version is now shown — please retry.",
        ApiOutcome.UnprocessableEntity => detail ?? "That action isn't valid for this ticket right now.",
        ApiOutcome.Forbidden => "You don't have permission to do that.",
        ApiOutcome.ValidationError => detail ?? "Check the values you entered.",
        ApiOutcome.NotFound => "That reference could not be found.",
        ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
        _ => detail ?? "Something went wrong."
    };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        ReturnContext = TicketsContext.FromCookieValue(Request.Cookies[TicketsContext.CookieName]);
        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var detailResult = await ticketsApiClient.GetByIdAsync(TicketId, cancellationToken);
        Outcome = detailResult.Outcome;
        if (!detailResult.IsSuccess || detailResult.Value is null)
        {
            Ticket = null;
            return;
        }

        Ticket = detailResult.Value;

        var slaTask = slaApiClient.GetSlaAsync(TicketId, cancellationToken);
        var notesTask = ticketsApiClient.GetNotesAsync(TicketId, 1, 50, cancellationToken);
        var escalationsTask = slaApiClient.GetEscalationsAsync(TicketId, cancellationToken);
        var assignableTask = usersApiClient.GetDepartmentUsersAsync(Ticket.CurrentDepartmentId, 1, 100, cancellationToken);
        var customerHistoryTask = ticketsApiClient.GetCustomerHistoryAsync(TicketId, limit: 10, cancellationToken);
        var approvalsTask = ticketsApiClient.GetApprovalsAsync(TicketId, cancellationToken);
        var interactionsTask = ticketsApiClient.GetInteractionsAsync(TicketId, cancellationToken);
        var historyTask = ticketsApiClient.GetLifecycleHistoryAsync(TicketId, cancellationToken);

        await Task.WhenAll(
            slaTask, notesTask, escalationsTask, assignableTask, customerHistoryTask, approvalsTask, interactionsTask,
            historyTask);

        Interactions = interactionsTask.Result.IsSuccess && interactionsTask.Result.Value is not null
            ? interactionsTask.Result.Value.Interactions
            : [];
        InteractionsUnavailable = !interactionsTask.Result.IsSuccess;

        Approvals = approvalsTask.Result.IsSuccess ? approvalsTask.Result.Value : null;
        Sla = slaTask.Result.IsSuccess ? slaTask.Result.Value : null;
        Notes = notesTask.Result.IsSuccess && notesTask.Result.Value is not null ? notesTask.Result.Value.Items : [];
        Escalations = escalationsTask.Result.IsSuccess && escalationsTask.Result.Value is not null ? escalationsTask.Result.Value : [];
        AssignableEmployees = assignableTask.Result.IsSuccess && assignableTask.Result.Value is not null ? [.. assignableTask.Result.Value.Items] : [];
        CustomerHistory = customerHistoryTask.Result.IsSuccess ? customerHistoryTask.Result.Value : null;

        var originating = Interactions.FirstOrDefault(i => i.IsOriginatingInteraction) ?? Interactions.FirstOrDefault();
        CustomerPhone = FirstNonEmpty(CustomerHistory?.PhoneNumberSnapshot, originating?.CustomerPhone);
        CustomerDisplayName = FirstNonEmpty(Ticket.CrmBuyerCustomerName, originating?.CustomerName, CustomerHistory?.CustomerDisplayName);
        CustomerKey = CustomerHistory?.CustomerKey
            ?? CustomerIdentity.FromTicketFacts(Ticket.CrmBuyerCustomerId, Ticket.CustomerVerificationSource, Ticket.ExternalCustomerId, CustomerPhone)?.Key;

        DepartmentName = nameResolver.TryGetDepartmentName(Ticket.CurrentDepartmentId);
        OwnerName = Ticket.CurrentOwnerEmployeeId is Guid ownerId
            ? await nameResolver.ResolveOwnerNameAsync(Ticket.CurrentDepartmentId, ownerId, cancellationToken)
            : null;

        await LoadTransferTargetsAsync(Ticket, cancellationToken);
        await LoadReopenTargetsAsync(Ticket, cancellationToken);

        var lifecycleHistory = historyTask.Result.IsSuccess && historyTask.Result.Value is not null
            ? historyTask.Result.Value.Entries
            : [];

        ActivityFeed = BuildActivityFeed(
            Ticket, Notes, Escalations, lifecycleHistory, Approvals?.Events ?? [], Viewer, nameResolver);

        // Pre-fill the RowVersion the forms will post back, and default the
        // employee/status pickers so an untouched form still submits something valid.
        Assign = new AssignInput { RowVersionBase64 = Ticket.RowVersion, AssignedEmployeeId = Ticket.CurrentOwnerEmployeeId ?? Guid.Empty };
        Transfer = new TransferInput { RowVersionBase64 = Ticket.RowVersion, TargetDepartmentId = TransferTargets.FirstOrDefault()?.DepartmentId ?? 0 };
        Status = new StatusInput { RowVersionBase64 = Ticket.RowVersion, NewStatus = Ticket.TicketStatus };
        Resolve = new ResolveInput { RowVersionBase64 = Ticket.RowVersion, ResolutionOutcome = "Resolved" };
        Close = new RowVersionInput { RowVersionBase64 = Ticket.RowVersion };
        Reopen = new ReopenInput
        {
            RowVersionBase64 = Ticket.RowVersion,
            // Defaults to the department that closed it — the common case, and
            // an explicit choice the agent can change rather than a blank.
            TargetDepartmentId = ReopenTargets.Any(d => d.DepartmentId == Ticket.CurrentDepartmentId)
                ? Ticket.CurrentDepartmentId
                : ReopenTargets.FirstOrDefault()?.DepartmentId ?? 0
        };
        Escalate = new EscalateInput { RowVersionBase64 = Ticket.RowVersion, Level = NextEscalationLevel(Ticket.EscalationLevel) };
    }

    /// <summary>
    /// Authorization first, then the directory: a viewer without transfer
    /// authority is offered no departments at all (not even their own), and
    /// an authorized viewer is offered only real, active departments other
    /// than the one the ticket is already in. The active-only directory call
    /// is skipped entirely for viewers who cannot transfer.
    /// </summary>
    private async Task LoadTransferTargetsAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        CanTransfer = TicketActions.CanTransfer(Viewer?.Roles);
        TransferTargets = [];
        TransferTargetsUnavailable = false;

        if (!CanTransfer)
        {
            return;
        }

        var active = await nameResolver.GetActiveDepartmentsAsync(cancellationToken);
        if (active is null)
        {
            TransferTargetsUnavailable = true;
            return;
        }

        TransferTargets =
        [
            .. active
                .Where(d => d.DepartmentId != ticket.CurrentDepartmentId)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The Reopen form's department picker. Role check first, then the
    /// directory — and skipped entirely when the ticket is not reopen-eligible,
    /// so a closed-but-expired ticket costs no directory call. Unlike transfer
    /// this keeps the ticket's current department in the list: reopening into
    /// the department that closed it is allowed.
    /// </summary>
    private async Task LoadReopenTargetsAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        CanReopen = TicketActions.CanReopen(Viewer?.Roles);
        ReopenTargets = [];
        ReopenTargetsUnavailable = false;

        if (!CanReopen || !ticket.IsReopenEligible)
        {
            return;
        }

        var active = await nameResolver.GetActiveDepartmentsAsync(cancellationToken);
        if (active is null)
        {
            ReopenTargetsUnavailable = true;
            return;
        }

        ReopenTargets = [.. active.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static IReadOnlyList<ActivityEntry> BuildActivityFeed(
        TicketDetailDto ticket,
        IReadOnlyList<TicketNoteResponseDto> notes,
        IReadOnlyList<TicketEscalationResponseDto> escalations,
        IReadOnlyList<TicketStatusHistoryEntryDto> lifecycleHistory,
        IReadOnlyList<TicketWorkflowEventDto> workflowEvents,
        CurrentUser? viewer,
        TicketNameResolver nameResolver)
    {
        var entries = new List<ActivityEntry>
        {
            new(ticket.CreatedAtUtc, "created", "System", $"Ticket {ticket.TicketNumber} created", null, false)
        };

        foreach (var note in notes)
        {
            var author = TicketNameResolver.ResolveSelfAuthorName(note.AuthorEmployeeId, viewer) ?? $"Employee #{note.AuthorEmployeeId.ToString()[..8]}";
            entries.Add(new ActivityEntry(note.CreatedAtUtc, "note", author, "added a note", note.NoteText, true));
        }

        foreach (var escalation in escalations)
        {
            var levelLabel = TicketDisplay.EscalationLevelLabel($"Level{escalation.Level}");
            entries.Add(new ActivityEntry(
                escalation.RaisedAtUtc, "escalation", "System",
                $"Escalated to {levelLabel} ({escalation.TriggerType})",
                escalation.NotifiedRoles is null ? null : $"Notified: {escalation.NotifiedRoles}", false));
        }

        // Approval cycles read from the typed workflow events the approval
        // service writes — the request (whose note is the reason, and for a
        // Reopen Approval that reason is the whole point) and the decision.
        // Deliberately separate entries from the reopen below: an approval
        // authorizes an ask, the reopen is the state change, and merging them
        // would claim the ticket moved when it did not.
        foreach (var approvalEvent in workflowEvents)
        {
            if (DescribeApprovalEvent(approvalEvent.EventType) is not { } description)
            {
                continue;
            }

            var actor = approvalEvent.ActorEmployeeId is { } approvalActorId
                ? TicketNameResolver.ResolveSelfAuthorName(approvalActorId, viewer) ?? $"Employee #{approvalActorId.ToString()[..8]}"
                : "System";

            entries.Add(new ActivityEntry(
                approvalEvent.OccurredAtUtc, "approval", actor, description, approvalEvent.Note, true));
        }

        // Reopens read from BOTH lifecycle stores, because each holds half of
        // what the entry has to say: the status-history row carries the agent
        // and the reason they typed, the typed Reopened event carries the
        // department move, where ownership landed and the new SLA deadline.
        // Matched by timestamp — one reopen writes both rows inside one
        // transaction at one instant.
        var reopenFacts = workflowEvents
            .Where(e => string.Equals(e.EventType, nameof(WorkflowEventType.Reopened), StringComparison.Ordinal))
            .ToDictionary(e => e.OccurredAtUtc, e => e.Note);

        foreach (var change in lifecycleHistory)
        {
            if (DescribeLifecycleChange(change, ticket) is not { } description)
            {
                continue;
            }

            var actor = change.ActorIsSystem || change.ActorEmployeeId is not { } actorId
                ? "System"
                : TicketNameResolver.ResolveSelfAuthorName(actorId, viewer) ?? $"Employee #{actorId.ToString()[..8]}";

            var isReopen = change is { Dimension: nameof(TicketStatusDimension.TicketStatus), NewValue: nameof(TicketStatus.InProgress) }
                && change.OldValue == nameof(TicketStatus.Closed);

            entries.Add(new ActivityEntry(
                change.OccurredAtUtc,
                isReopen ? "reopen" : "status",
                actor,
                description,
                change.Note,
                !change.ActorIsSystem,
                isReopen && reopenFacts.TryGetValue(change.OccurredAtUtc, out var note)
                    ? DescribeReopenFacts(note, nameResolver)
                    : null));
        }

        return [.. entries.OrderBy(e => e.TimestampUtc)];
    }

    /// <summary>
    /// What an approval-cycle workflow event reads as in the feed, or null
    /// for the dependency events (prerequisites/maintenance), which the
    /// Approvals panel already states as a current condition rather than a
    /// moment, and for <c>Reopened</c>, which the lifecycle row below owns.
    /// </summary>
    private static string? DescribeApprovalEvent(string eventType) => eventType switch
    {
        nameof(WorkflowEventType.ApprovalRequested) => "requested an approval",
        nameof(WorkflowEventType.ApprovalReceived) => "approved the request",
        nameof(WorkflowEventType.CustomerServiceApproved) => "approved the request (Customer Service)",
        nameof(WorkflowEventType.ApprovalRejected) => "rejected the request",
        _ => null
    };

    /// <summary>
    /// What one lifecycle row reads as in the feed, or null for rows worth
    /// nothing to a reader: the seeding rows written at creation (already
    /// covered by the "created" entry) and SLA-breach rows (already covered by
    /// the escalation entries they raise).
    /// </summary>
    private static string? DescribeLifecycleChange(TicketStatusHistoryEntryDto change, TicketDetailDto ticket) => change switch
    {
        { OldValue: null } => null,
        { Dimension: nameof(TicketStatusDimension.SlaState) } when change.NewValue == nameof(SlaState.Breached) => null,
        { Dimension: nameof(TicketStatusDimension.TicketStatus) } when
            change.OldValue == nameof(TicketStatus.Closed) && change.NewValue == nameof(TicketStatus.InProgress) =>
            $"reopened ticket {ticket.TicketNumber} — from Closed",
        { Dimension: nameof(TicketStatusDimension.TicketStatus) } =>
            $"changed status: {TicketDisplay.TicketStatusLabel(change.OldValue!)} → {TicketDisplay.TicketStatusLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.SlaState) } =>
            $"SLA: {TicketDisplay.SlaStateLabel(change.OldValue!)} → {TicketDisplay.SlaStateLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.VerificationStatus) } =>
            $"verification: {TicketDisplay.VerificationStatusLabel(change.OldValue!)} → {TicketDisplay.VerificationStatusLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.ResolutionOutcome) } =>
            $"recorded outcome: {change.NewValue}",
        _ => null
    };

    /// <summary>
    /// The reopen entry's second line, from the typed event's facts. Read back
    /// through the same <see cref="ReopenActivityFacts"/> the server wrote them
    /// with — never hand-parsed here — and degrades to no detail line at all if
    /// the note is not one this version understands.
    /// </summary>
    private static string? DescribeReopenFacts(string? note, TicketNameResolver nameResolver)
    {
        if (!ReopenActivityFacts.TryParse(note, out var facts))
        {
            return null;
        }

        var from = nameResolver.TryGetDepartmentName(facts.FromDepartmentId) ?? $"Department #{facts.FromDepartmentId}";
        var to = nameResolver.TryGetDepartmentName(facts.ToDepartmentId) ?? $"Department #{facts.ToDepartmentId}";

        var parts = new List<string> { $"Department: {from} → {to}" };
        parts.Add(facts.ResultingOwnerEmployeeId is null ? "Assigned to: Department queue" : "Reassigned automatically");
        if (facts.ResolutionSlaDueAtUtc is { } due)
        {
            parts.Add($"Resolution SLA due: {due:yyyy-MM-dd HH:mm} UTC");
        }

        return string.Join(" · ", parts);
    }

    private static byte NextEscalationLevel(string current) => current switch
    {
        "None" => 1,
        "Level1" => 2,
        "Level2" => 3,
        _ => 4
    };

    public sealed class AssignInput
    {
        [Required]
        public Guid AssignedEmployeeId { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class TransferInput
    {
        [Required, Range(1, int.MaxValue)]
        public int TargetDepartmentId { get; set; }
        [Required]
        public string Reason { get; set; } = string.Empty;
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class StatusInput
    {
        [Required]
        public string NewStatus { get; set; } = "Open";
        public string? RowVersionBase64 { get; set; }

        /// <summary>Required by the API when NewStatus is PendingCustomer/PendingThirdParty — a ticket is never pending without a recorded why.</summary>
        public string? PendingReason { get; set; }
    }

    public sealed class ResolveInput
    {
        [Required]
        public string ResolutionOutcome { get; set; } = "Resolved";
        [Required]
        public string ResolutionNote { get; set; } = string.Empty;
        public long? DuplicateOfTicketId { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class EscalateInput
    {
        [Range(1, 4)]
        public byte Level { get; set; } = 1;
        public string? Note { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class RowVersionInput
    {
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class ReopenInput
    {
        [Required]
        public string Reason { get; set; } = string.Empty;

        /// <summary>The department that takes the reopened work on. Required by the approved rule; may be the ticket's current department.</summary>
        [Required, Range(1, int.MaxValue)]
        public int TargetDepartmentId { get; set; }

        public string? RowVersionBase64 { get; set; }
    }

    public sealed class NoteInput
    {
        public string NoteText { get; set; } = string.Empty;
    }

    public sealed class ApprovalRequestInput
    {
        public string ApprovalType { get; set; } = string.Empty;
        public string? Comment { get; set; }
    }

    public sealed class ApprovalDecisionInput
    {
        public long ApprovalId { get; set; }
        public string Decision { get; set; } = "Approve";

        /// <summary>Optional on approval; the API requires it on rejection.</summary>
        public string? Comment { get; set; }
    }

    public sealed class WorkflowEventInput
    {
        public string EventType { get; set; } = string.Empty;
        public string? Note { get; set; }
    }
}
