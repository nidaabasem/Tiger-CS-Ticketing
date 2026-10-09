using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Approval and upload of reviewed records to Genesys outbound contact lists.
/// <list type="bullet">
/// <item>Confirm: authorizes, re-resolves the approved list and refuses it if it differs from what was shown, then stores a frozen
/// dispatch (idempotent by key; each record can be in only one live dispatch) and queues a job.</item>
/// <item>Execute (job): takes a lease, re-reads the current balances, excludes settled records, stops for review on any other change,
/// then uploads one batch per reminder type (at most 1,000 contacts each). A batch whose outcome is unknown is never re-sent;
/// it waits for reconciliation.</item>
/// </list>
/// "Uploaded to Genesys" means the contact is in the list. It never means the customer was called or contacted.
/// </summary>
public sealed class DispatchService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization, CollectionsClock clock,
    IReviewStore store, IReviewJobScheduler scheduler, IGenesysOutboundClient client,
    ReviewQueryService query, CurrentBalanceReader balances, ILogger<DispatchService> logger)
{
    public const string UploadDisclaimer =
        "Uploaded to Genesys means the contact was added to the outbound contact list. It does not mean the customer was called, " +
        "messaged or reached. If a Genesys campaign using that list is active it may start dialing at any time; call and message results " +
        "arrive separately from Genesys.";

    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------- confirm

    public async Task<CollectionsResult<DispatchDto>> ConfirmAsync(CollectionsCaller caller, ConfirmDispatchRequest request, CancellationToken ct)
    {
        // Authorization first: no data is read for an unauthorized caller.
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled);
        if (!genesys.Enabled || !genesys.LiveCustomerDispatchEnabled)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled,
                "Customer dispatch to Genesys is switched off in this environment. It stays off until the paid-after-upload suppression has been proven on a test contact list.");
        if (!genesys.SuppressionEnabled)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled,
                "Customer dispatch needs the paid-after-upload suppression sweep to be enabled; otherwise a customer who pays after upload could still be called.");
        var key = request.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "An idempotency key of 1-100 characters is required.");
        if (!request.AcknowledgeActiveCampaignRisk)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Confirm that you understand an active Genesys campaign may begin calling uploaded contacts immediately.");

        var existing = await store.FindDispatchByKeyAsync(key, ct);
        if (existing is not null) return Replay(existing, caller, request.ExpectedFingerprint);

        var resolved = await query.ResolveAsync(request.Selection, ct);
        if (resolved.Error is not null) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, resolved.Error);
        if (resolved.Records.Count == 0)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "No Ready records are selected.");
        var fingerprint = ReviewQueryService.Fingerprint(resolved.Records);
        if (resolved.Records.Count != request.ExpectedCount || !string.Equals(fingerprint, request.ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired,
                "The records, amounts or contacts changed since this list was shown. Review the current list and confirm again.");
        if (query.IsStale(resolved.Run!))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired, "The review data is stale. Refresh it and review again.");
        // Defence in depth: such records are already held back as Excluded, so this can only happen through a stale or tampered list.
        if (ReviewQueryService.SharedPhones(resolved.Records).Records > 0 || resolved.Records.Any(r => !IsVoiceEligible(r.Phone)))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired,
                "The list contains records that share a phone number or have no callable phone. Repeated calls are blocked pending a business decision; review the current list.");
        try { foreach (var type in resolved.Records.Select(r => r.ReminderType).Distinct()) _ = genesys.ContactListIdFor(type); }
        catch (InvalidOperationException ex) { return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message); }

        var now = clock.UtcNow;
        var dispatch = new CollectionsDispatch
        {
            IdempotencyKey = key, Fingerprint = fingerprint, Status = DispatchStatus.Queued, InitiatedByEmployeeId = caller.EmployeeId,
            InitiatedAtUtc = now, ReviewRunId = resolved.Run!.CollectionsReviewRunId, SelectionMode = request.Selection.Mode,
            FilterJson = JsonSerializer.Serialize(request.Selection.Filter), ApprovedCount = resolved.Records.Count,
            ApprovedTotalsJson = JsonSerializer.Serialize(ReviewQueryService.TotalsByCurrency(resolved.Records)
                .ToDictionary(p => p.Key, p => p.Value.ToString("0.00", CultureInfo.InvariantCulture))),
            AcknowledgedActiveCampaignRisk = true,
            Items = resolved.Records.Select(r => new CollectionsDispatchItem
            {
                RecordKey = r.RecordKey, ReminderType = r.ReminderType, CompanyId = r.CompanyId, TenantId = r.TenantId, UnitId = r.UnitId,
                UnitCode = r.UnitCode, CustomerName = r.CustomerName, Phone = r.Phone, Email = r.Email, Amount = r.RemainingAmount!.Value,
                Currency = r.Currency, DueDate = r.DueDate!.Value, Status = DispatchItemStatus.Approved, VoiceEligible = IsVoiceEligible(r.Phone)
            }).ToList()
        };
        if (!await store.TryAddDispatchAsync(dispatch, ct))
        {
            // Either the same key raced in (replay it) or a record is already in a live dispatch (a double click with another key).
            var winner = await store.FindDispatchByKeyAsync(key, ct);
            return winner is not null ? Replay(winner, caller, request.ExpectedFingerprint)
                : CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.DuplicateDispatch,
                    "One or more of these records are already in a pending or sent dispatch. Refresh the list; they now show as Already Sent.");
        }
        try { scheduler.EnqueueDispatch(dispatch.CollectionsDispatchId); }
        catch (Exception ex)
        {
            logger.LogError("Could not queue dispatch {DispatchId} ({ExceptionType}).", dispatch.CollectionsDispatchId, ex.GetType().Name);
            dispatch.Status = DispatchStatus.Failed; dispatch.StatusReason = "The background job could not be queued; nothing was sent.";
            await store.UpdateDispatchAsync(dispatch, ct);
            await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, "Dispatch could not be queued", ct);
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The send could not be queued. Nothing was sent; try again.");
        }
        return CollectionsResult<DispatchDto>.Ok(ToDto(dispatch), CollectionsOutcome.Accepted);
    }

    private CollectionsResult<DispatchDto> Replay(CollectionsDispatch existing, CollectionsCaller caller, string expectedFingerprint) =>
        existing.InitiatedByEmployeeId == caller.EmployeeId
        && string.Equals(existing.Fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase)
            ? CollectionsResult<DispatchDto>.Ok(ToDto(existing), CollectionsOutcome.Replayed)
            : CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.IdempotencyConflict, "This idempotency key was already used for a different approval.");

    // ---------------------------------------------------------------- read

    public async Task<CollectionsResult<DispatchDto>> GetAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        return dispatch is null ? CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound) : CollectionsResult<DispatchDto>.Ok(ToDto(dispatch));
    }

    public async Task<CollectionsResult<IReadOnlyList<DispatchDto>>> ListAsync(CollectionsCaller caller, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<IReadOnlyList<DispatchDto>>.Fail(CollectionsOutcome.Forbidden);
        return CollectionsResult<IReadOnlyList<DispatchDto>>.Ok((await store.ListDispatchesAsync(50, ct)).Select(ToDto).ToList());
    }

    // ---------------------------------------------------------------- execute (background job)

    public async Task ExecuteAsync(long dispatchId, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        if (!await store.TryAcquireLeaseAsync(dispatchId, owner, clock.UtcNow, LeaseTtl, ct))
        {
            logger.LogInformation("Dispatch {DispatchId} is finished, cancelled or being processed by another worker; skipping.", dispatchId);
            return;
        }
        var dispatch = await store.GetDispatchByIdAsync(dispatchId, ct);
        if (dispatch is null) return;

        try
        {
            if (dispatch.Status == DispatchStatus.Revalidating)
            {
                // Configuration may have changed between approval and execution: a disabled integration sends nothing.
                if (!options.Enabled || !genesys.Enabled || !genesys.LiveCustomerDispatchEnabled || !genesys.SuppressionEnabled)
                { await StopAsync(dispatch, DispatchStatus.Failed, "Customer dispatch or its suppression sweep is disabled; nothing was sent.", ct); return; }
                if (!await RevalidateAsync(dispatch, owner, ct)) return;
                if (!await PlanBatchesAsync(dispatch, ct)) return;
                dispatch = (await store.GetDispatchByIdAsync(dispatchId, ct))!;
            }
            if (dispatch.Status == DispatchStatus.Sending) await SendAsync(dispatch, owner, ct);
        }
        catch (LeaseLostException)
        {
            // Another worker owns the dispatch now (this one stalled past the lease). It must not write anything more.
            logger.LogWarning("Dispatch {DispatchId} lost its lease; this worker stopped without writing further changes.", dispatchId);
        }
    }

    private sealed class LeaseLostException : Exception;

    /// <summary>
    /// Runs slow, database-free work (the PACT reads) while renewing the lease on a timer, so a read that outlasts the lease cannot let a second worker
    /// start. The timer only touches the store while the work awaits the source, and is stopped (awaited) before the caller uses the store again.
    /// If the lease cannot be renewed the work is cancelled and <see cref="LeaseLostException"/> is thrown.
    /// </summary>
    private async Task<T> WithLeaseHeartbeatAsync<T>(long dispatchId, Guid owner, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stop = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Clamp(genesys.LeaseHeartbeatSeconds, 0.01, LeaseTtl.TotalSeconds / 2));
        var beat = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(interval, stop.Token);
                    if (!await store.RenewLeaseAsync(dispatchId, owner, clock.UtcNow, LeaseTtl, CancellationToken.None)) { await lost.CancelAsync(); return; }
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
        }, CancellationToken.None);
        try { return await work(lost.Token); }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested) { throw new LeaseLostException(); }
        finally
        {
            await stop.CancelAsync();
            await beat;
        }
    }

    /// <summary>Re-reads current balances. Settled records are excluded; any other change sends the whole list back for review.</summary>
    private async Task<bool> RevalidateAsync(CollectionsDispatch dispatch, Guid owner, CancellationToken ct)
    {
        var items = (await store.GetItemsAsync(dispatch.CollectionsDispatchId, ct)).Where(i => i.Status == DispatchItemStatus.Approved).ToList();
        var asOf = clock.BusinessDate;
        // Record identities and schedules are per business month: an approval from last month must be reviewed again.
        var run = await store.GetRunAsync(dispatch.ReviewRunId, ct);
        if (run is null || run.AsOfDate.Year != asOf.Year || run.AsOfDate.Month != asOf.Month)
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired, "The business month changed since approval. Nothing was sent; review the current list and approve again.", ct);

        var companies = items.Select(i => i.CompanyId).Distinct().Order().ToList();
        await store.SetDispatchPhaseAsync(dispatch.CollectionsDispatchId, $"Checking current balances (company {string.Join(" and ", companies)}); this can take several minutes", ct);
        CurrentBalanceReader.Result read;
        try
        {
            read = await WithLeaseHeartbeatAsync(dispatch.CollectionsDispatchId, owner, token => balances.ReadAsync(companies, [asOf], token), ct);
        }
        catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is PactReceivablesSourceException or DbException)
        {
            return await StopAsync(dispatch, DispatchStatus.Failed, "Current balances could not be read, so nothing was sent. Start the send again later.", ct);
        }
        // The lease may have been lost between the last heartbeat and now; do not write on someone else's dispatch.
        if (!await store.RenewLeaseAsync(dispatch.CollectionsDispatchId, owner, clock.UtcNow, LeaseTtl, ct)) throw new LeaseLostException();
        await store.SetRevalidationMsAsync(dispatch.CollectionsDispatchId, read.ElapsedMs, ct);
        var current = read.ByAsOfDate[asOf];

        var settled = new List<CollectionsDispatchItem>();
        var changed = 0;
        foreach (var item in items)
        {
            if (!current.TryGetValue(item.RecordKey, out var now))
            {
                item.Status = DispatchItemStatus.Excluded; item.StatusReason = "Paid or settled: no qualifying balance remains";
                settled.Add(item); continue;
            }
            var unchanged = now.ValidationStatus == ReviewValidationStatus.Ready && now.RemainingAmount == item.Amount
                && now.DueDate == item.DueDate && now.Currency == item.Currency && now.Phone == item.Phone
                && string.Equals(now.CustomerName, item.CustomerName, StringComparison.Ordinal);
            if (!unchanged) changed++;
        }
        if (changed > 0)
        {
            await store.UpdateItemsAsync(settled, ct);
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired,
                $"{changed} approved record(s) changed (amount, due date, contact or validation) after approval. Nothing was sent; review the current list and approve again.", ct,
                excluded: settled.Count);
        }
        if (settled.Count > 0) await store.UpdateItemsAsync(settled, ct);
        dispatch.ExcludedAtDispatchCount = settled.Count;
        if (settled.Count == items.Count)
        {
            dispatch.Status = DispatchStatus.Completed; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.StartedAtUtc ??= clock.UtcNow;
            dispatch.StatusReason = "Every approved record was paid or settled before sending; nothing was uploaded.";
            dispatch.Phase = "Finished"; dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
            await store.UpdateDispatchAsync(dispatch, ct);
            return false;
        }
        return true;
    }

    private async Task<bool> StopAsync(CollectionsDispatch dispatch, DispatchStatus status, string reason, CancellationToken ct, int? excluded = null)
    {
        dispatch.Status = status; dispatch.StatusReason = reason; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.Phase = "Finished";
        dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
        if (excluded is { } e) dispatch.ExcludedAtDispatchCount = e;
        await store.UpdateDispatchAsync(dispatch, ct);
        await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, reason, ct);
        return false;
    }

    /// <summary>Splits the surviving records into one batch list per reminder type, at most BatchSize per request, and stores the plan before any upload.</summary>
    private async Task<bool> PlanBatchesAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        var all = (await store.GetItemsAsync(dispatch.CollectionsDispatchId, ct)).Where(i => i.Status == DispatchItemStatus.Approved).ToList();
        // callable is set only for approved voice-eligible contacts; anything else is never uploaded.
        var ineligible = all.Where(i => !i.VoiceEligible || !IsVoiceEligible(i.Phone)).ToList();
        foreach (var item in ineligible) { item.Status = DispatchItemStatus.Excluded; item.StatusReason = "Not voice eligible: no valid international phone number"; }
        if (ineligible.Count > 0) await store.UpdateItemsAsync(ineligible, ct);
        var items = all.Except(ineligible).ToList();
        if (items.Count == 0)
        {
            dispatch.Status = DispatchStatus.Completed; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.Phase = "Finished";
            dispatch.StatusReason = "No approved record was voice eligible; nothing was uploaded.";
            dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
            await store.UpdateDispatchAsync(dispatch, ct);
            return false;
        }
        var batchSize = genesys.EffectiveBatchSize;
        var batches = new List<CollectionsGenesysBatch>();
        var plan = new List<(CollectionsGenesysBatch Batch, List<CollectionsDispatchItem> Items)>();
        var sequence = 0;
        foreach (var group in items.GroupBy(i => i.ReminderType).OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(i => i.RecordKey, StringComparer.Ordinal).ToList();
            for (var offset = 0; offset < ordered.Count; offset += batchSize)
            {
                var slice = ordered.Skip(offset).Take(batchSize).ToList();
                var batch = new CollectionsGenesysBatch
                {
                    CollectionsDispatchId = dispatch.CollectionsDispatchId, ReminderType = group.Key, ContactListId = genesys.ContactListIdFor(group.Key),
                    Sequence = ++sequence, ContactCount = slice.Count, Status = GenesysBatchStatus.Pending
                };
                batches.Add(batch); plan.Add((batch, slice));
            }
        }
        await store.AddBatchesAsync(batches, ct);
        foreach (var (batch, slice) in plan)
            for (var i = 0; i < slice.Count; i++) { slice[i].CollectionsGenesysBatchId = batch.CollectionsGenesysBatchId; slice[i].BatchPosition = i; }
        await store.UpdateItemsAsync(items, ct);
        dispatch.Status = DispatchStatus.Sending; dispatch.StartedAtUtc = clock.UtcNow; dispatch.Phase = $"Uploading {batches.Count} batch(es)";
        await store.UpdateDispatchAsync(dispatch, ct);
        return true;
    }

    private async Task SendAsync(CollectionsDispatch dispatch, Guid owner, CancellationToken ct)
    {
        foreach (var planned in dispatch.Batches.OrderBy(b => b.Sequence))
        {
            if (!await store.RenewLeaseAsync(dispatch.CollectionsDispatchId, owner, clock.UtcNow, LeaseTtl, ct))
            { logger.LogWarning("Dispatch {DispatchId} lost its lease; stopping.", dispatch.CollectionsDispatchId); return; }
            await store.SetDispatchPhaseAsync(dispatch.CollectionsDispatchId, $"Uploading batch {planned.Sequence} of {dispatch.Batches.Count}", ct);
            var batch = await store.GetBatchAsync(planned.CollectionsGenesysBatchId, ct);
            if (batch is null) continue;
            if (batch.Status == GenesysBatchStatus.Submitting)
            {
                // A previous worker died between claiming and recording the answer: the outcome is unknown. Never resend.
                batch.Status = GenesysBatchStatus.UnknownOutcome; batch.Error = "The worker stopped before the Genesys response was recorded.";
                batch.CompletedAtUtc = clock.UtcNow;
                await store.UpdateBatchAsync(batch, ct);
                await MarkItemsAsync(batch, DispatchItemStatus.UnknownOutcome, batch.Error, null, ct);
                continue;
            }
            if (batch.Status != GenesysBatchStatus.Pending) continue;
            if (!await store.TryClaimBatchAsync(batch.CollectionsGenesysBatchId, clock.UtcNow, ct)) continue;
            batch = (await store.GetBatchAsync(batch.CollectionsGenesysBatchId, ct))!;
            await SubmitAsync(batch, ct);
        }

        var final = await store.GetDispatchByIdAsync(dispatch.CollectionsDispatchId, ct);
        if (final is null) return;
        var items = await store.GetItemsAsync(final.CollectionsDispatchId, ct);
        var allUploaded = items.Where(i => i.Status != DispatchItemStatus.Excluded).All(i => i.Status == DispatchItemStatus.UploadedToGenesys);
        final.Status = allUploaded ? DispatchStatus.Completed : DispatchStatus.CompletedWithErrors;
        final.StatusReason = allUploaded ? "All approved contacts were uploaded to Genesys. No call or message has been confirmed."
            : "Some batches failed or have an unconfirmed outcome. Unconfirmed batches must be reconciled in Genesys before anything is re-sent.";
        final.CompletedAtUtc = clock.UtcNow; final.Phase = "Finished"; final.LeaseOwner = null; final.LeaseExpiresAtUtc = null;
        await store.UpdateDispatchAsync(final, ct);
    }

    private async Task SubmitAsync(CollectionsGenesysBatch batch, CancellationToken ct)
    {
        var items = (await store.GetBatchItemsAsync(batch.CollectionsGenesysBatchId, ct)).OrderBy(i => i.BatchPosition).ToList();
        var payloads = items.Select(i => ToPayload(i, batch.ContactListId, genesys)).ToList();
        batch.RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloads))));
        GenesysUploadResult result;
        try { result = await client.UploadContactsAsync(batch.ContactListId, payloads, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = new(GenesysUploadOutcome.Unknown, null, [], "The job was cancelled during the request."); }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys upload for batch {BatchId} threw {ExceptionType}.", batch.CollectionsGenesysBatchId, ex.GetType().Name);
            result = new(GenesysUploadOutcome.Unknown, null, [], "The request ended without a usable response.");
        }

        batch.HttpStatus = result.HttpStatus; batch.CompletedAtUtc = clock.UtcNow; batch.Error = Truncate(result.Error);
        batch.ReturnedContactCount = result.ContactIds.Count;
        switch (result.Outcome)
        {
            case GenesysUploadOutcome.Accepted:
                var complete = result.ContactIds.Count == items.Count;
                batch.Status = complete ? GenesysBatchStatus.Uploaded : GenesysBatchStatus.UnknownOutcome;
                if (!complete) batch.Error = "Genesys accepted the request but returned a different number of contacts; check the list.";
                var now = clock.UtcNow;
                for (var i = 0; i < items.Count; i++)
                {
                    var id = i < result.ContactIds.Count ? result.ContactIds[i] : null;
                    items[i].Status = id is null ? DispatchItemStatus.UnknownOutcome : DispatchItemStatus.UploadedToGenesys;
                    items[i].GenesysContactId = id; items[i].UploadedAtUtc = id is null ? null : now;
                    items[i].StatusReason = id is null ? batch.Error : null;
                }
                await store.UpdateItemsAsync(items, ct);
                break;
            case GenesysUploadOutcome.Rejected:
                batch.Status = GenesysBatchStatus.Failed;
                foreach (var item in items) { item.Status = DispatchItemStatus.Failed; item.StatusReason = batch.Error; }
                await store.UpdateItemsAsync(items, ct);
                break;
            default:
                batch.Status = GenesysBatchStatus.UnknownOutcome;
                foreach (var item in items) { item.Status = DispatchItemStatus.UnknownOutcome; item.StatusReason = batch.Error; }
                await store.UpdateItemsAsync(items, ct);
                break;
        }
        await store.UpdateBatchAsync(batch, ct);
    }

    private async Task MarkItemsAsync(CollectionsGenesysBatch batch, DispatchItemStatus status, string? reason, string? contactId, CancellationToken ct)
    {
        var items = await store.GetBatchItemsAsync(batch.CollectionsGenesysBatchId, ct);
        foreach (var item in items) { item.Status = status; item.StatusReason = reason; item.GenesysContactId ??= contactId; }
        await store.UpdateItemsAsync(items, ct);
    }

    /// <summary>The exact contact object posted to Genesys: the list id, the six data fields as strings, and callable (true only for approved voice-eligible contacts).</summary>
    public static GenesysContactPayload ToPayload(CollectionsDispatchItem item, string contactListId, GenesysOutboundOptions settings) => new(
        contactListId, item.Phone, item.CustomerName, item.Email, settings.LabelFor(item.ReminderType),
        MoneyNormalizer.Format(item.Amount), item.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Callable: item.VoiceEligible && IsVoiceEligible(item.Phone));

    /// <summary>A callable contact needs a valid international (E.164) number.</summary>
    public static bool IsVoiceEligible(string? phone) => !string.IsNullOrEmpty(phone) && System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+[1-9]\d{7,14}$");

    private static string? Truncate(string? text) => text is { Length: > 400 } ? text[..400] : text;

    // ---------------------------------------------------------------- audit export

    /// <summary>The contacts of a dispatch exactly as sent to Genesys (the six contact-list columns), for comparison with the Genesys list.</summary>
    public async Task<CollectionsResult<string>> ExportContactsAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<string>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        if (dispatch is null) return CollectionsResult<string>.Fail(CollectionsOutcome.NotFound);
        var lists = dispatch.Batches.ToDictionary(b => b.CollectionsGenesysBatchId, b => b.ContactListId);
        var sent = dispatch.Items.Where(i => i.CollectionsGenesysBatchId is not null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released)
            .OrderBy(i => i.CollectionsGenesysBatchId).ThenBy(i => i.BatchPosition)
            .Select(i => ToPayload(i, lists[i.CollectionsGenesysBatchId!.Value], genesys));
        return CollectionsResult<string>.Ok(GenesysContactTemplate.WriteCsv(sent));
    }

    // ---------------------------------------------------------------- cancel

    /// <summary>Cancels a dispatch that has not started (still Queued, for example because background jobs are off). Records are released.</summary>
    public async Task<CollectionsResult<DispatchDto>> CancelAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        if (dispatch is null) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound);
        if (!await store.TryCancelQueuedAsync(dispatch.CollectionsDispatchId, "Cancelled before sending by employee " + caller.EmployeeId, clock.UtcNow, ct))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Only a send that has not started can be cancelled.");
        await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, "Dispatch cancelled", ct);
        return CollectionsResult<DispatchDto>.Ok(ToDto((await store.GetDispatchAsync(dispatchId, ct))!));
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>
    /// Resolves a batch whose outcome is unknown, after a person checked the Genesys contact list.
    /// <c>ConfirmedUploaded</c>: the contacts are there; they stay blocked as already sent.
    /// <c>ConfirmedNotUploaded</c>: nothing was created; the records are released and may be approved again (with fresh validation).
    /// </summary>
    public async Task<CollectionsResult<DispatchDto>> ReconcileAsync(CollectionsCaller caller, Guid dispatchId, long batchId,
        ReconcileBatchRequest request, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Describe what you checked in Genesys (1-500 characters).");
        var uploaded = string.Equals(request.Resolution, "ConfirmedUploaded", StringComparison.OrdinalIgnoreCase);
        if (!uploaded && !string.Equals(request.Resolution, "ConfirmedNotUploaded", StringComparison.OrdinalIgnoreCase))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Resolution must be ConfirmedUploaded or ConfirmedNotUploaded.");
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        var batch = await store.GetBatchAsync(batchId, ct);
        if (dispatch is null || batch is null || batch.CollectionsDispatchId != dispatch.CollectionsDispatchId)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound);
        if (batch.Status != GenesysBatchStatus.UnknownOutcome)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Only a batch with an unconfirmed outcome can be reconciled.");

        batch.Status = uploaded ? GenesysBatchStatus.Uploaded : GenesysBatchStatus.ConfirmedNotUploaded;
        batch.ReconciledByEmployeeId = caller.EmployeeId; batch.ReconciledAtUtc = clock.UtcNow; batch.ReconciliationNote = request.Note.Trim();
        await store.UpdateBatchAsync(batch, ct);
        var items = await store.GetBatchItemsAsync(batchId, ct);
        foreach (var item in items.Where(i => i.Status == DispatchItemStatus.UnknownOutcome))
        {
            item.Status = uploaded ? DispatchItemStatus.UploadedToGenesys : DispatchItemStatus.Released;
            item.StatusReason = uploaded ? "Confirmed in Genesys by a reviewer (contact id not recorded)" : "Confirmed not uploaded by a reviewer";
            if (uploaded) item.UploadedAtUtc ??= batch.CompletedAtUtc;
        }
        await store.UpdateItemsAsync(items, ct);
        return CollectionsResult<DispatchDto>.Ok(ToDto((await store.GetDispatchAsync(dispatchId, ct))!));
    }

    // ---------------------------------------------------------------- mapping

    public static DispatchDto ToDto(CollectionsDispatch d)
    {
        var totals = string.IsNullOrEmpty(d.ApprovedTotalsJson) ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(d.ApprovedTotalsJson) ?? [];
        int Count(DispatchItemStatus s) => d.Items.Count(i => i.Status == s);
        int Count2(ContactSuppressionStatus s) => d.Items.Count(i => i.SuppressionStatus == s);
        return new(d.PublicId, d.Status.ToString(), d.StatusReason, d.InitiatedByEmployeeId, d.InitiatedAtUtc, d.StartedAtUtc, d.CompletedAtUtc,
            d.ApprovedCount, Count(DispatchItemStatus.UploadedToGenesys), Count(DispatchItemStatus.Excluded), Count(DispatchItemStatus.Failed),
            Count(DispatchItemStatus.UnknownOutcome),
            totals.ToDictionary(p => p.Key, p => decimal.Parse(p.Value, CultureInfo.InvariantCulture)),
            d.Batches.OrderBy(b => b.Sequence).Select(b => new DispatchBatchDto(b.CollectionsGenesysBatchId, b.ReminderType.ToString(), b.ContactListId,
                b.Sequence, b.ContactCount, b.Status.ToString(), b.AttemptCount, b.HttpStatus, b.ReturnedContactCount, b.Error, b.StartedAtUtc,
                b.CompletedAtUtc, b.ReconciledAtUtc, b.ReconciliationNote)).ToList(),
            UploadDisclaimer, d.Phase, d.RevalidationMs, Count2(ContactSuppressionStatus.Suppressed), Count2(ContactSuppressionStatus.Failed),
            d.Items.Count(i => i.Status == DispatchItemStatus.UploadedToGenesys && string.IsNullOrEmpty(i.GenesysContactId)));
    }
}
