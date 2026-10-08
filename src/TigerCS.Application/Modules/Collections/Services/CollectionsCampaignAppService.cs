using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>Read-only campaign preview and manual export. No scheduling, dispatch, ticket creation or legal referral.</summary>
public sealed class CollectionsCampaignAppService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsAuthorizationService authorization, CollectionsClock clock, IPactReceivablesSource source,
    ILogger<CollectionsCampaignAppService> logger)
{
    public async Task<CollectionsResult<CollectionsCampaignPreviewDto>> PreviewAsync(
        CollectionsCaller caller, string? stage, DateOnly? businessDate = null, int? companyId = null,
        string? search = null, int page = 1, int pageSize = 25, bool forExport = false,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReadFinancials || (forExport && !permissions.CanSendReminders))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Disabled);
        var date = businessDate ?? clock.BusinessDate;
        if (!CollectionsEnums.TryParse<CollectionsCampaignStage>(stage, out var selected)
            || date.Year is < 2000 or > 2100 || companyId is not (null or 4 or 32)
            || search?.Length > 200 || page < 1 || pageSize is < 1 or > 100
            || (long)(page - 1) * pageSize > int.MaxValue)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose a campaign stage, a date in 2000-2100, company 4 or 32, page >= 1 and pageSize 1-100; search is limited to 200 characters.");

        // Instalment due-date window. Defaults: 1 January of the preview year through the preview date, except the
        // whole-month stages (current month, follow-up), which default to the end of the preview month so upcoming
        // instalments of that month are not cut off. The preview date still alone drives stage scheduling/eligibility;
        // the window only limits which instalments are read.
        var from = dateFrom ?? new DateOnly(date.Year, 1, 1);
        var to = dateTo ?? CollectionsCampaignPolicy.DefaultDateTo(selected, date);
        if (from > to || from.Year < 2000 || to.Year > 2100)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "From date must not be after To date, and both must be within 2000-2100.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));
        PactReceivablesSnapshot snapshot;
        var started = Stopwatch.GetTimestamp();
        var scope = companyId is { } c ? $"company {c}" : "companies 4 and 32";
        try
        {
            // Company and window are pushed down to the source: a single-company request must not wait for the other
            // company's (much slower) procedure, and rows outside the window are never read.
            snapshot = await source.ReadAsync(new PactReceivablesRequest(from, to, companyId), budget.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Campaign preview {Stage} ({Scope}) was cancelled by the caller after {ElapsedMs} ms (client disconnected).",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}) exceeded the {Budget} s request budget after {ElapsedMs} ms; the caller was still waiting.",
                selected, scope, from, to, sourceOptions.RequestBudgetSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The PACT receivables read timed out. Please retry.");
        }
        catch (PactReceivablesSourceException ex)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}) source failure after {ElapsedMs} ms: {Reason}.",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Campaign preview {Stage} ({Scope}) source read failed after {ElapsedMs} ms ({ExceptionType}).",
                selected, scope, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name);
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The campaign source could not be read.");
        }

        logger.LogInformation("Campaign preview {Stage} ({Scope}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}) read {Rows} source rows in {ElapsedMs} ms.",
            selected, scope, from, to, snapshot.Items.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        // Defensive: a legacy source may ignore the window.
        var rows = snapshot.Items.Where(r => r.Amount > 0
            && DateOnly.FromDateTime(r.DueDate) >= from && DateOnly.FromDateTime(r.DueDate) <= to).ToList();
        if (rows.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "PACT returned a receivable without a valid company/customer identity.");
        // The originals can repeat one instalment under different apartments of the same tenant.
        var ambiguousTenants = rows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim()))
            .Where(g => g.GroupBy(r => DateOnly.FromDateTime(r.DueDate))
                .Any(d => d.Select(r => (r.UnitId, Code: r.UnitCode.Trim())).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitId).Any(u => u.Select(r => r.UnitCode.Trim()).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitCode.Trim()).Any(u => u.Select(r => r.UnitId).Distinct().Count() > 1))
            .Select(g => g.Key).ToHashSet();
        var dates = CollectionsCampaignPolicy.ScheduledDates(selected, date);
        var cycle = $"{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{selected}";
        var fresh = snapshot.ReadAtUtc.Kind == DateTimeKind.Utc && !clock.IsStale(snapshot.ReadAtUtc)
            && snapshot.ReadAtUtc <= clock.UtcNow.AddMinutes(1);
        List<CollectionsCampaignContactDto> contacts;
        try
        {
            contacts = rows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Code: r.UnitCode.Trim()))
                .Select(g =>
                {
                    var first = g.First();
                    var amount = CollectionsCampaignPolicy.Evaluate(g.Select(r =>
                        new CampaignInstalment(DateOnly.FromDateTime(r.DueDate), r.Amount)), selected, date);
                    if (amount.Reason is "NoQualifyingBalance" or "BelowThreshold") return null;
                    var phone = NormalizePhone(first.Mobile);
                    var email = NormalizeEmail(first.Email);
                    var reasons = new List<string>();
                    if (amount.Reason != "Qualifies") reasons.Add(amount.Reason);
                    if (amount.Amount is { } value && value != decimal.Round(value, 2)) reasons.Add("AmountPrecisionNeedsReview");
                    if (g.Key.UnitId is not > 0 || string.IsNullOrWhiteSpace(g.Key.Code) || g.Key.Code == "0")
                        reasons.Add("MissingUnitIdentity");
                    if (g.Select(r => (r.FullName.Trim(), Phone: NormalizePhone(r.Mobile), Email: NormalizeEmail(r.Email), r.ProjectCode))
                        .Distinct().Count() > 1) reasons.Add("ConflictingContactDetails");
                    if (ambiguousTenants.Contains((g.Key.CompanyId, g.Key.Tenant))) reasons.Add("UnitAllocationNeedsReview");
                    if (g.Any(r => string.Equals(r.SourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase))) reasons.Add("ContradictoryPaymentStatus");
                    if (sourceOptions.Currency != "AED") reasons.Add("CurrencyNeedsReview");
                    if (!fresh) reasons.Add("StaleSource");
                    if (!campaignOptions.FinancialSourceValidated) reasons.Add("SourceReconciliationRequired");
                    if (selected != CollectionsCampaignStage.LegalReferral && phone.Length == 0 && email.Length == 0)
                        reasons.Add("NoValidContact");
                    var status = reasons.Count > 0 ? "NeedsReview" : "Ready";
                    if (reasons.Count == 0 && (date != clock.BusinessDate || !dates.Contains(date)))
                    { status = "PreviewOnly"; reasons.Add("OutsideSchedule"); }
                    if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalNotice && !campaignOptions.LegalNoticeExportEnabled)
                    { status = "NeedsReview"; reasons.Add("LegalNoticeReleaseRequired"); }
                    if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalReferral)
                    { status = "InternalReview"; reasons.Add("InternalLegalReferralOnly"); }
                    var identity = $"{g.Key.CompanyId}:{g.Key.Tenant}:{g.Key.UnitId}:{g.Key.Code}:{cycle}";
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    return new CollectionsCampaignContactDto(id, $"ext:Pact:{g.Key.Tenant}", first.CompanyId,
                        g.Key.Tenant, first.FullName, phone, email, first.UnitId, g.Key.Code, first.ProjectCode,
                        amount.Amount, sourceOptions.Currency, amount.EarliestDueDate, selected.ToString(), cycle,
                        status, string.Join(";", reasons.Count == 0 ? ["Qualifies"] : reasons));
                }).OfType<CollectionsCampaignContactDto>().ToList();
        }
        catch (OverflowException)
        {
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                "The campaign contains amounts outside the supported range.");
        }
        if (companyId is { } company) contacts = contacts.Where(c => c.CompanyId == company).ToList();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            bool Match(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
            contacts = contacts.Where(c => Match(c.CustomerName) || Match(c.TenantId) || Match(c.UnitCode)
                || Match(c.Phone) || Match(c.Email)
                || (digits.Length >= 3 && !term.Any(char.IsLetter) && c.Phone.Contains(digits, StringComparison.Ordinal))).ToList();
        }
        contacts = contacts.OrderBy(c => c.CompanyId).ThenBy(c => c.TenantId, StringComparer.Ordinal)
            .ThenBy(c => c.UnitCode, StringComparer.Ordinal).ThenBy(c => c.UnitId).ToList();
        if (forExport && contacts.Count > Math.Clamp(campaignOptions.MaxExportRows, 1, 50000))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "The export exceeds the row limit. Filter by company or customer; no partial file was created.");
        return CollectionsResult<CollectionsCampaignPreviewDto>.Ok(new(date, clock.BusinessDate, snapshot.ReadAtUtc,
            "PACT receivables (companies 4 and 32)", selected.ToString(), cycle, dates, dates.Contains(date),
            campaignOptions.FinancialSourceValidated, snapshot.LegacyExclusionsApplied, permissions.CanSendReminders,
            contacts.Count, contacts.Count(c => c.Status == "Ready"), contacts.Count(c => c.Status == "NeedsReview"),
            page, pageSize, forExport ? contacts : contacts.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            from, to, CollectionsCampaignPolicy.RangeNotes(selected, date, from, to)));
    }

    public async Task<CollectionsResult<CollectionsCampaignExportDto>> ExportAsync(CollectionsCaller caller,
        string? stage, string? mode, DateOnly? businessDate = null, int? companyId = null, string? search = null,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null)
    {
        // No source read for an unauthorized export, including malformed requests.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanSendReminders)
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.Forbidden);
        if (mode is not ("review" or "genesys"))
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose review or genesys export.");
        var result = await PreviewAsync(caller, stage, businessDate, companyId, search,
            forExport: true, cancellationToken: cancellationToken, dateFrom: dateFrom, dateTo: dateTo);
        if (!result.IsSuccess) return CollectionsResult<CollectionsCampaignExportDto>.Fail(result.Outcome, result.Detail);
        var report = result.Value!;
        if (mode == "genesys" && (report.Stage == nameof(CollectionsCampaignStage.LegalReferral)
            || report.BusinessDate != report.LiveBusinessDate || !report.IsScheduledDate
            || report.Items.Any(c => c.Status != "Ready") || report.Items.Count == 0))
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Genesys export needs a current scheduled date and every selected row ready. Review the list first; internal legal referrals cannot be sent to a customer campaign.");
        var csv = CollectionsCampaignCsv.Write(report, review: mode == "review");
        return CollectionsResult<CollectionsCampaignExportDto>.Ok(new(
            $"collections-{report.Stage}-{report.BusinessDate:yyyy-MM-dd}-{mode}.csv", csv, report.Items.Count));
    }

    internal static string NormalizePhone(string value)
    {
        var compact = new string(value.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal)) compact = "+" + compact[2..];
        if (compact.Length == 10 && compact.StartsWith("05", StringComparison.Ordinal)) compact = "+971" + compact[1..];
        if (compact.Length == 12 && compact.StartsWith("971", StringComparison.Ordinal)) compact = "+" + compact;
        return compact.StartsWith('+') && compact.Length is >= 9 and <= 16 && compact[1] != '0'
            && compact[1..].All(char.IsAsciiDigit) ? compact : "";
    }

    private static string NormalizeEmail(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim() ? address.Address : "";
}

public static class CollectionsCampaignCsv
{
    public static string Write(CollectionsCampaignPreviewDto report, bool review)
    {
        var text = new StringBuilder();
        text.Append("RecordId,CustomerKey,CompanyId,TenantId,CustomerName,Phone,Email,UnitId,UnitCode,ProjectCode,Amount,Currency,DueDate,Stage,CycleKey,ReadAtUtc,Status,Reason,Use,VoiceEligible,SmsEligible,EmailEligible\r\n");
        foreach (var c in report.Items)
        {
            var cells = new[] { c.RecordId, c.CustomerKey, c.CompanyId.ToString(CultureInfo.InvariantCulture), c.TenantId,
                c.CustomerName, c.Phone, c.Email, c.UnitId?.ToString(CultureInfo.InvariantCulture) ?? "", c.UnitCode,
                c.ProjectCode, c.Amount?.ToString("0.00##########################", CultureInfo.InvariantCulture) ?? "",
                c.Currency, c.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", c.Stage, c.CycleKey,
                report.ReadAtUtc.ToString("O", CultureInfo.InvariantCulture), c.Status, c.Reason,
                review ? "InternalReviewOnly" : "GenesysCampaign", !review && c.VoiceEligible ? "true" : "false",
                !review && c.SmsEligible ? "true" : "false", !review && c.EmailEligible ? "true" : "false" };
            text.Append(string.Join(',', cells.Select((v, index) => Cell(v, review, isPhone: index == 5)))).Append("\r\n");
        }
        return text.ToString();
    }

    private static string Cell(string value, bool review, bool isPhone)
    {
        // Review files are opened in spreadsheets. Machine imports preserve validated E.164 phone numbers.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'
            && (review || !isPhone || !(trimmed.StartsWith('+') && trimmed[1..].All(char.IsAsciiDigit)))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
