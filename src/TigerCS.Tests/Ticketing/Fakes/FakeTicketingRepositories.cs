using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Fakes;

public sealed class FakeCategoryRepository : ICategoryRepository
{
    private readonly Dictionary<int, Category> _categories = [];
    private int _nextId = 1;

    public Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_categories.GetValueOrDefault(categoryId));

    public Category Seed(int departmentId, string name = "General Inquiry", bool isActive = true)
    {
        var category = new Category(name, departmentId, isActive: isActive);
        typeof(Category).GetProperty(nameof(Category.CategoryId))!.SetValue(category, _nextId++);
        _categories[category.CategoryId] = category;
        return category;
    }

    public Task<IReadOnlyCollection<Category>> ListAsync(bool activeOnly, int? departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Category>>(
            _categories.Values
                .Where(c => !activeOnly || c.IsActive)
                .Where(c => departmentId is null || c.DepartmentId == departmentId)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList());
}

public sealed class FakePriorityRepository : IPriorityRepository
{
    private readonly Dictionary<byte, Priority> _priorities = new()
    {
        [(byte)PriorityLevel.Critical] = new Priority((byte)PriorityLevel.Critical, "Critical", 1),
        [(byte)PriorityLevel.High] = new Priority((byte)PriorityLevel.High, "High", 2),
        [(byte)PriorityLevel.Medium] = new Priority((byte)PriorityLevel.Medium, "Medium", 3),
        [(byte)PriorityLevel.Low] = new Priority((byte)PriorityLevel.Low, "Low", 4)
    };

    public Task<Priority?> GetByIdAsync(byte priorityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_priorities.GetValueOrDefault(priorityId));
}

public sealed class FakeIntakeRecordRepository : IIntakeRecordRepository
{
    private readonly Dictionary<long, IntakeRecord> _records = [];
    private long _nextId = 1;

    public Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.GetValueOrDefault(intakeRecordId));

    public Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values.FirstOrDefault(r => r.LinkedTicketId == ticketId));

    public Task<IReadOnlyList<long>> ListLinkedTicketIdsByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(
            _records.Values
                .Where(r => r.PhoneNumber == phoneNumber && r.LinkedTicketId is not null)
                .Select(r => r.LinkedTicketId!.Value)
                .Distinct()
                .ToList());

    public Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default)
    {
        typeof(IntakeRecord).GetProperty(nameof(IntakeRecord.IntakeRecordId))!.SetValue(intakeRecord, _nextId++);
        _records[intakeRecord.IntakeRecordId] = intakeRecord;
        return Task.CompletedTask;
    }
}

public sealed class FakeDepartmentCustomerLookupSourceRepository : IDepartmentCustomerLookupSourceRepository
{
    private readonly Dictionary<int, List<CustomerLookupSource>> _sourcesByDepartmentId = [];

    public FakeDepartmentCustomerLookupSourceRepository Seed(int departmentId, params CustomerLookupSource[] sources)
    {
        _sourcesByDepartmentId[departmentId] = sources.ToList();
        return this;
    }

    public Task<IReadOnlyCollection<CustomerLookupSource>> GetSourcesForDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<CustomerLookupSource>>(
            _sourcesByDepartmentId.GetValueOrDefault(departmentId, []));
}

public sealed class FakeTicketRepository : ITicketRepository
{
    private readonly Dictionary<long, Ticket> _tickets = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every ticket added so far, in insertion order.</summary>
    public IReadOnlyCollection<Ticket> All => _tickets.Values;

    public Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.GetValueOrDefault(ticketId));

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(ticket, _nextId++);
        _tickets[ticket.TicketId] = ticket;
        return Task.CompletedTask;
    }

    public Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.Values.Count(t => t.TicketNumber.StartsWith(ticketNumberPrefix, StringComparison.Ordinal)));

    public Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = _tickets.Values.AsEnumerable();

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        if (query.DepartmentId is { } departmentId)
        {
            filtered = filtered.Where(t => t.CurrentDepartmentId == departmentId);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(t => t.CategoryId == categoryId);
        }

        if (query.PriorityId is { } priorityId)
        {
            filtered = filtered.Where(t => t.PriorityId == priorityId);
        }

        if (query.TicketStatus is { } ticketStatus)
        {
            filtered = filtered.Where(t => t.TicketStatus == ticketStatus);
        }

        if (query.VerificationStatus is { } verificationStatus)
        {
            filtered = filtered.Where(t => t.VerificationStatus == verificationStatus);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(t =>
                t.TicketNumber.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                || t.RequestSummary.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        }

        var all = filtered.ToList();
        var totalCount = all.Count;

        IEnumerable<Ticket> sorted = query.SortBy switch
        {
            TicketSortBy.Priority => query.SortDescending
                ? all.OrderByDescending(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc)
                : all.OrderBy(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc),
            _ => query.SortDescending
                ? all.OrderByDescending(t => t.CreatedAtUtc)
                : all.OrderBy(t => t.CreatedAtUtc)
        };

        var page = sorted.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        return Task.FromResult(new TicketQueryResult(page, totalCount));
    }

    /// <summary>Test double — RowVersion concurrency is simulated by FakeTicketingUnitOfWork.ThrowTicketConcurrencyConflictOnCall instead, since there's no real change tracker here to prime.</summary>
    public void SetRowVersion(Ticket ticket, byte[] rowVersion)
    {
    }

    public Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(
        CustomerHistoryQuery query, CancellationToken cancellationToken = default)
    {
        IEnumerable<Ticket> filtered;
        if (query.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            filtered = _tickets.Values.Where(t => t.CrmBuyerCustomerId == crmBuyerCustomerId);
        }
        else if (query.TicketIds is { Count: > 0 } ticketIds)
        {
            filtered = _tickets.Values.Where(t => ticketIds.Contains(t.TicketId));
        }
        else
        {
            return Task.FromResult(new CustomerHistoryQueryResult([], 0, 0, 0));
        }

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        if (query.ExcludeTicketId is { } excludeTicketId)
        {
            filtered = filtered.Where(t => t.TicketId != excludeTicketId);
        }

        var all = filtered.ToList();
        var closedCount = all.Count(t => t.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed);
        var items = all.OrderByDescending(t => t.CreatedAtUtc).Take(query.Limit).ToList();

        return Task.FromResult(new CustomerHistoryQueryResult(items, all.Count, all.Count - closedCount, closedCount));
    }
}

public sealed class FakeTicketRequesterSnapshotRepository : ITicketRequesterSnapshotRepository
{
    public List<TicketRequesterSnapshot> Added { get; } = [];

    public Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Added.Add(snapshot);
        return Task.CompletedTask;
    }

    /// <summary>Reads back from <see cref="Added"/>, so a test that creates a ticket through the real service sees the same snapshot the acknowledgement handler would.</summary>
    public Task<TicketRequesterSnapshot?> GetByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.FirstOrDefault(s => s.TicketId == ticketId));
}

public sealed class FakeTicketStatusHistoryRepository : ITicketStatusHistoryRepository
{
    public List<TicketStatusHistory> Added { get; } = [];

    public Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default)
    {
        Added.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class FakeTicketAssignmentRepository : ITicketAssignmentRepository
{
    public List<TicketAssignment> Added { get; } = [];

    public Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.LastOrDefault(a => a.TicketId == ticketId && a.IsCurrent));

    public Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default)
    {
        Added.Add(assignment);
        return Task.CompletedTask;
    }
}

public sealed class FakeTicketResolutionRepository : ITicketResolutionRepository
{
    public List<TicketResolution> Added { get; } = [];

    public Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.LastOrDefault(r => r.TicketId == ticketId && r.IsCurrent));

    public Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default)
    {
        Added.Add(resolution);
        return Task.CompletedTask;
    }
}

public sealed class FakeTicketNoteRepository : ITicketNoteRepository
{
    public List<TicketNote> Added { get; } = [];

    public Task AddAsync(TicketNote note, CancellationToken cancellationToken = default)
    {
        typeof(TicketNote).GetProperty(nameof(TicketNote.TicketNoteId))!.SetValue(note, Added.Count + 1L);
        Added.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketNote>> ListByTicketIdAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketNote>>(
            Added.Where(n => n.TicketId == ticketId)
                .OrderByDescending(n => n.CreatedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList());

    public Task<int> CountByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.Count(n => n.TicketId == ticketId));
}

public sealed class FakeTicketingUnitOfWork : ITicketingUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    /// <summary>
    /// Optional Outbox writer whose staged rows follow this unit of work's
    /// own fate, so an atomicity test can assert the real property: an Outbox
    /// message becomes durable only when the business transaction commits,
    /// and a rollback leaves none behind (ADR-0013).
    /// </summary>
    public TigerCS.Tests.Notifications.Fakes.FakeOutboxWriter? OutboxWriter { get; set; }

    public int TransactionsBegun { get; private set; }
    public int TransactionsCommitted { get; private set; }
    public int TransactionsRolledBack { get; private set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw VerificationSessionConcurrentlyConsumedException — mirrors FakeCustomerVerificationUnitOfWork's ThrowDuplicateWriteExceptionOnCall. Null = never.</summary>
    public int? ThrowConcurrencyConflictOnCall { get; set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw TicketConcurrentlyModifiedException (a lost RowVersion race on assignment/transfer/status/resolve/close/reconciliation). Null = never.</summary>
    public int? ThrowTicketConcurrencyConflictOnCall { get; set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw DuplicateWriteException (a TicketNumber collision). Null = never.</summary>
    public int? ThrowDuplicateWriteExceptionOnCall { get; set; }

    public Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        TransactionsBegun++;
        return Task.FromResult<ITicketingTransaction>(new FakeTicketingTransaction(this));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;

        if (ThrowConcurrencyConflictOnCall == SaveChangesCallCount)
        {
            ThrowConcurrencyConflictOnCall = null;
            throw new VerificationSessionConcurrentlyConsumedException(new InvalidOperationException("Simulated concurrency conflict."));
        }

        if (ThrowTicketConcurrencyConflictOnCall == SaveChangesCallCount)
        {
            ThrowTicketConcurrencyConflictOnCall = null;
            throw new TicketConcurrentlyModifiedException(new InvalidOperationException("Simulated ticket RowVersion conflict."));
        }

        if (ThrowDuplicateWriteExceptionOnCall == SaveChangesCallCount)
        {
            ThrowDuplicateWriteExceptionOnCall = null;
            throw new DuplicateWriteException(new InvalidOperationException("Simulated unique-constraint violation."));
        }

        return Task.CompletedTask;
    }

    private sealed class FakeTicketingTransaction(FakeTicketingUnitOfWork owner) : ITicketingTransaction
    {
        private bool _resolved;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.TransactionsCommitted++;
            owner.OutboxWriter?.Commit();
            _resolved = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            owner.TransactionsRolledBack++;
            owner.OutboxWriter?.Rollback();
            _resolved = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // Standard transaction semantics: disposing without an explicit
            // Commit is a rollback (mirrors IDbContextTransaction).
            if (!_resolved)
            {
                owner.TransactionsRolledBack++;
                owner.OutboxWriter?.Rollback();
                _resolved = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
