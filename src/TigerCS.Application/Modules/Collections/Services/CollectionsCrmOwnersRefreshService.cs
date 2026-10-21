using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

public sealed record CrmOwnersRefreshResult(bool Succeeded, int Fetched, int Stored, int Rejected, int Unlinked, string? Message, IReadOnlyDictionary<string, int>? RejectedByReason = null);

/// <summary>Why a CRM relation is not stored as an owner (counted per refresh so a missing unit can be explained, not guessed).</summary>
public static class CrmOwnerRejection
{
    public const string NotBuyer = "NotBuyer";
    public const string NotSaleStatus = "NotSoldOrContractOrCancelled";
    public const string NoProjectCode = "NoProjectCode";
    public const string NoUnitNumber = "NoUnitNumber";
    public const string UnitNotListable = "UnitZeroOrCancelled";
}

/// <summary>
/// Loads the bulk CRM owner feed in the background: pages the gateway, keeps only eligible rows (buyer relation, configured Sold / Contract lead statuses,
/// never a cancelled lead, a unit key that can be built), normalises phone / e-mail with the application's single normaliser and stores a complete run.
/// A failed or empty read keeps the previous data and is recorded. Nothing is matched by customer name or by phone.
/// </summary>
public sealed class CollectionsCrmOwnersRefreshService(
    CollectionsCrmOwnersOptions options, ICrmUnitOwnersGateway gateway, ICollectionsCrmOwnerStore store, ILogger<CollectionsCrmOwnersRefreshService> logger,
    ICollectionsCrmProjectMap? projectMap = null)
{
    public async Task<CrmOwnersRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new(false, 0, 0, 0, 0, "The CRM owner feed is disabled.");
        var fetched = 0;
        var rows = new List<CrmOwnerRow>();
        var reasons = new Dictionary<string, int>();
        var withoutCode = new List<CrmUnitOwnerDto>();   // an eligible sale whose CRM project has no PACT code in the feed: the verified project map may know it
        var eligible = options.EligibleLeadStatuses.ToHashSet();
        var pageSize = Math.Clamp(options.PageSize, 10, 5000);
        for (var page = 1; ; page++)
        {
            if (page > Math.Max(1, options.MaxPages)) return await FailAsync("The CRM owner feed did not end within the page limit.", cancellationToken);
            var result = await gateway.GetPageAsync(page, pageSize, cancellationToken);
            if (result.Outcome != CrmUnitOwnersOutcome.Success)
                return await FailAsync($"CRM owner feed {result.Outcome}{(string.IsNullOrWhiteSpace(result.Message) ? "" : ": " + result.Message)}", cancellationToken);
            foreach (var owner in result.Owners)
            {
                fetched++;
                var (row, reason) = Evaluate(owner, eligible, null);
                if (row is not null) rows.Add(row);
                else if (reason == CrmOwnerRejection.NoProjectCode && projectMap is not null) withoutCode.Add(owner);
                else reasons[reason!] = reasons.GetValueOrDefault(reason!) + 1;
            }
            if (result.Owners.Count < pageSize) break;
        }
        if (withoutCode.Count > 0)
        {
            var codes = await ProjectCodesAsync(withoutCode.Select(o => o.ProjectId).Distinct().ToList(), cancellationToken);
            foreach (var owner in withoutCode)
            {
                var (row, reason) = Evaluate(owner, eligible, codes);
                if (row is not null) rows.Add(row);
                else reasons[reason!] = reasons.GetValueOrDefault(reason!) + 1;
            }
        }
        var rejected = reasons.Values.Sum();
        if (rows.Count == 0) return await FailAsync("CRM returned no eligible owner at all; the previous data was kept.", cancellationToken);
        var note = rejected == 0 ? null : "Not stored: " + string.Join(", ", reasons.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {r.Value}"));
        try
        {
            var linked = await store.ReplaceAsync(rows, cancellationToken, note);
            logger.LogInformation("CRM owners loaded: {Fetched} fetched, {Stored} stored, {Rejected} not eligible, {Unlinked} without a company.", fetched, rows.Count, rejected, rows.Count - linked);
            return new(true, fetched, rows.Count, rejected, rows.Count - linked, null, reasons);
        }
        catch (CrmOwnerStorageException ex)
        {
            // The safe message is what the pages and CollectionsCrmOwnerState.LastError show; the connection details stay in the log.
            logger.LogWarning("CRM owners could not be stored: {Detail}", ex.LogDetail);
            return await FailAsync(ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning("CRM owners could not be stored ({ExceptionType}).", ex.GetType().Name);
            return await FailAsync(CrmOwnerStorageException.BuildSafeMessage("storing the owners", null, null, null, null, ex.GetType().Name), cancellationToken);
        }
    }

    private async Task<CrmOwnersRefreshResult> FailAsync(string message, CancellationToken cancellationToken)
    {
        logger.LogWarning("CRM owner load failed: {Message}", message);
        try { await store.RecordFailureAsync(message, cancellationToken); }
        catch (CrmOwnerStorageException ex) { logger.LogWarning("The CRM owner failure could not be recorded: {Detail}", ex.LogDetail); }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException) { logger.LogWarning("The CRM owner failure could not be recorded ({ExceptionType}).", ex.GetType().Name); }
        return new(false, 0, 0, 0, 0, message);
    }

    /// <summary>The verified PACT code of each CRM project: only a project with exactly ONE code is usable (several codes are an ambiguous mapping and nothing is chosen).</summary>
    private async Task<IReadOnlyDictionary<int, string>> ProjectCodesAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        try
        {
            foreach (var (projectId, mappings) in await projectMap!.GetAsync(projectIds, cancellationToken))
            {
                var distinct = mappings.Select(m => m.ProjectCode.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count == 1) result[projectId] = distinct[0];
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning("The CRM project map could not be read ({ExceptionType}); sales of projects without a code in the feed are not stored.", ex.GetType().Name);
        }
        return result;
    }

    /// <summary>Null when the relation is not an owner we may use: not a buyer, a status that is not a sale, a cancelled lead, no unit key, a cancelled / invalid unit.</summary>
    public CrmOwnerRow? ToRow(CrmUnitOwnerDto owner, IReadOnlySet<int>? eligible = null) => Evaluate(owner, eligible ?? options.EligibleLeadStatuses.ToHashSet(), null).Row;

    /// <summary>The owner row, or the reason (<see cref="CrmOwnerRejection"/>) it is not one. <paramref name="projectCodes"/> supplies the PACT code of a CRM project that the feed sent without one.</summary>
    internal (CrmOwnerRow? Row, string? Reason) Evaluate(CrmUnitOwnerDto owner, IReadOnlySet<int> eligible, IReadOnlyDictionary<int, string>? projectCodes)
    {
        if (owner.CustomerType != options.BuyerCustomerType) return (null, CrmOwnerRejection.NotBuyer);
        if (!CrmSaleEligibility.IsEligible(owner.LeadStatus, owner.LeadStatusName, eligible)) return (null, CrmOwnerRejection.NotSaleStatus);
        var code = owner.ProjectCode;
        if (string.IsNullOrWhiteSpace(code)) code = projectCodes is not null && projectCodes.TryGetValue(owner.ProjectId, out var mapped) ? mapped : null;
        if (string.IsNullOrWhiteSpace(code)) return (null, CrmOwnerRejection.NoProjectCode);
        if (string.IsNullOrWhiteSpace(owner.UnitNumber)) return (null, CrmOwnerRejection.NoUnitNumber);
        var key = CollectionsUnitKey.FromCrm(code, owner.UnitNumber);
        if (key is null || !CollectionsUnitKey.IsListable(key) || owner.UnitNumber.Trim().TrimStart('0').Length == 0) return (null, CrmOwnerRejection.UnitNotListable); // unit 0 / 00 is no unit
        var name = !string.IsNullOrWhiteSpace(owner.FullNameEnglish) ? owner.FullNameEnglish!.Trim() : owner.FullNameArabic?.Trim() ?? "";
        var mobile = owner.MobileNumber?.Trim() ?? ""; var email = owner.Email?.Trim() ?? "";
        return (new(key, code.Trim(), owner.UnitNumber.Trim(), owner.LeadId, owner.LeadStatus, owner.CustomerId, owner.UnitId, owner.ProjectId, name, mobile, email,
            CollectionsContactNormalizer.NormalizePhone(mobile), CollectionsContactNormalizer.NormalizeEmail(email), owner.ProjectName?.Trim() ?? ""), null);
    }
}
