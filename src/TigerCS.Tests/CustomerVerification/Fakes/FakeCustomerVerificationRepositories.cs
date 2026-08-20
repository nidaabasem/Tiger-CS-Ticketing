using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Tests.CustomerVerification.Fakes;

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

public sealed class FakeCustomerVerificationUnitOfWork : ICustomerVerificationUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    /// <summary>
    /// Simulates a concurrent-duplicate-write race (a unique-index
    /// violation) on the next SaveChangesAsync call only — equivalent to
    /// setting <see cref="ThrowDuplicateWriteExceptionOnCall"/> to
    /// <see cref="SaveChangesCallCount"/> + 1 at the time it's set.
    /// </summary>
    public bool ThrowDuplicateWriteExceptionOnce
    {
        set => ThrowDuplicateWriteExceptionOnCall = value ? SaveChangesCallCount + 1 : null;
    }

    /// <summary>1-based SaveChangesAsync call number on which to throw DuplicateWriteException — lets a test let an earlier call (e.g. an unrelated entity's own upsert) succeed normally before the race hits a later one. Null = never.</summary>
    public int? ThrowDuplicateWriteExceptionOnCall { get; set; }

    /// <summary>Simulates a genuine, unrelated save failure (never a uniqueness violation) on the next SaveChangesAsync call only — must never be caught/recovered as if it were a duplicate-write race.</summary>
    public bool ThrowUnrelatedFailureOnce { get; set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        if (ThrowUnrelatedFailureOnce)
        {
            ThrowUnrelatedFailureOnce = false;
            throw new InvalidOperationException("Simulated unrelated save failure (not a uniqueness violation).");
        }

        if (ThrowDuplicateWriteExceptionOnCall == SaveChangesCallCount)
        {
            ThrowDuplicateWriteExceptionOnCall = null;
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
