using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Authorization;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets;

/// <summary>
/// One ticket: detail, SLA, activity, notes, and every action the caller may
/// take (MVP-API-Contracts.md §3.3, §3.5-§3.10, §4.1/§4.2, §5.1/§5.9).
///
/// <para>
/// <b>Every action here posts to the API and renders whatever comes back.</b>
/// The action bar is filtered by <see cref="TicketActionPolicy"/> so it shows
/// what this user can actually use, but no handler assumes that filtering
/// happened: each one handles 403, 409, 410 and 422 explicitly, because the
/// server decides again and can legitimately refuse an action the bar offered
/// - most obviously when someone else changed the ticket first.
/// </para>
/// </summary>
public sealed class DetailsModel(
    TicketsApiClient ticketsApiClient,
    TicketSlaApiClient slaApiClient,
    DirectoryService directoryService,
    UserContextAccessor userContextAccessor) : PageModel
{
    private const int NotePageSize = 100;

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public UserContext CurrentUser { get; private set; } = UserContext.Anonymous;

    public TicketDetailDto? Ticket { get; private set; }

    public TicketSlaSummaryResponseDto? Sla { get; private set; }

    public IReadOnlyList<TicketEscalationResponseDto> Escalations { get; private set; } = [];

    public IReadOnlyList<TicketNoteResponseDto> Notes { get; private set; } = [];

    public IReadOnlyList<DirectoryMember> AssignableMembers { get; private set; } = [];

    public IReadOnlyDictionary<Guid, string> PeopleNames { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>A failure that prevented the ticket loading at all.</summary>
    public ApiProblem? LoadProblem { get; private set; }

    /// <summary>A failure from an action the user just attempted.</summary>
    public ApiProblem? ActionProblem { get; private set; }

    /// <summary>Confirmation of an action that just succeeded.</summary>
    public string? ActionSuccess { get; private set; }

    public bool IsClosed => Ticket is not null && TicketActionPolicy.IsClosed(Ticket);

    public bool IsPendingCrm => Ticket is not null && TicketActionPolicy.IsPendingCrm(Ticket.VerificationStatus);

    /// <summary>The safe place to go back to - the queue, with its filters, when one was supplied.</summary>
    public string BackUrl =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : Url.Page("/Tickets/Index")!;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken) ?? Page();

    // ---------------------------------------------------------------- actions

    public async Task<IActionResult> OnPostAssignAsync(
        Guid assignedEmployeeId, string rowVersion, CancellationToken cancellationToken) =>
        await ActAsync(
            () => ticketsApiClient.AssignAsync(Id, assignedEmployeeId, Decode(rowVersion), cancellationToken),
            "Ticket assigned.",
            cancellationToken);

    public async Task<IActionResult> OnPostTransferAsync(
        int targetDepartmentId, string reason, string rowVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            ActionProblem = new ApiProblem
            {
                StatusCode = HttpStatusCode.BadRequest,
                Title = "Reason required",
                Detail = "Give a reason for the transfer."
            };

            return await LoadAsync(cancellationToken) ?? Page();
        }

        return await ActAsync(
            () => ticketsApiClient.TransferAsync(Id, targetDepartmentId, reason, Decode(rowVersion), cancellationToken),
            "Ticket transferred. The receiving department will need to claim it.",
            cancellationToken);
    }

    public async Task<IActionResult> OnPostChangeStatusAsync(
        string newStatus, string rowVersion, CancellationToken cancellationToken) =>
        await ActAsync(
            () => ticketsApiClient.ChangeStatusAsync(Id, newStatus, Decode(rowVersion), cancellationToken),
            "Status updated.",
            cancellationToken);

    public async Task<IActionResult> OnPostResolveAsync(
        string resolutionOutcome,
        string resolutionNote,
        long? duplicateOfTicketId,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolutionNote))
        {
            ActionProblem = new ApiProblem
            {
                StatusCode = HttpStatusCode.BadRequest,
                Title = "Resolution note required",
                Detail = "Describe how the ticket was resolved."
            };

            return await LoadAsync(cancellationToken) ?? Page();
        }

        return await ActAsync(
            () => ticketsApiClient.ResolveAsync(
                Id, resolutionOutcome, resolutionNote, duplicateOfTicketId, Decode(rowVersion), cancellationToken),
            "Ticket resolved.",
            cancellationToken);
    }

    public async Task<IActionResult> OnPostCloseAsync(string rowVersion, CancellationToken cancellationToken) =>
        await ActAsync(
            () => ticketsApiClient.CloseAsync(Id, Decode(rowVersion), cancellationToken),
            "Ticket closed. It is now read-only.",
            cancellationToken);

    public async Task<IActionResult> OnPostReconcileAsync(
        Guid verificationSessionId, string rowVersion, CancellationToken cancellationToken) =>
        await ActAsync(
            () => ticketsApiClient.ReconcileAsync(Id, verificationSessionId, Decode(rowVersion), cancellationToken),
            "Ticket reconciled against CRM.",
            cancellationToken);

    public async Task<IActionResult> OnPostAddNoteAsync(string noteText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            ActionProblem = new ApiProblem
            {
                StatusCode = HttpStatusCode.BadRequest,
                Title = "Empty note",
                Detail = "Write something before adding the note."
            };

            return await LoadAsync(cancellationToken) ?? Page();
        }

        var result = await ticketsApiClient.AddNoteAsync(Id, noteText, cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = "Note added.";
        }
        else if (result.Problem!.StatusCode == HttpStatusCode.Unauthorized)
        {
            return SignInAgain();
        }
        else
        {
            ActionProblem = result.Problem;
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs one ticket mutation and reloads.
    ///
    /// <para>
    /// The reload is what makes a 409 recoverable: the ticket comes back with
    /// a fresh <c>rowVersion</c>, so the user can read what changed and retry
    /// against current state rather than being stuck with a stale token in a
    /// hidden field.
    /// </para>
    /// </summary>
    private async Task<IActionResult> ActAsync(
        Func<Task<ApiResult<TicketDetailDto>>> action, string successMessage, CancellationToken cancellationToken)
    {
        var result = await action();

        if (result.IsSuccess)
        {
            ActionSuccess = successMessage;
        }
        else if (result.Problem!.StatusCode == HttpStatusCode.Unauthorized)
        {
            return SignInAgain();
        }
        else
        {
            ActionProblem = result.Problem;
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    /// <summary>Loads everything the page shows. Returns a redirect when the page cannot be shown at all.</summary>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        CurrentUser = userContextAccessor.Current;

        var detail = await ticketsApiClient.GetDetailAsync(Id, cancellationToken);

        if (!detail.IsSuccess)
        {
            if (detail.Problem!.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SignInAgain();
            }

            LoadProblem = detail.Problem;
            return null;
        }

        Ticket = detail.Required;

        // These four are independent of each other, so they run together. Each
        // failure degrades only its own panel: a ticket that renders without
        // its escalation history is far better than one that does not render.
        var slaTask = slaApiClient.GetSlaAsync(Id, cancellationToken);
        var escalationsTask = slaApiClient.GetEscalationsAsync(Id, cancellationToken);
        var notesTask = ticketsApiClient.GetNotesAsync(Id, 1, NotePageSize, cancellationToken);
        var rosterTask = directoryService.GetAssignableMembersAsync(
            Ticket.CurrentDepartmentId, cancellationToken);

        await Task.WhenAll(slaTask, escalationsTask, notesTask, rosterTask);

        var sla = await slaTask;
        Sla = sla.IsSuccess ? sla.Value : null;

        var escalations = await escalationsTask;
        Escalations = escalations.IsSuccess && escalations.Value is not null ? escalations.Value : [];

        var notes = await notesTask;
        Notes = notes.IsSuccess && notes.Value is not null ? notes.Value.Items : [];

        AssignableMembers = await rosterTask;

        // The roster doubles as the name source for note authors and the
        // current owner, since it is the only employee-name endpoint there is.
        PeopleNames = AssignableMembers.ToDictionary(m => m.EmployeeId, m => m.DisplayName);

        return null;
    }

    private IActionResult SignInAgain() =>
        RedirectToPage("/Account/Login", new { reason = "expired", returnUrl = Url.Page("/Tickets/Details", new { id = Id }) });

    /// <summary>
    /// The row version travels through the page as the Base64 string the API
    /// gave us and is decoded on the way back, because the write DTOs take
    /// <c>byte[]</c>. A malformed value becomes an empty array, which the API
    /// answers with 409 - the same as any other stale token, and the right
    /// answer either way.
    /// </summary>
    private static byte[] Decode(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            return [];
        }

        Span<byte> buffer = new byte[((rowVersion.Length * 3) + 3) / 4];
        return Convert.TryFromBase64String(rowVersion, buffer, out var written)
            ? buffer[..written].ToArray()
            : [];
    }

    /// <summary>A person's display name when the department roster knows them.</summary>
    public string PersonName(Guid employeeId) =>
        PeopleNames.TryGetValue(employeeId, out var name) ? name : "Unknown staff member";
}
