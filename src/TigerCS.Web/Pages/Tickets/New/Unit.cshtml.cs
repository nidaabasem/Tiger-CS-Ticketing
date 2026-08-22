using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Web.Infrastructure;
using TigerCS.Web.Services;

namespace TigerCS.Web.Pages.Tickets.New;

/// <summary>
/// Step 2 — find the caller's unit in Tiger CRM
/// (<c>GET /api/crm/units/search</c>, MVP-API-Contracts.md §2.2).
///
/// <para>
/// A <c>502</c> here is not an error state. It is ISSUE-006's approved
/// CRM-outage path, and the screen presents it as a first-class route with a
/// clear next step - exactly as MVP-UI-Wireframes.md §4 requires ("the screen
/// does not show a bare error").
/// </para>
/// </summary>
public sealed class UnitModel(
    CrmApiClient crmApiClient,
    WizardStateStore stateStore) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? UnitNumber { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? PropertyName { get; set; }

    public WizardState? State { get; private set; }

    public IReadOnlyList<UnitVerificationResponseDto>? Results { get; private set; }

    public bool CrmUnavailable { get; private set; }

    public ApiProblem? Problem { get; private set; }

    public bool HasSearched { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        State = stateStore.Read(HttpContext);
        if (State is null)
        {
            return RedirectToPage("/Tickets/New/Intake");
        }

        // The number the caller gave is the obvious first search, so it runs
        // without making the agent retype it.
        UnitNumber ??= State.RawUnitNumberEntered;

        if (!string.IsNullOrWhiteSpace(UnitNumber))
        {
            await SearchAsync(cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSelectAsync(
        int unitReferenceId, string crmUnitId, string unitLabel, CancellationToken cancellationToken)
    {
        State = stateStore.Read(HttpContext);
        if (State is null)
        {
            return RedirectToPage("/Tickets/New/Intake");
        }

        stateStore.Write(HttpContext, State with
        {
            UnitReferenceId = unitReferenceId,
            CrmUnitId = crmUnitId,
            UnitLabel = unitLabel,
            CrmUnavailable = false,

            // Choosing a different unit invalidates any contact already
            // confirmed against the previous one.
            VerificationSessionId = null,
            ContactLabel = null
        });

        await Task.CompletedTask;
        _ = cancellationToken;

        return RedirectToPage("/Tickets/New/Contact");
    }

    /// <summary>
    /// Takes the wizard down ISSUE-006's fallback path when the CRM is
    /// unreachable. No verification session can exist on this path, so the
    /// review step will use the provisional endpoint.
    /// </summary>
    public IActionResult OnPostFallback()
    {
        State = stateStore.Read(HttpContext);
        if (State is null)
        {
            return RedirectToPage("/Tickets/New/Intake");
        }

        stateStore.Write(HttpContext, State with
        {
            CrmUnavailable = true,
            UnitReferenceId = null,
            CrmUnitId = null,
            UnitLabel = null,
            VerificationSessionId = null,
            ContactLabel = null
        });

        return RedirectToPage("/Tickets/New/Review");
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        HasSearched = true;

        var result = await crmApiClient.SearchUnitsAsync(UnitNumber!, PropertyName, cancellationToken);

        if (result.IsSuccess)
        {
            Results = result.Value ?? [];
            return;
        }

        if (result.Problem!.StatusCode == HttpStatusCode.BadGateway)
        {
            CrmUnavailable = true;
            return;
        }

        Problem = result.Problem;
    }
}
