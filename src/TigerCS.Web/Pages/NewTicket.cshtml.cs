using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// "+ New Ticket": Intake → CRM unit/contact verification → ticket
/// creation, wired to the real endpoints (POST /api/intake-records, GET
/// /api/crm/units/..., POST /api/verification-sessions, POST /api/tickets).
/// Not part of the approved Direction B mockup (Login/Queue/Details only)
/// — added because the task explicitly requires connecting Intake and
/// Customer Verification to the real Api.
///
/// State is carried step-to-step via the query string (GET) and hidden
/// form fields (POST) rather than server-side session/TempData — every
/// step is a plain, bookmarkable, refresh-safe request with no session
/// state to lose, consistent with the rest of the app's no-JS-required
/// progressive-enhancement forms.
/// </summary>
public sealed class NewTicketModel(
    IntakeRecordsApiClient intakeClient,
    CrmApiClient crmClient,
    VerificationSessionsApiClient verificationClient,
    TicketsApiClient ticketsClient) : PageModel
{
    public string Step { get; private set; } = "intake";
    public string? ErrorMessage { get; private set; }

    [BindProperty] public IntakeInput Intake { get; set; } = new();
    [BindProperty] public UnitSearchStepInput UnitSearchStep { get; set; } = new();
    public IReadOnlyList<UnitVerificationResponseDto> UnitResults { get; private set; } = [];
    public IReadOnlyList<ContactVerificationResponseDto> ContactResults { get; private set; } = [];
    [BindProperty] public ConfirmStepInput ConfirmStep { get; set; } = new();
    [BindProperty] public CreateStepInput CreateStep { get; set; } = new();

    public long? IntakeRecordId { get; private set; }
    public int? UnitReferenceId { get; private set; }
    public string? CrmUnitId { get; private set; }
    public string? UnitNumber { get; private set; }
    public int? ContactReferenceId { get; private set; }
    public string? ContactDisplayName { get; private set; }
    public string? VerificationSessionId { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? step, long? intakeRecordId, int? unitReferenceId, string? crmUnitId, string? unitNumber,
        int? contactReferenceId, string? contactDisplayName, string? verificationSessionId,
        CancellationToken cancellationToken)
    {
        Step = step ?? "intake";
        IntakeRecordId = intakeRecordId;
        UnitReferenceId = unitReferenceId;
        CrmUnitId = crmUnitId;
        UnitNumber = unitNumber;
        ContactReferenceId = contactReferenceId;
        ContactDisplayName = contactDisplayName;
        VerificationSessionId = verificationSessionId;

        if (Step == "contact" && CrmUnitId is not null)
        {
            var result = await crmClient.GetContactsAsync(CrmUnitId, cancellationToken);
            ContactResults = result.IsSuccess && result.Value is not null ? result.Value : [];
        }

        return Page();
    }

    public async Task<IActionResult> OnPostIntakeAsync(CancellationToken cancellationToken)
    {
        var request = new CreateIntakeRecordRequestDto(
            Intake.ChannelId, Intake.IsUnitRelated, Intake.IsUnitRelated ? Intake.RawUnitNumberEntered : null, Intake.PriorityHint);

        var result = await intakeClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not record this interaction.";
            Step = "intake";
            return Page();
        }

        if (!Intake.IsUnitRelated)
        {
            // Business-rule change: a non-unit-related intake has no CRM
            // unit/contact to verify, so it skips straight to category
            // selection and ticket creation — no CRM Verification step.
            return RedirectToPage(new { step = "non-unit-create", intakeRecordId = result.Value.IntakeRecordId });
        }

        return RedirectToPage(new { step = "unit", intakeRecordId = result.Value.IntakeRecordId });
    }

    public async Task<IActionResult> OnPostSearchUnitAsync(long intakeRecordId, CancellationToken cancellationToken)
    {
        Step = "unit";
        IntakeRecordId = intakeRecordId;

        var result = await crmClient.SearchUnitsAsync(UnitSearchStep.UnitNumber, UnitSearchStep.PropertyName, cancellationToken);
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Outcome == ApiOutcome.BadGateway
                ? "Tiger CRM is currently unavailable. Try again shortly."
                : result.Detail ?? "Could not search CRM units.";
            return Page();
        }

        UnitResults = result.Value ?? [];
        if (UnitResults.Count == 0)
        {
            ErrorMessage = "No units matched that search.";
        }

        return Page();
    }

    public IActionResult OnPostSelectUnit(long intakeRecordId, int unitReferenceId, string crmUnitId, string unitNumber) =>
        RedirectToPage(new { step = "contact", intakeRecordId, unitReferenceId, crmUnitId, unitNumber });

    public IActionResult OnPostSelectContact(
        long intakeRecordId, int unitReferenceId, string crmUnitId, string unitNumber, int contactReferenceId, string? contactDisplayName) =>
        RedirectToPage(new { step = "confirm", intakeRecordId, unitReferenceId, crmUnitId, unitNumber, contactReferenceId, contactDisplayName });

    public async Task<IActionResult> OnPostConfirmAsync(
        long intakeRecordId, int unitReferenceId, string unitNumber, int contactReferenceId, string? contactDisplayName,
        CancellationToken cancellationToken)
    {
        Step = "confirm";
        IntakeRecordId = intakeRecordId;
        UnitReferenceId = unitReferenceId;
        UnitNumber = unitNumber;
        ContactReferenceId = contactReferenceId;
        ContactDisplayName = contactDisplayName;

        var request = new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true, ConfirmStep.VerificationMethod);
        var result = await verificationClient.CreateAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not create the verification session.";
            return Page();
        }

        return RedirectToPage(new { step = "create", intakeRecordId, verificationSessionId = result.Value.VerificationSessionId });
    }

    public async Task<IActionResult> OnPostCreateAsync(long intakeRecordId, Guid verificationSessionId, CancellationToken cancellationToken)
    {
        Step = "create";
        IntakeRecordId = intakeRecordId;

        var request = new CreateTicketFromVerificationRequestDto(
            intakeRecordId, verificationSessionId, CreateStep.CategoryId, CreateStep.PriorityId, CreateStep.RequestSummary);

        var result = await ticketsClient.CreateFromVerificationAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not create the ticket.";
            return Page();
        }

        return RedirectToPage("/TicketDetails", new { id = result.Value.TicketId });
    }

    /// <summary>Business-rule change: a non-unit-related intake skips CRM Verification entirely and creates the ticket directly from the selected category.</summary>
    public async Task<IActionResult> OnPostCreateNonUnitAsync(long intakeRecordId, CancellationToken cancellationToken)
    {
        Step = "non-unit-create";
        IntakeRecordId = intakeRecordId;

        var request = new CreateTicketFromNonUnitIntakeRequestDto(
            intakeRecordId, CreateStep.CategoryId, CreateStep.PriorityId, CreateStep.RequestSummary);

        var result = await ticketsClient.CreateFromNonUnitIntakeAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            ErrorMessage = result.Detail ?? "Could not create the ticket.";
            return Page();
        }

        return RedirectToPage("/TicketDetails", new { id = result.Value.TicketId });
    }

    public sealed class IntakeInput
    {
        [Required]
        public string ChannelId { get; set; } = "Phone";
        public bool IsUnitRelated { get; set; } = true;
        public string? RawUnitNumberEntered { get; set; }
        public byte? PriorityHint { get; set; }
    }

    public sealed class UnitSearchStepInput
    {
        [Required]
        public string UnitNumber { get; set; } = string.Empty;
        public string? PropertyName { get; set; }
    }

    public sealed class ConfirmStepInput
    {
        [Required]
        public string VerificationMethod { get; set; } = "ManualAgentConfirmation";
    }

    public sealed class CreateStepInput
    {
        [Required, Range(1, int.MaxValue)]
        public int CategoryId { get; set; }
        public byte PriorityId { get; set; } = 3;
        [Required]
        public string RequestSummary { get; set; } = string.Empty;
    }
}
