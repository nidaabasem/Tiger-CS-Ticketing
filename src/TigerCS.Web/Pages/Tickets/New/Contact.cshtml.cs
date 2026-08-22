using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets.New;

/// <summary>
/// Step 3 — confirm which contact the agent is speaking to
/// (<c>GET /api/crm/units/{crmUnitId}/contacts</c> then
/// <c>POST /api/verification-sessions</c>).
///
/// <para>
/// The confirmation checkbox is a hard gate, mirroring the API's own
/// <c>Confirmed: must be true</c> rule. It is not a formality: the session it
/// produces is what lets a ticket be created as verified, and it captures an
/// immutable snapshot of the unit and contact at this moment.
/// </para>
/// </summary>
public sealed class ContactModel(
    CrmApiClient crmApiClient,
    VerificationSessionsApiClient verificationSessionsApiClient,
    WizardStateStore stateStore) : PageModel
{
    public WizardState? State { get; private set; }

    public IReadOnlyList<ContactVerificationResponseDto> Contacts { get; private set; } = [];

    public bool CrmUnavailable { get; private set; }

    public ApiProblem? Problem { get; private set; }

    public string? GateError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var redirect = await LoadAsync(cancellationToken);
        return redirect ?? Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(
        int? contactReferenceId, bool confirmed, string? contactLabel, CancellationToken cancellationToken)
    {
        var redirect = await LoadAsync(cancellationToken);
        if (redirect is not null)
        {
            return redirect;
        }

        if (contactReferenceId is null)
        {
            GateError = "Select the contact you are speaking to.";
            return Page();
        }

        if (!confirmed)
        {
            // Mirrors the API's rule rather than discovering it via a 400: the
            // agent must affirm the match before a session can exist.
            GateError = "Confirm that you have verified the caller's identity before continuing.";
            return Page();
        }

        var result = await verificationSessionsApiClient.CreateConfirmedAsync(
            State!.UnitReferenceId!.Value, contactReferenceId.Value, cancellationToken);

        if (!result.IsSuccess)
        {
            if (result.Problem!.StatusCode == HttpStatusCode.Unauthorized)
            {
                return RedirectToPage("/Account/Login", new { reason = "expired" });
            }

            Problem = result.Problem;
            return Page();
        }

        stateStore.Write(HttpContext, State with
        {
            VerificationSessionId = result.Required.VerificationSessionId,
            ContactLabel = contactLabel
        });

        return RedirectToPage("/Tickets/New/Review");
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        State = stateStore.Read(HttpContext);

        if (State is null)
        {
            return RedirectToPage("/Tickets/New/Intake");
        }

        if (State.UnitReferenceId is null || string.IsNullOrEmpty(State.CrmUnitId))
        {
            return RedirectToPage("/Tickets/New/Unit");
        }

        var result = await crmApiClient.GetContactsAsync(State.CrmUnitId, cancellationToken);

        if (result.IsSuccess)
        {
            Contacts = result.Value ?? [];
            return null;
        }

        if (result.Problem!.StatusCode == HttpStatusCode.BadGateway)
        {
            CrmUnavailable = true;
            return null;
        }

        Problem = result.Problem;
        return null;
    }
}
