using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

public class CollectionsReviewRunConfiguration : IEntityTypeConfiguration<CollectionsReviewRun>
{
    public void Configure(EntityTypeBuilder<CollectionsReviewRun> builder)
    {
        builder.ToTable("CollectionsReviewRuns");
        builder.HasKey(r => r.CollectionsReviewRunId);
        builder.Property(r => r.CollectionsReviewRunId).ValueGeneratedOnAdd();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(200).IsRequired();
        builder.Property(r => r.SourceProcedureSuffix).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(r => r.Phase).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Error).HasMaxLength(500);
        // Single-flight refresh and a single current run, enforced by the database.
        builder.HasIndex(r => r.IsActive).IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_CollectionsReviewRuns_Active");
        builder.HasIndex(r => r.IsCurrent).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_CollectionsReviewRuns_Current");
    }
}

public class CollectionsReviewRecordConfiguration : IEntityTypeConfiguration<CollectionsReviewRecord>
{
    public void Configure(EntityTypeBuilder<CollectionsReviewRecord> builder)
    {
        builder.ToTable("CollectionsReviewRecords");
        builder.HasKey(r => r.CollectionsReviewRecordId);
        builder.Property(r => r.CollectionsReviewRecordId).ValueGeneratedOnAdd();
        builder.Property(r => r.RecordKey).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.CycleKey).HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(r => r.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.TenantId).HasMaxLength(50).IsRequired();
        builder.Property(r => r.CustomerName).HasMaxLength(300).IsRequired();
        builder.Property(r => r.Phone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(r => r.Email).HasMaxLength(320).IsRequired();
        builder.Property(r => r.UnitCode).HasMaxLength(60).IsRequired();
        builder.Property(r => r.ProjectCode).HasMaxLength(60).IsRequired();
        builder.Property(r => r.PaymentStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.SourceStatus).HasMaxLength(100).IsRequired();
        builder.Property(r => r.RemainingAmount).HasPrecision(19, 4);
        builder.Property(r => r.RawRemainingAmount).HasPrecision(19, 4);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.ValidationStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Reasons).HasMaxLength(600).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(200).IsRequired();

        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.RecordKey }).IsUnique().HasDatabaseName("UX_CollectionsReviewRecords_Run_Key");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.ReminderType, r.ValidationStatus }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Type_Status");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.CompanyId, r.TenantId, r.UnitCode }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Customer");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.DueDate }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Due");
        builder.HasOne<CollectionsReviewRun>().WithMany().HasForeignKey(r => r.CollectionsReviewRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CollectionsDispatchConfiguration : IEntityTypeConfiguration<CollectionsDispatch>
{
    public void Configure(EntityTypeBuilder<CollectionsDispatch> builder)
    {
        builder.ToTable("CollectionsDispatches");
        builder.HasKey(d => d.CollectionsDispatchId);
        builder.Property(d => d.CollectionsDispatchId).ValueGeneratedOnAdd();
        builder.Property(d => d.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(d => d.SelectionMode).HasMaxLength(24).IsRequired();
        builder.Property(d => d.FilterJson).HasMaxLength(2000).IsRequired();
        builder.Property(d => d.ApprovedTotalsJson).HasMaxLength(500).IsRequired();
        builder.Property(d => d.StatusReason).HasMaxLength(500);
        builder.Property(d => d.Phase).HasMaxLength(200);
        builder.HasIndex(d => d.IdempotencyKey).IsUnique().HasDatabaseName("UX_CollectionsDispatches_IdempotencyKey");
        builder.HasIndex(d => d.PublicId).IsUnique().HasDatabaseName("UX_CollectionsDispatches_PublicId");
        builder.HasMany(d => d.Items).WithOne().HasForeignKey(i => i.CollectionsDispatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(d => d.Batches).WithOne().HasForeignKey(b => b.CollectionsDispatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CollectionsDispatchItemConfiguration : IEntityTypeConfiguration<CollectionsDispatchItem>
{
    public const string LiveFilter = "[Status] IN ('Approved','UploadedToGenesys','UnknownOutcome')";

    public void Configure(EntityTypeBuilder<CollectionsDispatchItem> builder)
    {
        builder.ToTable("CollectionsDispatchItems");
        builder.HasKey(i => i.CollectionsDispatchItemId);
        builder.Property(i => i.CollectionsDispatchItemId).ValueGeneratedOnAdd();
        builder.Property(i => i.RecordKey).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.TenantId).HasMaxLength(50).IsRequired();
        builder.Property(i => i.UnitCode).HasMaxLength(60).IsRequired();
        builder.Property(i => i.CustomerName).HasMaxLength(300).IsRequired();
        builder.Property(i => i.Phone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(i => i.Email).HasMaxLength(320).IsRequired();
        builder.Property(i => i.Amount).HasPrecision(19, 4);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(i => i.StatusReason).HasMaxLength(500);
        builder.Property(i => i.GenesysContactId).HasMaxLength(64).IsUnicode(false);
        builder.Property(i => i.SuppressionStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.SuppressionError).HasMaxLength(500);
        // The database guarantee behind duplicate prevention: a record can be in only one live dispatch
        // (approved, uploaded or of unknown outcome) at a time, whatever the idempotency key.
        builder.HasIndex(i => i.RecordKey, "UX_CollectionsDispatchItems_LiveRecord").IsUnique().HasFilter(LiveFilter);
        builder.HasIndex(i => i.RecordKey, "IX_CollectionsDispatchItems_RecordKey");
        builder.HasIndex(i => i.CollectionsGenesysBatchId).HasDatabaseName("IX_CollectionsDispatchItems_Batch");
    }
}

public class CollectionsGenesysBatchConfiguration : IEntityTypeConfiguration<CollectionsGenesysBatch>
{
    public void Configure(EntityTypeBuilder<CollectionsGenesysBatch> builder)
    {
        builder.ToTable("CollectionsGenesysBatches");
        builder.HasKey(b => b.CollectionsGenesysBatchId);
        builder.Property(b => b.CollectionsGenesysBatchId).ValueGeneratedOnAdd();
        builder.Property(b => b.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(b => b.ContactListId).HasMaxLength(36).IsUnicode(false).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(b => b.RequestHash).HasMaxLength(64).IsUnicode(false);
        builder.Property(b => b.Error).HasMaxLength(500);
        builder.Property(b => b.ReconciliationNote).HasMaxLength(500);
        builder.HasIndex(b => new { b.CollectionsDispatchId, b.Sequence }).IsUnique().HasDatabaseName("UX_CollectionsGenesysBatches_Dispatch_Sequence");
    }
}

public sealed class ReviewStore(TigerCsDbContext db) : IReviewStore
{
    private static readonly DispatchItemStatus[] LiveStatuses = [DispatchItemStatus.Approved, DispatchItemStatus.UploadedToGenesys, DispatchItemStatus.UnknownOutcome];

    // ---------------------------------------------------------------- runs

    public Task<CollectionsReviewRun?> GetCurrentRunAsync(CancellationToken ct) =>
        db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IsCurrent, ct);

    public Task<CollectionsReviewRun?> GetRunAsync(long runId, CancellationToken ct) =>
        db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.CollectionsReviewRunId == runId, ct);

    public Task<CollectionsReviewRun?> GetActiveRunAsync(CancellationToken ct) =>
        db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IsActive, ct);

    public async Task<bool> TryAddRunAsync(CollectionsReviewRun run, CancellationToken ct)
    {
        run.IsActive = true;
        db.CollectionsReviewRuns.Add(run);
        try { await db.SaveChangesAsync(ct); db.Entry(run).State = EntityState.Detached; return true; }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task<bool> TryStartRunAsync(long runId, DateTime nowUtc, CancellationToken ct) =>
        await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId && r.Status == ReviewRunStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Running).SetProperty(r => r.StartedAtUtc, nowUtc)
                .SetProperty(r => r.Phase, "Starting").SetProperty(r => r.ProgressPercent, 1), ct) == 1;

    public Task UpdateRunProgressAsync(long runId, string phase, int percent, int? sourceRows, CancellationToken ct) =>
        db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Phase, phase).SetProperty(r => r.ProgressPercent, percent)
                .SetProperty(r => r.SourceRowCount, r => sourceRows ?? r.SourceRowCount), ct);

    public async Task FailRunAsync(long runId, string error, DateTime nowUtc, CancellationToken ct)
    {
        await db.CollectionsReviewRecords.Where(r => r.CollectionsReviewRunId == runId).ExecuteDeleteAsync(ct);
        await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Failed).SetProperty(r => r.IsActive, false)
                .SetProperty(r => r.Error, error.Length > 500 ? error[..500] : error).SetProperty(r => r.CompletedAtUtc, nowUtc)
                .SetProperty(r => r.Phase, "Failed"), ct);
    }

    public async Task AddRecordsAsync(long runId, IReadOnlyList<CollectionsReviewRecord> records, CancellationToken ct)
    {
        foreach (var record in records) record.CollectionsReviewRunId = runId;
        db.CollectionsReviewRecords.AddRange(records);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task PublishRunAsync(long runId, int recordCount, int sourceRows, DateTime sourceReadAtUtc, DateTime nowUtc, int runsToKeep, CancellationToken ct)
    {
        // A relational transaction can be unavailable on the InMemory provider used by some tests; the unique "current" index
        // is what keeps two published runs from coexisting on a real database.
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.CollectionsReviewRuns.Where(r => r.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), ct);
        await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Completed).SetProperty(r => r.IsCurrent, true)
                .SetProperty(r => r.IsActive, false).SetProperty(r => r.CompletedAtUtc, nowUtc).SetProperty(r => r.SourceReadAtUtc, sourceReadAtUtc)
                .SetProperty(r => r.RecordCount, recordCount).SetProperty(r => r.SourceRowCount, sourceRows)
                .SetProperty(r => r.ProgressPercent, 100).SetProperty(r => r.Phase, "Completed"), ct);
        var keep = await db.CollectionsReviewRuns.Where(r => r.Status == ReviewRunStatus.Completed)
            .OrderByDescending(r => r.CollectionsReviewRunId).Select(r => r.CollectionsReviewRunId).Take(Math.Max(1, runsToKeep)).ToListAsync(ct);
        await db.CollectionsReviewRecords.Where(r => !keep.Contains(r.CollectionsReviewRunId)
            && db.CollectionsReviewRuns.Any(x => x.CollectionsReviewRunId == r.CollectionsReviewRunId && x.Status != ReviewRunStatus.Running && x.Status != ReviewRunStatus.Queued))
            .ExecuteDeleteAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- review queries

    private sealed class Row
    {
        public CollectionsReviewRecord Record { get; set; } = null!;
        public bool Sent { get; set; }
    }

    private IQueryable<CollectionsReviewRecord> Filtered(long runId, ReviewQuerySpec s)
    {
        var q = db.CollectionsReviewRecords.AsNoTracking().Where(r => r.CollectionsReviewRunId == runId);
        if (s.CompanyId is { } company) q = q.Where(r => r.CompanyId == company);
        if (s.Project is { } project) { var p = Like(project); q = q.Where(r => EF.Functions.Like(r.ProjectCode, p, "\\")); }
        if (s.Unit is { } unit) { var p = Like(unit); q = q.Where(r => EF.Functions.Like(r.UnitCode, p, "\\")); }
        if (s.CustomerText is { } text)
        {
            var name = Like(text);
            var digits = s.PhoneDigits is null ? null : Like(s.PhoneDigits);
            q = digits is null
                ? q.Where(r => EF.Functions.Like(r.CustomerName, name, "\\") || EF.Functions.Like(r.TenantId, name, "\\"))
                : q.Where(r => EF.Functions.Like(r.CustomerName, name, "\\") || EF.Functions.Like(r.Phone, digits, "\\") || EF.Functions.Like(r.TenantId, name, "\\"));
        }
        if (s.DueFrom is { } from) q = q.Where(r => r.DueDate >= from);
        if (s.DueTo is { } to) q = q.Where(r => r.DueDate <= to);
        if (s.PaymentStatuses is { } statuses) q = q.Where(r => statuses.Contains(r.PaymentStatus));
        if (s.MinRemaining is { } min) q = q.Where(r => r.RemainingAmount >= min);
        if (s.MaxRemaining is { } max) q = q.Where(r => r.RemainingAmount <= max);
        if (s.ReminderType is { } type) q = q.Where(r => r.ReminderType == type);
        if (s.Reason is { } reason) { var p = $"%;{reason};%"; q = q.Where(r => EF.Functions.Like(r.Reasons, p)); }
        return q;
    }

    private IQueryable<Row> WithSent(IQueryable<CollectionsReviewRecord> q) =>
        q.Select(r => new Row { Record = r, Sent = db.CollectionsDispatchItems.Any(i => i.RecordKey == r.RecordKey && LiveStatuses.Contains(i.Status)) });

    private static IQueryable<Row> ByStatus(IQueryable<Row> q, ReviewValidationStatus? status, bool stale) => status switch
    {
        null => q,
        ReviewValidationStatus.AlreadySent => q.Where(x => x.Sent),
        ReviewValidationStatus.Ready => stale ? q.Where(_ => false) : q.Where(x => !x.Sent && x.Record.ValidationStatus == ReviewValidationStatus.Ready),
        ReviewValidationStatus.NeedsReview => q.Where(x => !x.Sent && (x.Record.ValidationStatus == ReviewValidationStatus.NeedsReview
            || (stale && x.Record.ValidationStatus == ReviewValidationStatus.Ready))),
        _ => q.Where(x => !x.Sent && x.Record.ValidationStatus == status)
    };

    public async Task<StoredPage> QueryAsync(long runId, ReviewQuerySpec spec, bool stale, int skip, int take, CancellationToken ct)
    {
        var q = ByStatus(WithSent(Filtered(runId, spec)), spec.ValidationStatus, stale);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.Record.CompanyId).ThenBy(x => x.Record.TenantId).ThenBy(x => x.Record.UnitCode)
            .ThenBy(x => x.Record.ReminderType).ThenBy(x => x.Record.CollectionsReviewRecordId).Skip(skip).Take(take).ToListAsync(ct);
        return new StoredPage(await WithHistoryAsync(rows, ct), total);
    }

    public async Task<ReviewCountsDto> CountAsync(long runId, ReviewQuerySpec specWithoutStatus, bool stale, CancellationToken ct)
    {
        var baseQuery = WithSent(Filtered(runId, specWithoutStatus with { ValidationStatus = null }));
        var ready = await ByStatus(baseQuery, ReviewValidationStatus.Ready, stale).CountAsync(ct);
        var review = await ByStatus(baseQuery, ReviewValidationStatus.NeedsReview, stale).CountAsync(ct);
        var excluded = await ByStatus(baseQuery, ReviewValidationStatus.Excluded, stale).CountAsync(ct);
        var sent = await ByStatus(baseQuery, ReviewValidationStatus.AlreadySent, stale).CountAsync(ct);
        return new ReviewCountsDto(ready + review + excluded + sent, ready, review, excluded, sent);
    }

    public async Task<IReadOnlyList<ReviewRecordView>> ResolveAsync(long runId, ReviewQuerySpec spec, IReadOnlyCollection<string>? keys, bool stale, int max, CancellationToken ct)
    {
        var q = Filtered(runId, spec);
        if (keys is not null) q = q.Where(r => keys.Contains(r.RecordKey));
        var rows = await ByStatus(WithSent(q), spec.ValidationStatus, stale).OrderBy(x => x.Record.RecordKey).Take(max + 1).ToListAsync(ct);
        return await WithHistoryAsync(rows, ct);
    }

    private async Task<IReadOnlyList<ReviewRecordView>> WithHistoryAsync(List<Row> rows, CancellationToken ct)
    {
        var keys = rows.Select(r => r.Record.RecordKey).Distinct().ToList();
        var history = keys.Count == 0 ? [] : await (from i in db.CollectionsDispatchItems.AsNoTracking()
                                                    join d in db.CollectionsDispatches.AsNoTracking() on i.CollectionsDispatchId equals d.CollectionsDispatchId
                                                    where keys.Contains(i.RecordKey)
                                                    orderby i.CollectionsDispatchItemId descending
                                                    select new { i.RecordKey, i.Status, At = i.UploadedAtUtc ?? d.InitiatedAtUtc }).ToListAsync(ct);
        var latest = history.GroupBy(h => h.RecordKey).ToDictionary(g => g.Key, g => g.First());
        return rows.Select(r => latest.TryGetValue(r.Record.RecordKey, out var h)
            ? new ReviewRecordView(r.Record, r.Sent, h.Status.ToString(), h.At)
            : new ReviewRecordView(r.Record, r.Sent, null, null)).ToList();
    }

    private static string Like(string value) =>
        "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

    // ---------------------------------------------------------------- dispatch

    public Task<CollectionsDispatch?> FindDispatchByKeyAsync(string idempotencyKey, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey, ct);

    private IQueryable<CollectionsDispatch> Dispatches() =>
        db.CollectionsDispatches.AsNoTracking().Include(d => d.Items).Include(d => d.Batches).AsSplitQuery();

    public async Task<bool> TryAddDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        db.CollectionsDispatches.Add(dispatch);
        try { await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); return true; }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    public Task<CollectionsDispatch?> GetDispatchAsync(Guid publicId, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.PublicId == publicId, ct);

    public Task<CollectionsDispatch?> GetDispatchByIdAsync(long dispatchId, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.CollectionsDispatchId == dispatchId, ct);

    public async Task<IReadOnlyList<CollectionsDispatch>> ListDispatchesAsync(int take, CancellationToken ct) =>
        await Dispatches().OrderByDescending(d => d.CollectionsDispatchId).Take(take).ToListAsync(ct);

    public async Task<bool> TryAcquireLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
    {
        var expires = nowUtc + ttl;
        return await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId
                && (d.Status == DispatchStatus.Queued || d.Status == DispatchStatus.Revalidating || d.Status == DispatchStatus.Sending)
                && (d.LeaseExpiresAtUtc == null || d.LeaseExpiresAtUtc < nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseOwner, owner).SetProperty(d => d.LeaseExpiresAtUtc, expires)
                .SetProperty(d => d.Status, d => d.Status == DispatchStatus.Queued ? DispatchStatus.Revalidating : d.Status), ct) == 1;
    }

    public async Task<bool> RenewLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
    {
        var expires = nowUtc + ttl;
        return await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId && d.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseExpiresAtUtc, expires), ct) == 1;
    }

    public async Task<bool> TryCancelQueuedAsync(long dispatchId, string reason, DateTime nowUtc, CancellationToken ct) =>
        await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId && d.Status == DispatchStatus.Queued && d.LeaseOwner == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DispatchStatus.Cancelled).SetProperty(d => d.StatusReason, reason)
                .SetProperty(d => d.CompletedAtUtc, nowUtc), ct) == 1;

    public async Task UpdateDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        var tracked = await db.CollectionsDispatches.FirstAsync(d => d.CollectionsDispatchId == dispatch.CollectionsDispatchId, ct);
        tracked.Status = dispatch.Status; tracked.StatusReason = Cut(dispatch.StatusReason); tracked.StartedAtUtc = dispatch.StartedAtUtc;
        tracked.CompletedAtUtc = dispatch.CompletedAtUtc; tracked.ExcludedAtDispatchCount = dispatch.ExcludedAtDispatchCount;
        tracked.Phase = dispatch.Phase;
        if (dispatch.LeaseOwner is null) { tracked.LeaseOwner = null; tracked.LeaseExpiresAtUtc = null; }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public Task ReleaseItemsAsync(long dispatchId, string reason, CancellationToken ct)
    {
        var text = Cut(reason);
        return db.CollectionsDispatchItems.Where(i => i.CollectionsDispatchId == dispatchId && i.Status == DispatchItemStatus.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, DispatchItemStatus.Released).SetProperty(i => i.StatusReason, text), ct);
    }

    public async Task AddBatchesAsync(IReadOnlyList<CollectionsGenesysBatch> batches, CancellationToken ct)
    {
        db.CollectionsGenesysBatches.AddRange(batches);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public Task<CollectionsGenesysBatch?> GetBatchAsync(long batchId, CancellationToken ct) =>
        db.CollectionsGenesysBatches.AsNoTracking().FirstOrDefaultAsync(b => b.CollectionsGenesysBatchId == batchId, ct);

    public async Task<bool> TryClaimBatchAsync(long batchId, DateTime nowUtc, CancellationToken ct) =>
        await db.CollectionsGenesysBatches.Where(b => b.CollectionsGenesysBatchId == batchId && b.Status == GenesysBatchStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, GenesysBatchStatus.Submitting)
                .SetProperty(b => b.AttemptCount, b => b.AttemptCount + 1).SetProperty(b => b.StartedAtUtc, nowUtc), ct) == 1;

    public async Task UpdateBatchAsync(CollectionsGenesysBatch batch, CancellationToken ct)
    {
        var tracked = await db.CollectionsGenesysBatches.FirstAsync(b => b.CollectionsGenesysBatchId == batch.CollectionsGenesysBatchId, ct);
        tracked.Status = batch.Status; tracked.Error = Cut(batch.Error); tracked.HttpStatus = batch.HttpStatus;
        tracked.ReturnedContactCount = batch.ReturnedContactCount; tracked.RequestHash = batch.RequestHash;
        tracked.CompletedAtUtc = batch.CompletedAtUtc; tracked.ReconciledByEmployeeId = batch.ReconciledByEmployeeId;
        tracked.ReconciledAtUtc = batch.ReconciledAtUtc; tracked.ReconciliationNote = Cut(batch.ReconciliationNote);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<CollectionsDispatchItem>> GetBatchItemsAsync(long batchId, CancellationToken ct) =>
        await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsGenesysBatchId == batchId).OrderBy(i => i.BatchPosition).ToListAsync(ct);

    public async Task<IReadOnlyList<CollectionsDispatchItem>> GetItemsAsync(long dispatchId, CancellationToken ct) =>
        await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == dispatchId).OrderBy(i => i.RecordKey).ToListAsync(ct);

    public async Task UpdateItemsAsync(IReadOnlyCollection<CollectionsDispatchItem> items, CancellationToken ct)
    {
        foreach (var chunk in items.Chunk(500))
        {
            var ids = chunk.Select(i => i.CollectionsDispatchItemId).ToList();
            var tracked = await db.CollectionsDispatchItems.Where(i => ids.Contains(i.CollectionsDispatchItemId)).ToDictionaryAsync(i => i.CollectionsDispatchItemId, ct);
            foreach (var item in chunk)
            {
                var target = tracked[item.CollectionsDispatchItemId];
                target.Status = item.Status; target.StatusReason = Cut(item.StatusReason);
                target.CollectionsGenesysBatchId = item.CollectionsGenesysBatchId; target.BatchPosition = item.BatchPosition;
                target.GenesysContactId = item.GenesysContactId; target.UploadedAtUtc = item.UploadedAtUtc;
                target.VoiceEligible = item.VoiceEligible; target.SuppressionStatus = item.SuppressionStatus;
                target.SuppressedAtUtc = item.SuppressedAtUtc; target.SuppressionError = Cut(item.SuppressionError);
                target.BalanceCheckedAtUtc = item.BalanceCheckedAtUtc;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<UploadedContactView>> GetSuppressionCandidatesAsync(DateTime sinceUtc, int take, CancellationToken ct)
    {
        var rows = await (from i in db.CollectionsDispatchItems.AsNoTracking()
                          join b in db.CollectionsGenesysBatches.AsNoTracking() on i.CollectionsGenesysBatchId equals b.CollectionsGenesysBatchId
                          where i.Status == DispatchItemStatus.UploadedToGenesys && i.GenesysContactId != null
                                && i.SuppressionStatus != ContactSuppressionStatus.Suppressed && i.UploadedAtUtc >= sinceUtc
                          orderby i.BalanceCheckedAtUtc, i.CollectionsDispatchItemId
                          select new { Item = i, b.ContactListId }).Take(take).ToListAsync(ct);
        return rows.Select(r => new UploadedContactView(r.Item, r.ContactListId)).ToList();
    }

    public Task SetDispatchPhaseAsync(long dispatchId, string phase, CancellationToken ct) =>
        db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Phase, phase), ct);

    public Task SetRevalidationMsAsync(long dispatchId, long ms, CancellationToken ct) =>
        db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevalidationMs, ms), ct);

    public async Task<OverlapPage> GetOverlapsAsync(long runId, int skip, int take, CancellationToken ct)
    {
        var flagged = db.CollectionsReviewRecords.AsNoTracking().Where(r => r.CollectionsReviewRunId == runId && r.Phone != ""
            && (EF.Functions.Like(r.Reasons, "%;SharedPhoneMultipleUnits;%") || EF.Functions.Like(r.Reasons, "%;ReminderTypeOverlap;%")));
        var phones = flagged.GroupBy(r => r.Phone);
        var total = await phones.CountAsync(ct);
        var page = await phones.OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Skip(skip).Take(take).Select(g => g.Key).ToListAsync(ct);
        var rows = page.Count == 0 ? [] : await flagged.Where(r => page.Contains(r.Phone))
            .OrderBy(r => r.Phone).ThenBy(r => r.TenantId).ThenBy(r => r.UnitCode).ThenBy(r => r.ReminderType)
            .Select(r => new OverlapRow(r.Phone, r.CompanyId, r.TenantId, r.CustomerName, r.UnitCode, r.ReminderType.ToString(), r.RemainingAmount, r.Currency, r.Reasons))
            .ToListAsync(ct);
        return new OverlapPage(total, page.Select(p => new OverlapGroup(p, rows.Where(r => r.Phone == p).ToList())).ToList());
    }

    private static string? Cut(string? text) => text is { Length: > 500 } ? text[..500] : text;

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 }) return true;
            // SQLite (used by the relational tests) reports unique violations by message.
            if (inner.GetType().Name == "SqliteException" && inner.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
