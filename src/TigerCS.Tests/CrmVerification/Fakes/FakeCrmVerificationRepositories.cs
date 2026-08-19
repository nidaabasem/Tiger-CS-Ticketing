using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Tests.CrmVerification.Fakes;

public sealed class FakeUnitReferenceRepository : IUnitReferenceRepository
{
    private readonly Dictionary<int, UnitReference> _units = [];
    private int _nextId = 1;

    public Task<UnitReference?> GetByCrmUnitIdAsync(string crmUnitId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_units.Values.FirstOrDefault(u => u.CrmUnitId == crmUnitId));

    public Task<UnitReference?> GetByIdAsync(int unitReferenceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_units.GetValueOrDefault(unitReferenceId));

    public Task AddAsync(UnitReference unitReference, CancellationToken cancellationToken = default)
    {
        typeof(UnitReference).GetProperty(nameof(UnitReference.UnitReferenceId))!.SetValue(unitReference, _nextId++);
        _units[unitReference.UnitReferenceId] = unitReference;
        return Task.CompletedTask;
    }

    /// <summary>Test setup helper — bypasses the CRM gateway to seed a cache row directly.</summary>
    public UnitReference Seed(string crmUnitId, string unitNumber, string? propertyName = null)
    {
        var unit = new UnitReference(crmUnitId, unitNumber, propertyName, null, null, DateTime.UtcNow);
        typeof(UnitReference).GetProperty(nameof(UnitReference.UnitReferenceId))!.SetValue(unit, _nextId++);
        _units[unit.UnitReferenceId] = unit;
        return unit;
    }
}

public sealed class FakeContactReferenceRepository : IContactReferenceRepository
{
    private readonly Dictionary<int, ContactReference> _contacts = [];
    private int _nextId = 1;

    public Task<IReadOnlyList<ContactReference>> GetByUnitReferenceIdAsync(
        int unitReferenceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContactReference>>(
            _contacts.Values.Where(c => c.UnitReferenceId == unitReferenceId).ToList());

    public Task<ContactReference?> GetByCrmContactIdAsync(string crmContactId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_contacts.Values.FirstOrDefault(c => c.CrmContactId == crmContactId));

    public Task<ContactReference?> GetByIdAsync(int contactReferenceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_contacts.GetValueOrDefault(contactReferenceId));

    public Task AddAsync(ContactReference contactReference, CancellationToken cancellationToken = default)
    {
        typeof(ContactReference).GetProperty(nameof(ContactReference.ContactReferenceId))!.SetValue(contactReference, _nextId++);
        _contacts[contactReference.ContactReferenceId] = contactReference;
        return Task.CompletedTask;
    }

    /// <summary>Test setup helper — bypasses the CRM gateway to seed a cache row directly.</summary>
    public ContactReference Seed(int unitReferenceId, string crmContactId, string displayName, ContactType type = ContactType.Owner)
    {
        var contact = new ContactReference(crmContactId, unitReferenceId, displayName, "channel@example.com", type, null, DateTime.UtcNow);
        typeof(ContactReference).GetProperty(nameof(ContactReference.ContactReferenceId))!.SetValue(contact, _nextId++);
        _contacts[contact.ContactReferenceId] = contact;
        return contact;
    }
}

public sealed class FakeVerificationSessionRepository : IVerificationSessionRepository
{
    private readonly Dictionary<Guid, VerificationSession> _sessions = [];

    public Task<VerificationSession?> GetByIdAsync(Guid verificationSessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.GetValueOrDefault(verificationSessionId));

    public Task<VerificationSession?> GetByIdempotencyKeyAsync(
        Guid agentEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.Values.FirstOrDefault(
            s => s.AgentEmployeeId == agentEmployeeId && s.IdempotencyKey == idempotencyKey));

    public Task AddAsync(VerificationSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.VerificationSessionId] = session;
        return Task.CompletedTask;
    }
}

public sealed class FakeCrmVerificationUnitOfWork : ICrmVerificationUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    /// <summary>Simulates a concurrent-duplicate-write race (a unique-index violation) on the next SaveChangesAsync call only.</summary>
    public bool ThrowDuplicateWriteExceptionOnce { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        if (ThrowDuplicateWriteExceptionOnce)
        {
            ThrowDuplicateWriteExceptionOnce = false;
            throw new DuplicateWriteException(new InvalidOperationException("Simulated unique-constraint violation."));
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeAuditEntryWriter : IAuditEntryWriter
{
    public List<(Guid? ActorEmployeeId, string Action, string EntityType, string? EntityId)> Written { get; } = [];

    public Task WriteAsync(
        Guid? actorEmployeeId, string action, string entityType, string? entityId, string? beforeValue,
        string? afterValue, Guid correlationId, CancellationToken cancellationToken = default)
    {
        Written.Add((actorEmployeeId, action, entityType, entityId));
        return Task.CompletedTask;
    }
}

public sealed class FakeCrmGateway : ICrmGateway
{
    public bool ThrowUnavailable { get; set; }

    private readonly Dictionary<string, (CrmUnitResult Unit, CrmContactResult[] Contacts)> _fixtures = [];

    public FakeCrmGateway Seed(CrmUnitResult unit, params CrmContactResult[] contacts)
    {
        _fixtures[unit.CrmUnitId] = (unit, contacts);
        return this;
    }

    public Task<CrmUnitResult?> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        if (ThrowUnavailable)
        {
            throw new CrmGatewayUnavailableException("Simulated outage.");
        }

        return Task.FromResult(_fixtures.TryGetValue(crmUnitId, out var f) ? f.Unit : null);
    }

    public Task<IReadOnlyList<CrmUnitResult>> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default)
    {
        if (ThrowUnavailable)
        {
            throw new CrmGatewayUnavailableException("Simulated outage.");
        }

        var matches = _fixtures.Values.Where(f => f.Unit.UnitNumber == unitNumber).Select(f => f.Unit).ToList();
        return Task.FromResult<IReadOnlyList<CrmUnitResult>>(matches);
    }

    public Task<IReadOnlyList<CrmContactResult>> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        if (ThrowUnavailable)
        {
            throw new CrmGatewayUnavailableException("Simulated outage.");
        }

        return Task.FromResult<IReadOnlyList<CrmContactResult>>(
            _fixtures.TryGetValue(crmUnitId, out var f) ? f.Contacts : []);
    }
}
