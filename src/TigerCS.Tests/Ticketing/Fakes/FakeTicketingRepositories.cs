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

    public Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default)
    {
        typeof(IntakeRecord).GetProperty(nameof(IntakeRecord.IntakeRecordId))!.SetValue(intakeRecord, _nextId++);
        _records[intakeRecord.IntakeRecordId] = intakeRecord;
        return Task.CompletedTask;
    }
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
}

public sealed class FakeTicketRequesterSnapshotRepository : ITicketRequesterSnapshotRepository
{
    public List<TicketRequesterSnapshot> Added { get; } = [];

    public Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Added.Add(snapshot);
        return Task.CompletedTask;
    }
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

public sealed class FakeTicketingUnitOfWork : ITicketingUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.CompletedTask;
    }
}
