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
using TigerCS.Domain.Modules.Collections.Review;

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
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null)
    {
        var total = Stopwatch.StartNew();
        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReadFinancials || (forExport && !permissions.CanSendReminders))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.Disabled);
        var date = businessDate ?? clock.BusinessDate;
        if (!CollectionsEnums.TryParse<CollectionsCampaignStage>(stage, out var selected)
            || date.Year is < 2000 or > 2100 || companyId is not (null or 4 or 32)
            || search?.Length > 200 || page < 1 || pageSize is < 1 or > 100 || towerId is <= 0 || minTotal < 0 || minTotal > sourceOptions.MaxMinOutstandingAmount
            || (long)(page - 1) * pageSize > int.MaxValue)
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Choose a campaign stage, a date in 2000-2100, company 4 or 32, a minimum amount of 0 or more, page >= 1 and pageSize 1-100; search is limited to 200 characters.");
        // "Minimum Total": a unit is listed only when its Due + Overdue is GREATER than this (null = no minimum). It is applied to the unit's sum - never to single
        // instalments - before the counts, the pages and both CSV exports, so all of them agree.
        decimal? unitMinTotal = minTotal is { } requested ? decimal.Round(requested, 4) : null;
        const decimal min = 0m;   // no per-instalment minimum any more
        var today = clock.BusinessDate;   // the real Dubai date: Due = due today, Overdue = before it; nothing later is ever listed

        // Instalment due-date window. Defaults: the configured receivables StartDate through the preview date, except the
        // whole-month stages (current month, follow-up), which default to the end of the preview month so upcoming
        // instalments of that month are not cut off. The preview date still alone drives stage scheduling/eligibility;
        // the window only limits which instalments are read.
        var to = dateTo ?? CollectionsCampaignPolicy.DefaultDateTo(selected, date);
        if (to > today) to = today;   // future instalments are not part of any list, total or export
        // Lower bound = the configured receivables StartDate (not 1 January of the preview year): overdue and legal
        // stages look back months, so a calendar-year floor would silently empty them from January onwards.
        var configuredStart = DateOnly.FromDateTime(sourceOptions.StartDate);
        if (dateFrom is null && configuredStart > to)
            // No calendar-year fallback: instalments before the configured start date are out of scope, so a window that ends
            // before it has nothing to read.
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"The window ends before the configured receivables start date ({configuredStart:yyyy-MM-dd}); nothing earlier is in scope. Choose a later preview date, or set From date explicitly.");
        var from = dateFrom ?? configuredStart;
        if (!CollectionsDateRanges.IsSupported(from, to))
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                "From date must not be after To date (instalments due after today are not listed), and both must be within 2000-2100.");

        // Search: the same term semantics for both evaluation paths (name, customer id, unit code, normalised phone / e-mail, phone digits).
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var digits = term is null ? "" : new string(term.Where(char.IsAsciiDigit).ToArray());
        var phoneDigits = term is not null && digits.Length >= 3 && !term.Any(char.IsLetter) ? digits : null;
        var (stageFrom, stageToExclusive) = CollectionsCampaignPolicy.StageRange(selected, date);
        var exportLimit = Math.Clamp(campaignOptions.MaxExportRows, 1, 50000);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));
        PactCampaignPage? sqlPage = null;
        PactReceivablesSnapshot? snapshot = null;
        var started = Stopwatch.GetTimestamp();
        var scope = companyId is { } c ? $"company {c}" : "companies 4 and 32";
        try
        {
            // The data store evaluates the campaign itself when it can (filtering, aggregation, review flags, totals, paging): only the requested
            // page - or, for an export, the bounded full result - reaches the application. Otherwise (a source without that capability, or a
            // snapshot the engine cannot use) every instalment of the window is read and evaluated in memory, as before.
            if (source is IPactCampaignSource campaign)
            {
                var request = new PactCampaignRequest(from, to, min, stageFrom, stageToExclusive, CollectionsCampaignPolicy.Threshold(selected),
                    selected != CollectionsCampaignStage.LegalReferral, term, phoneDigits,
                    forExport ? 0 : (page - 1) * pageSize, forExport ? exportLimit + 1 : pageSize, companyId, towerId, today, unitMinTotal);
                sqlPage = await campaign.ReadCampaignAsync(request, budget.Token);
                if (!sqlPage.Supported) sqlPage = null;
            }
            // Company and window are pushed down to the source: a single-company request must not wait for the other
            // company's (much slower) procedure, and rows outside the window are never read.
            if (sqlPage is null)
                snapshot = await source.ReadAsync(new PactReceivablesRequest(from, to, companyId, towerId, MinAmount: min), budget.Token);
        }
        catch (PactReceivablesScopeException ex)
        {
            return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message);
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

        var sourceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var snapshotStatus = sqlPage?.Snapshot ?? snapshot!.Snapshot;
        var readAtUtc = sqlPage?.ReadAtUtc ?? snapshot!.ReadAtUtc;
        logger.LogInformation("Campaign preview {Stage} ({Scope}, {From:yyyy-MM-dd}..{To:yyyy-MM-dd}) read {Rows} {What} in {ElapsedMs} ms ({Engine}).",
            selected, scope, from, to, sqlPage?.Units.Count ?? snapshot!.Items.Count, sqlPage is null ? "source rows" : "units", sourceMs, sqlPage is null ? "in memory" : "sql");

        var dates = CollectionsCampaignPolicy.ScheduledDates(selected, date);
        var cycle = $"{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{selected}";
        // Local snapshot: every company in scope must be loaded and within the documented maximum age
        // (ReceivablesSnapshotOptions.MaxAgeMinutes). Other sources keep the generic staleness test.
        var fresh = snapshotStatus is { } status
            ? status.IsFresh
            : readAtUtc.Kind == DateTimeKind.Utc && !clock.IsStale(readAtUtc) && readAtUtc <= clock.UtcNow.AddMinutes(1);
        var coverageIncomplete = snapshotStatus is { RangeCovered: false };
        CollectionsCampaignContactDto ToContact(CampaignUnitFacts f)
        {
            var (unitStatus, reasons) = Compose(f.Flags, fresh, coverageIncomplete, selected, date, dates);
            var identity = $"{f.CompanyId}:{f.TenantId}:{f.UnitId}:{f.UnitCode}:{cycle}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return new CollectionsCampaignContactDto(id, $"ext:Pact:{f.TenantId}", f.CompanyId, f.TenantId, f.FullName, f.Phone, f.Email,
                f.UnitId, f.UnitCode, f.ProjectCode, f.Amount, sourceOptions.Currency, f.EarliestDue, selected.ToString(), cycle,
                unitStatus, string.Join(";", reasons.Count == 0 ? ["Qualifies"] : reasons), f.TowerNumber, f.TowerName, f.DueAmount, f.OverdueAmount);
        }

        int count, ready, review;
        List<CollectionsCampaignContactDto> items;
        if (sqlPage is not null)
        {
            if (sqlPage.BadIdentityRows > 0)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT returned a receivable without a valid company/customer identity.");
            if (forExport && sqlPage.Total > exportLimit)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "The export exceeds the row limit. Filter by company or customer; no partial file was created.");
            // Every unit-level reason makes a unit NeedsReview whatever the global gates are; a unit without one gets the status a hypothetical clean
            // unit would get, so Ready / NeedsReview counts are exact over the whole filtered set without materialising it.
            var cleanStatus = Compose(0, fresh, coverageIncomplete, selected, date, dates).Status;
            count = sqlPage.Total;
            ready = cleanStatus == "Ready" ? sqlPage.Clean : 0;
            review = sqlPage.Review + (cleanStatus == "NeedsReview" ? sqlPage.Clean : 0);
            items = sqlPage.Units.Select(ToContact).ToList();
        }
        else
        {
            List<CampaignUnitFacts> facts;
            try
            {
                facts = BuildFactsInMemory(snapshot!.Items, from, to, min, selected, date, term, phoneDigits, companyId, today, unitMinTotal);
            }
            catch (OverflowException)
            {
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "The campaign contains amounts outside the supported range.");
            }
            catch (InvalidDataException)
            {
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.FinanceUnavailable,
                    "PACT returned a receivable without a valid company/customer identity.");
            }
            if (forExport && facts.Count > exportLimit)
                return CollectionsResult<CollectionsCampaignPreviewDto>.Fail(CollectionsOutcome.InvalidRequest,
                    "The export exceeds the row limit. Filter by company or customer; no partial file was created.");
            var contacts = facts.Select(ToContact).ToList();
            count = contacts.Count;
            ready = contacts.Count(x => x.Status == "Ready");
            review = contacts.Count(x => x.Status == "NeedsReview");
            items = forExport ? contacts : contacts.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        return CollectionsResult<CollectionsCampaignPreviewDto>.Ok(new(date, clock.BusinessDate, readAtUtc,
            "PACT receivables (companies 4 and 32)", selected.ToString(), cycle, dates, dates.Contains(date),
            campaignOptions.FinancialSourceValidated, sqlPage is null && snapshot!.LegacyExclusionsApplied, permissions.CanSendReminders,
            count, ready, review, page, pageSize, items,
            from, to, CollectionsCampaignPolicy.RangeNotes(selected, date, from, to), towerId, snapshotStatus, min,
            new ServerTimingsDto(sourceMs, total.Elapsed.TotalMilliseconds - sourceMs, total.Elapsed.TotalMilliseconds)));
    }

    /// <summary>Review reasons and status of a candidate from its unit-level flags and the global (source / schedule / release) gates. Order is part of the contract.</summary>
    private (string Status, List<string> Reasons) Compose(int flags, bool fresh, bool coverageIncomplete, CollectionsCampaignStage selected,
        DateOnly date, IReadOnlyList<DateOnly> dates)
    {
        bool Has(CollectionsCampaignFlags flag) => (flags & (int)flag) != 0;
        var reasons = new List<string>();
        if (Has(CollectionsCampaignFlags.AmbiguousInstalments)) reasons.Add("AmbiguousInstalments");
        if (Has(CollectionsCampaignFlags.AmountPrecisionNeedsReview)) reasons.Add("AmountPrecisionNeedsReview");
        if (Has(CollectionsCampaignFlags.MissingUnitIdentity)) reasons.Add("MissingUnitIdentity");
        if (Has(CollectionsCampaignFlags.ConflictingContactDetails)) reasons.Add("ConflictingContactDetails");
        if (Has(CollectionsCampaignFlags.UnitAllocationNeedsReview)) reasons.Add("UnitAllocationNeedsReview");
        if (Has(CollectionsCampaignFlags.ContradictoryPaymentStatus)) reasons.Add("ContradictoryPaymentStatus");
        if (sourceOptions.Currency != "AED") reasons.Add("CurrencyNeedsReview");
        if (!fresh) reasons.Add("StaleSource");
        if (coverageIncomplete) reasons.Add("CoverageIncomplete");
        if (!campaignOptions.FinancialSourceValidated) reasons.Add("SourceReconciliationRequired");
        if (Has(CollectionsCampaignFlags.NoValidContact)) reasons.Add("NoValidContact");
        var status = reasons.Count > 0 ? "NeedsReview" : "Ready";
        if (reasons.Count == 0 && (date != clock.BusinessDate || !dates.Contains(date)))
        { status = "PreviewOnly"; reasons.Add("OutsideSchedule"); }
        if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalNotice && !campaignOptions.LegalNoticeExportEnabled)
        { status = "NeedsReview"; reasons.Add("LegalNoticeReleaseRequired"); }
        if (reasons.Count == 0 && selected == CollectionsCampaignStage.LegalReferral)
        { status = "InternalReview"; reasons.Add("InternalLegalReferralOnly"); }
        return (status, reasons);
    }

    /// <summary>
    /// The in-memory evaluation: every instalment of the window is grouped per unit here. It is the reference implementation of the rules (the SQL engine
    /// must return the same units, flags, totals and order - see the equivalence tests) and the fallback for sources/snapshots without the SQL engine.
    /// </summary>
    internal List<CampaignUnitFacts> BuildFactsInMemory(IReadOnlyList<PactReceivableInstalment> items, DateOnly from, DateOnly to, decimal min,
        CollectionsCampaignStage selected, DateOnly date, string? term, string? phoneDigits, int? companyId, DateOnly? today = null, decimal? minTotal = null)
    {
        // Defensive: a legacy source may ignore the window.
        // Floating-point residue from the deployed procedures (e.g. 1E-12 on a settled instalment) is snapped to fils; a genuine
        // sub-fils amount stays as read so AmountPrecisionNeedsReview still fires. Rounding is never used to hide it.
        var rows = items
            .Select(r => MoneyNormalizer.Normalize(r.Amount, r.AmountIsFloatingPoint) is { IsResolved: true, Value: { } value } ? r with { Amount = value } : r)
            .Where(r => r.Amount > 0 && r.Amount >= min && DateOnly.FromDateTime(r.DueDate) >= from && DateOnly.FromDateTime(r.DueDate) <= to
                && !r.UnitCode.Contains('*')    // a '*' in the unit code marks a cancelled apartment (e.g. 513*): excluded before any grouping
                && r.UnitId is > 0 && r.UnitCode.Trim() is not ("" or "0"))   // units without a real number are never listed
            .ToList();
        if (rows.Any(r => r.CompanyId is not (4 or 32) || string.IsNullOrWhiteSpace(r.TenantId)))
            throw new InvalidDataException("PACT returned a receivable without a valid company/customer identity.");
        // The originals can repeat one instalment under different apartments of the same tenant.
        var ambiguousTenants = rows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim()))
            .Where(g => g.GroupBy(r => DateOnly.FromDateTime(r.DueDate))
                .Any(d => d.Select(r => (r.UnitId, Code: r.UnitCode.Trim())).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitId).Any(u => u.Select(r => r.UnitCode.Trim()).Distinct().Count() > 1)
                || g.GroupBy(r => r.UnitCode.Trim()).Any(u => u.Select(r => r.UnitId).Distinct().Count() > 1))
            .Select(g => g.Key).ToHashSet();
        var facts = rows.GroupBy(r => (r.CompanyId, Tenant: r.TenantId.Trim(), r.UnitId, Code: r.UnitCode.Trim()))
            .Select(g =>
            {
                var first = g.First();
                var amount = CollectionsCampaignPolicy.Evaluate(g.Select(r =>
                    new CampaignInstalment(DateOnly.FromDateTime(r.DueDate), r.Amount)), selected, date);
                if (amount.Reason is "NoQualifyingBalance" or "BelowThreshold") return null;
                // Due = due today, Overdue = due before it (the window never reaches past today). Nothing Due or Overdue: not listed; Minimum Total is on the unit's sum.
                var dueAmount = today is { } t1 ? g.Where(r => DateOnly.FromDateTime(r.DueDate) == t1).Sum(r => r.Amount) : 0m;
                var overdueAmount = today is { } t2 ? g.Where(r => DateOnly.FromDateTime(r.DueDate) < t2).Sum(r => r.Amount) : 0m;
                if (today is not null && dueAmount + overdueAmount <= 0) return null;
                if (minTotal is { } floor && dueAmount + overdueAmount <= floor) return null;
                var phone = CollectionsContactNormalizer.NormalizePhone(first.Mobile);
                var email = CollectionsContactNormalizer.NormalizeEmail(first.Email);
                var flags = CollectionsCampaignFlags.None;
                if (amount.Reason != "Qualifies") flags |= CollectionsCampaignFlags.AmbiguousInstalments;
                if (amount.Amount is { } value && value != decimal.Round(value, 2)) flags |= CollectionsCampaignFlags.AmountPrecisionNeedsReview;
                if (g.Key.UnitId is not > 0 || string.IsNullOrWhiteSpace(g.Key.Code) || g.Key.Code == "0")
                    flags |= CollectionsCampaignFlags.MissingUnitIdentity;
                if (g.Select(r => (r.FullName.Trim(), Phone: CollectionsContactNormalizer.NormalizePhone(r.Mobile),
                        Email: CollectionsContactNormalizer.NormalizeEmail(r.Email), r.ProjectCode)).Distinct().Count() > 1)
                    flags |= CollectionsCampaignFlags.ConflictingContactDetails;
                if (ambiguousTenants.Contains((g.Key.CompanyId, g.Key.Tenant))) flags |= CollectionsCampaignFlags.UnitAllocationNeedsReview;
                if (g.Any(r => string.Equals(r.SourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase)))
                    flags |= CollectionsCampaignFlags.ContradictoryPaymentStatus;
                if (selected != CollectionsCampaignStage.LegalReferral && phone.Length == 0 && email.Length == 0)
                    flags |= CollectionsCampaignFlags.NoValidContact;
                return new CampaignUnitFacts(first.CompanyId, g.Key.Tenant, first.FullName, phone, email, first.UnitId, g.Key.Code, first.ProjectCode,
                    amount.Amount, amount.EarliestDueDate, (int)flags, first.TowerNumber, first.TowerName, dueAmount, overdueAmount);
            }).OfType<CampaignUnitFacts>().ToList();
        if (companyId is { } company) facts = facts.Where(f => f.CompanyId == company).ToList();
        if (term is not null)
        {
            bool Match(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
            facts = facts.Where(c => Match(c.FullName) || Match(c.TenantId) || Match(c.UnitCode) || Match(c.Phone) || Match(c.Email) || Match(c.TowerNumber ?? "") || Match(c.TowerName ?? "")
                || (phoneDigits is not null && c.Phone.Contains(phoneDigits, StringComparison.Ordinal))).ToList();
        }
        return facts.OrderBy(c => c.CompanyId).ThenBy(c => c.TenantId, StringComparer.Ordinal)
            .ThenBy(c => c.UnitCode, StringComparer.Ordinal).ThenBy(c => c.UnitId).ToList();
    }

    public async Task<CollectionsResult<CollectionsCampaignExportDto>> ExportAsync(CollectionsCaller caller,
        string? stage, string? mode, DateOnly? businessDate = null, int? companyId = null, string? search = null,
        CancellationToken cancellationToken = default, DateOnly? dateFrom = null, DateOnly? dateTo = null, int? towerId = null, decimal? minTotal = null)
    {
        // No source read for an unauthorized export, including malformed requests.
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanSendReminders)
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.Forbidden);
        if (mode is not ("review" or "genesys"))
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose review or genesys export.");
        var result = await PreviewAsync(caller, stage, businessDate, companyId, search,
            forExport: true, cancellationToken: cancellationToken, dateFrom: dateFrom, dateTo: dateTo, towerId: towerId, minTotal: minTotal);
        if (!result.IsSuccess) return CollectionsResult<CollectionsCampaignExportDto>.Fail(result.Outcome, result.Detail);
        var report = result.Value!;
        // Freshness requirement (both export modes): the snapshot of every company in scope must be loaded and no older than
        // ReceivablesSnapshotOptions.MaxAgeMinutes. A failed latest refresh is covered by the same rule because the age keeps growing.
        // The whole requested From/To range must also be covered by the snapshot, otherwise the file would silently omit receivables.
        if (report.Snapshot is { IsReady: false } stale)
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"Receivables data is not ready to export. {stale.ReadyProblem} No file was created; once the data is loaded and fresh, retry.");
        if (mode == "genesys" && (report.Stage == nameof(CollectionsCampaignStage.LegalReferral)
            || report.BusinessDate != report.LiveBusinessDate || !report.IsScheduledDate
            || report.Items.Any(c => c.Status != "Ready") || report.Items.Count == 0))
            return CollectionsResult<CollectionsCampaignExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Genesys export needs a current scheduled date and every selected row ready. Review the list first; internal legal referrals cannot be sent to a customer campaign.");
        var csv = CollectionsCampaignCsv.Write(report, review: mode == "review");
        return CollectionsResult<CollectionsCampaignExportDto>.Ok(new(
            $"collections-{report.Stage}-{report.BusinessDate:yyyy-MM-dd}-{mode}.csv", csv, report.Items.Count));
    }
}

public static class CollectionsCampaignCsv
{
    public static string Write(CollectionsCampaignPreviewDto report, bool review)
    {
        var text = new StringBuilder();
        text.Append("RecordId,CustomerKey,CompanyId,TenantId,CustomerName,Phone,Email,UnitId,UnitCode,ProjectCode,Amount,Currency,DueDate,Stage,CycleKey,ReadAtUtc,Status,Reason,Use,VoiceEligible,SmsEligible,EmailEligible"  + "\r\n");
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
