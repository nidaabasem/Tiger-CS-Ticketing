using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Authorization;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets.New;

/// <summary>
/// Step 1 — record the interaction (<c>POST /api/intake-records</c>).
///
/// <para>
/// This runs before any CRM lookup, deliberately: MVP-ERD.md §2.9 makes the
/// intake record the unconditional first step so no interaction is lost even
/// if verification never completes or the CRM is down. The wizard follows that
/// order rather than creating the record later, once it knows whether it will
/// need one.
/// </para>
/// </summary>
public sealed class IntakeModel(
    IntakeApiClient intakeApiClient,
    WizardStateStore stateStore,
    UserContextAccessor userContextAccessor) : PageModel
{
    [BindProperty]
    public string RawUnitNumberEntered { get; set; } = string.Empty;

    [BindProperty]
    public byte? PriorityHint { get; set; }

    public string? FieldError { get; private set; }

    public ApiProblem? Problem { get; private set; }

    public bool IsPermitted { get; private set; }

    public void OnGet()
    {
        IsPermitted = TicketActionPolicy.CanCreateTickets(userContextAccessor.Current);

        // A fresh start clears anything left from an abandoned attempt, so a
        // stale intake record is never silently reused for a new caller.
        stateStore.Clear(HttpContext);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        IsPermitted = TicketActionPolicy.CanCreateTickets(userContextAccessor.Current);

        if (string.IsNullOrWhiteSpace(RawUnitNumberEntered))
        {
            FieldError = "Enter the unit number exactly as the caller gave it.";
            return Page();
        }

        // MVP scope is phone intake; Channel is a fixed API enum and this is
        // the only value a phone-desk screen can honestly send.
        var request = new CreateIntakeRecordRequestDto(
            ChannelId: "Phone",
            IsUnitRelated: true,
            RawUnitNumberEntered: RawUnitNumberEntered.Trim(),
            PriorityHint: PriorityHint);

        var result = await intakeApiClient.CreateAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            if (result.Problem!.StatusCode == HttpStatusCode.Unauthorized)
            {
                return RedirectToPage("/Account/Login", new { reason = "expired" });
            }

            Problem = result.Problem;
            return Page();
        }

        var intake = result.Required;

        stateStore.Write(HttpContext, new WizardState
        {
            IntakeRecordId = intake.IntakeRecordId,
            RawUnitNumberEntered = intake.RawUnitNumberEntered ?? RawUnitNumberEntered.Trim(),
            PriorityHint = PriorityHint
        });

        return RedirectToPage("/Tickets/New/Unit");
    }
}
