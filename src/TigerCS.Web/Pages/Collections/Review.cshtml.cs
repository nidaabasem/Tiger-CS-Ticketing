using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Review and approval of Collections reminders. Filters, counts and pages come from stored review data (the Api queries
/// its own database); the financial source is only read by an explicit, visible refresh job. Sending is a separate,
/// confirmed step that shows the exact list, totals by currency and contacts first.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReviewModel(CollectionsApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? CompanyId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Project { get; set; }
    [BindProperty(SupportsGet = true)] public string? Unit { get; set; }
    [BindProperty(SupportsGet = true)] public string? Customer { get; set; }
    /// <summary>yyyy-MM from the month picker.</summary>
    [BindProperty(SupportsGet = true)] public string? Month { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? DueFrom { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? DueTo { get; set; }
    [BindProperty(SupportsGet = true)] public string? PaymentStatus { get; set; }
    [BindProperty(SupportsGet = true)] public string? MinRemaining { get; set; }
    [BindProperty(SupportsGet = true)] public string? MaxRemaining { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReminderType { get; set; }
    [BindProperty(SupportsGet = true)] public string? ValidationStatus { get; set; }
    [BindProperty(SupportsGet = true)] public string? Reason { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? Dispatch { get; set; }

    // Selection / confirmation (POST)
    [BindProperty] public string SelectionMode { get; set; } = ReviewQueryService.ModeSelectedKeys;
    [BindProperty] public string? SelectedKeys { get; set; }
    [BindProperty] public int ContactPage { get; set; } = 1;
    [BindProperty] public int ExpectedCount { get; set; }
    [BindProperty] public string? ExpectedFingerprint { get; set; }
    [BindProperty] public string? IdempotencyKey { get; set; }
    [BindProperty] public bool AckCampaign { get; set; }
    [BindProperty] public bool AckSharedPhone { get; set; }
    [BindProperty] public long BatchId { get; set; }
    [BindProperty] public string? Resolution { get; set; }
    [BindProperty] public string? Note { get; set; }

    public ReviewRunDto? Run { get; private set; }
    public ReviewPageDto? Report { get; private set; }
    public SelectionSummaryDto? Summary { get; private set; }
    public DispatchDto? DispatchResult { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string ConfirmKey { get; private set; } = Guid.NewGuid().ToString("N");
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));
    public bool RunActive => Run is { Status: "Queued" or "Running" };
    public static IReadOnlyList<ReviewReasonInfo> Reasons => ReviewReasons.All;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (Dispatch is { } id) { await LoadDispatchAsync(id, cancellationToken); return; }
        await LoadAsync(cancellationToken);
    }

    /// <summary>Progress poll for the refresh job (JSON, no financial data).</summary>
    public async Task<IActionResult> OnGetRunAsync(CancellationToken cancellationToken)
    {
        var run = await api.GetReviewRunAsync(cancellationToken);
        return new JsonResult(run.IsSuccess && run.Value is { } r
            ? new { r.Status, r.Phase, r.ProgressPercent, r.Error, active = r.Status is "Queued" or "Running" }
            : new { Status = "Unknown", Phase = "", ProgressPercent = 0, Error = (string?)null, active = false });
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken cancellationToken)
    {
        var started = await api.StartReviewRefreshAsync(CompanyId, cancellationToken);
        if (!started.IsSuccess) { Error = Describe(started.Outcome, started.Detail); await LoadAsync(cancellationToken); return Page(); }
        return Redirect(PageUrl(1));
    }

    public async Task<IActionResult> OnPostSummaryAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var result = await api.SummarizeSelectionAsync(BuildSelection(), cancellationToken);
        if (!result.IsSuccess) Error = Describe(result.Outcome, result.Detail);
        else if (result.Value!.Count == 0) Error = "No Ready records are selected. Only Ready records can be sent.";
        else Summary = result.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        var request = new ConfirmDispatchRequest(BuildSelection(), ExpectedCount, ExpectedFingerprint ?? "", IdempotencyKey ?? "", AckCampaign, AckSharedPhone);
        var result = await api.ConfirmDispatchAsync(request, cancellationToken);
        if (result.IsSuccess) return Redirect($"/Collections/Review?dispatch={result.Value!.DispatchId:D}");
        await LoadAsync(cancellationToken);
        Error = result.Outcome == ApiOutcome.Conflict
            ? (result.Detail ?? "The list changed. Review the current list and confirm again.")
            : Describe(result.Outcome, result.Detail);
        // Re-show the current exact list so the user can confirm what is really there now.
        var summary = await api.SummarizeSelectionAsync(BuildSelection(), cancellationToken);
        if (summary.IsSuccess && summary.Value!.Count > 0) Summary = summary.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostReconcileAsync(CancellationToken cancellationToken)
    {
        if (Dispatch is not { } id) return Redirect("/Collections/Review");
        var result = await api.ReconcileBatchAsync(id, BatchId, new ReconcileBatchRequest(Resolution ?? "", Note ?? ""), cancellationToken);
        if (!result.IsSuccess) Error = Describe(result.Outcome, result.Detail); else Notice = "Batch reconciled.";
        await LoadDispatchAsync(id, cancellationToken);
        return Page();
    }

    private async Task LoadDispatchAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await api.GetDispatchAsync(id, cancellationToken);
        if (result.IsSuccess) DispatchResult = result.Value; else Error ??= Describe(result.Outcome, result.Detail);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var run = await api.GetReviewRunAsync(cancellationToken);
        if (run.IsSuccess) Run = run.Value;
        var report = await api.GetReviewPageAsync(BuildFilter(), Math.Max(1, PageNumber), cancellationToken);
        if (report.IsSuccess) Report = report.Value;
        else Error ??= Describe(report.Outcome, report.Detail);
    }

    public ReviewFilter BuildFilter()
    {
        int? year = null, month = null;
        if (!string.IsNullOrWhiteSpace(Month) && DateTime.TryParseExact(Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        { year = m.Year; month = m.Month; }
        return new ReviewFilter(CompanyId, Project, Unit, Customer, year, month, DueFrom, DueTo, PaymentStatus,
            ParseMoney(MinRemaining), ParseMoney(MaxRemaining), ReminderType, ValidationStatus, Reason);
    }

    private SelectionRequest BuildSelection() => new(BuildFilter(), SelectionMode,
        string.IsNullOrWhiteSpace(SelectedKeys) ? [] : SelectedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        null, Math.Max(1, ContactPage), 50);

    private static decimal? ParseMoney(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    public string PageUrl(int page, string? paymentStatus = null, string? validation = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) query[name] = value; }
        Add("companyId", CompanyId?.ToString(CultureInfo.InvariantCulture)); Add("project", Project); Add("unit", Unit); Add("customer", Customer);
        Add("month", Month); Add("dueFrom", DueFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Add("dueTo", DueTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("paymentStatus", paymentStatus ?? PaymentStatus); Add("minRemaining", MinRemaining); Add("maxRemaining", MaxRemaining);
        Add("reminderType", ReminderType); Add("validationStatus", validation ?? ValidationStatus); Add("reason", Reason);
        query["page"] = page.ToString(CultureInfo.InvariantCulture);
        return $"/Collections/Review?{query}";
    }

    public static string Money(decimal? amount) => amount?.ToString("N2", CultureInfo.InvariantCulture) ?? "—";

    public static string Label(string value) => value switch
    {
        "CurrentMonth" => "Current Month", "FollowUp" => "Follow Up", "LegalNotice" => "Legal Notice", "LegalCase" => "Legal Case",
        "NeedsReview" => "Needs Review", "AlreadySent" => "Already Sent", "PartiallyPaid" => "Partially Paid",
        "UploadedToGenesys" => "Uploaded to Genesys", "UnknownOutcome" => "Unconfirmed", "CompletedWithErrors" => "Completed with errors",
        "ReviewRequired" => "Review required", _ => value
    };

    private static string Describe(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden => "Your account does not have permission for this Collections operation.",
        ApiOutcome.Unauthorized => "Your session has expired. Sign in again.",
        ApiOutcome.Unreachable => "The Tiger CS API could not be reached. Nothing was changed.",
        _ => detail ?? "The request could not be completed."
    };
}
