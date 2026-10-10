using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

public sealed record CrmOwnersRefreshResult(bool Succeeded, int Fetched, int Stored, int Rejected, int Unlinked, string? Message);

/// <summary>
/// Loads the bulk CRM owner feed in the background: pages the gateway, keeps only eligible rows (buyer relation, configured Sold / Contract lead statuses,
/// never a cancelled lead, a unit key that can be built), normalises phone / e-mail with the application's single normaliser and stores a complete run.
/// A failed or empty read keeps the previous data and is recorded. Nothing is matched by customer name or by phone.
/// </summary>
public sealed class CollectionsCrmOwnersRefreshService(
    CollectionsCrmOwnersOptions options, ICrmUnitOwnersGateway gateway, ICollectionsCrmOwnerStore store, ILogger<CollectionsCrmOwnersRefreshService> logger)
{
    public async Task<CrmOwnersRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new(false, 0, 0, 0, 0, "The CRM owner feed is disabled.");
        var fetched = 0; var rejected = 0;
        var rows = new List<CrmOwnerRow>();
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
                var row = ToRow(owner, eligible);
                if (row is null) rejected++; else rows.Add(row);
            }
            if (result.Owners.Count < pageSize) break;
        }
        if (rows.Count == 0) return await FailAsync("CRM returned no eligible owner at all; the previous data was kept.", cancellationToken);
        try
        {
            var linked = await store.ReplaceAsync(rows, cancellationToken);
            logger.LogInformation("CRM owners loaded: {Fetched} fetched, {Stored} stored, {Rejected} not eligible, {Unlinked} without a company.", fetched, rows.Count, rejected, rows.Count - linked);
            return new(true, fetched, rows.Count, rejected, rows.Count - linked, null);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning("CRM owners could not be stored ({ExceptionType}).", ex.GetType().Name);
            return await FailAsync("The CRM owners could not be stored.", cancellationToken);
        }
    }

    private async Task<CrmOwnersRefreshResult> FailAsync(string message, CancellationToken cancellationToken)
    {
        logger.LogWarning("CRM owner load failed: {Message}", message);
        try { await store.RecordFailureAsync(message, cancellationToken); } catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException) { }
        return new(false, 0, 0, 0, 0, message);
    }

    /// <summary>Null when the relation is not an owner we may use: not a buyer, a status that is not a sale, a cancelled lead, no unit key, a cancelled / invalid unit.</summary>
    public CrmOwnerRow? ToRow(CrmUnitOwnerDto owner, IReadOnlySet<int>? eligible = null)
    {
        eligible ??= options.EligibleLeadStatuses.ToHashSet();
        if (owner.CustomerType != options.BuyerCustomerType || !CrmSaleEligibility.IsEligible(owner.LeadStatus, owner.LeadStatusName, eligible)) return null;
        var key = CollectionsUnitKey.FromCrm(owner.ProjectCode, owner.UnitNumber);
        if (key is null || !CollectionsUnitKey.IsListable(key) || owner.UnitNumber!.Trim().TrimStart('0').Length == 0) return null; // unit 0 / 00 is no unit
        var name = !string.IsNullOrWhiteSpace(owner.FullNameEnglish) ? owner.FullNameEnglish!.Trim() : owner.FullNameArabic?.Trim() ?? "";
        var mobile = owner.MobileNumber?.Trim() ?? ""; var email = owner.Email?.Trim() ?? "";
        return new(key, owner.ProjectCode!.Trim(), owner.UnitNumber!.Trim(), owner.LeadId, owner.LeadStatus, owner.CustomerId, owner.UnitId, owner.ProjectId, name, mobile, email,
            CollectionsContactNormalizer.NormalizePhone(mobile), CollectionsContactNormalizer.NormalizeEmail(email));
    }
}
