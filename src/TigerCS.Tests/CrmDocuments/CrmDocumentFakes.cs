using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>A scriptable Tiger CRM document source — what CRM returns, and what it was asked.</summary>
public sealed class FakeCrmDocumentGateway : ICrmDocumentGateway
{
    public List<CrmDocumentRecord> Records { get; } = [];

    /// <summary>The document type each record belongs to (default Contract).</summary>
    public Dictionary<string, CrmDocumentType> TypeByRecord { get; } = [];

    private CrmDocumentType TypeOf(CrmDocumentRecord r) => TypeByRecord.GetValueOrDefault(r.RecordId, CrmDocumentType.Contract);
    public Dictionary<string, CrmDocumentContent?> Contents { get; } = [];
    public string? CustomerEmail { get; set; } = "customer@example.com";
    public bool Unavailable { get; set; }
    public bool ReturnEverythingUnfiltered { get; set; }
    public List<(CrmDocumentType Type, string UnitId, string ContactId)> ListCalls { get; } = [];
    public List<string> ContentCalls { get; } = [];

    public Task<CrmDocumentListing> ListAsync(CrmDocumentType type, string crmUnitId, string crmContactId, CancellationToken cancellationToken = default)
    {
        ListCalls.Add((type, crmUnitId, crmContactId));
        if (Unavailable)
        {
            throw new CrmDocumentSourceUnavailableException("CRM down");
        }

        var rows = ReturnEverythingUnfiltered
            ? Records.Where(r => TypeOf(r) == type).ToList()
            : Records.Where(r => TypeOf(r) == type && r.CrmUnitId == crmUnitId && r.OwnerCrmContactId == crmContactId).ToList();
        return Task.FromResult(new CrmDocumentListing(rows, CustomerEmail));
    }

    public Task<CrmDocumentContent?> GetContentAsync(CrmDocumentType type, string recordId, CancellationToken cancellationToken = default)
    {
        ContentCalls.Add(recordId);
        if (Unavailable)
        {
            throw new CrmDocumentSourceUnavailableException("CRM down");
        }

        if (Contents.TryGetValue(recordId, out var content))
        {
            return Task.FromResult(content);
        }

        var record = Records.FirstOrDefault(r => r.RecordId == recordId);
        return Task.FromResult<CrmDocumentContent?>(record is null
            ? null
            : new CrmDocumentContent(record.RecordId, record.CrmUnitId, record.OwnerCrmContactId, [1, 2, 3], "application/pdf", $"{record.RecordId}.pdf"));
    }
}

public sealed class FakeDocumentChannelSender(DocumentDeliveryChannel channel = DocumentDeliveryChannel.Email) : IDocumentDeliveryChannelSender
{
    public DocumentDeliveryChannel Channel => channel;
    public List<DocumentDeliveryMessage> Sent { get; } = [];
    public DocumentDeliveryOutcome NextOutcome { get; set; } = DocumentDeliveryOutcome.Sent;

    /// <summary>Runs inside the send — lets a test fire a second request while the first is still in flight.</summary>
    public Func<Task>? DuringSend { get; set; }

    public async Task<DocumentDeliveryResult> SendAsync(DocumentDeliveryMessage message, CancellationToken cancellationToken = default)
    {
        if (DuringSend is not null)
        {
            await DuringSend();
        }

        if (NextOutcome == DocumentDeliveryOutcome.Sent)
        {
            Sent.Add(message);
        }

        return new DocumentDeliveryResult(NextOutcome, NextOutcome == DocumentDeliveryOutcome.Sent ? null : "FAKE_FAILURE");
    }
}

/// <summary>Mirrors the table's unique (caller, key) index: a second add of the same pair fails the save with DuplicateWriteException.</summary>
public sealed class FakeCrmDocumentDeliveryRepository : ICrmDocumentDeliveryRepository
{
    private long _nextId = 1;
    public List<CrmDocumentDeliveryRequest> All { get; } = [];

    public Task<CrmDocumentDeliveryRequest?> GetByKeyAsync(Guid callerEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(All.FirstOrDefault(r => r.CallerEmployeeId == callerEmployeeId && r.IdempotencyKey == idempotencyKey && r.CrmDocumentDeliveryRequestId != 0));

    public Task<CrmDocumentDeliveryRequest?> FindRecentSentAsync(
        Guid verificationSessionId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel, DateTime sinceUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(All.FirstOrDefault(r => r.CrmDocumentDeliveryRequestId != 0
            && r.VerificationSessionId == verificationSessionId && r.DocumentType == type && r.CrmRecordId == crmRecordId
            && r.Channel == channel && r.Status == DocumentDeliveryStatus.Sent && r.SentAtUtc >= sinceUtc));

    public Task AddAsync(CrmDocumentDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        All.Add(request);
        return Task.CompletedTask;
    }

    /// <summary>Called by the unit of work on save: assigns ids and enforces uniqueness the way SQL Server would.</summary>
    public void Commit()
    {
        foreach (var pending in All.Where(r => r.CrmDocumentDeliveryRequestId == 0).ToList())
        {
            if (All.Any(r => r != pending && r.CrmDocumentDeliveryRequestId != 0
                && r.CallerEmployeeId == pending.CallerEmployeeId && r.IdempotencyKey == pending.IdempotencyKey))
            {
                All.Remove(pending);
                throw new DuplicateWriteException(new InvalidOperationException("UX_CrmDocumentDeliveryRequests_CallerKey"));
            }

            typeof(CrmDocumentDeliveryRequest).GetProperty(nameof(CrmDocumentDeliveryRequest.CrmDocumentDeliveryRequestId))!
                .SetValue(pending, _nextId++);
        }
    }
}

public sealed class CommittingUnitOfWork(FakeCrmDocumentDeliveryRepository deliveries) : ICustomerVerificationUnitOfWork
{
    public Func<Task>? BeforeSave { get; set; }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (BeforeSave is { } hook)
        {
            BeforeSave = null;
            await hook();
        }

        deliveries.Commit();
    }
}

public sealed class DocumentServiceFixture
{
    public static readonly Guid Caller = Guid.NewGuid();

    public CrmDocumentOptions Options { get; } = new() { Enabled = true };
    public FakeVerificationSessionRepository Sessions { get; } = new();
    public FakeUnitReferenceRepository Units { get; } = new();
    public FakeContactReferenceRepository Contacts { get; } = new();
    public FakeCrmDocumentGateway Gateway { get; } = new();
    public FakeCrmDocumentDeliveryRepository Deliveries { get; } = new();
    public FakeDocumentChannelSender Email { get; } = new();
    public CommittingUnitOfWork UnitOfWork { get; }
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc));
    public CrmDocumentCopyAppService Service { get; }

    public UnitReference Unit { get; }
    public ContactReference Contact { get; }
    public Guid SessionId { get; }

    public DocumentServiceFixture(VerificationMethod method = VerificationMethod.Otp, bool confirm = true, Guid? owner = null)
    {
        UnitOfWork = new CommittingUnitOfWork(Deliveries);
        Unit = Units.Seed("CRM-UNIT-1001", "1204", "Tiger Tower A");
        Contact = Contacts.Seed(Unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Al-Farsi");
        SessionId = AddSession(method, confirm, owner ?? Caller, Unit, Contact);

        Service = new CrmDocumentCopyAppService(
            Options, Sessions, Units, Contacts, Gateway, Deliveries, [Email], UnitOfWork, Audit, Clock,
            NullLogger<CrmDocumentCopyAppService>.Instance);
    }

    public Guid AddSession(VerificationMethod method, bool confirm, Guid owner, UnitReference unit, ContactReference contact, TimeSpan? lifetime = null)
    {
        var id = Guid.NewGuid();
        var now = Clock.GetUtcNow().UtcDateTime;
        var session = new VerificationSession(
            id, owner, unit.UnitReferenceId, contact.ContactReferenceId, unit.UnitNumber, unit.PropertyName, null, null,
            contact.DisplayName, contact.ContactChannel, now, now + (lifetime ?? TimeSpan.FromMinutes(30)), null);
        if (confirm)
        {
            session.Confirm(now, method);
        }

        Sessions.AddAsync(session);
        return id;
    }

    public CrmDocumentRecord Own(string recordId, string label = "Sale and Purchase Agreement", CrmDocumentType type = CrmDocumentType.Contract) =>
        Add(new CrmDocumentRecord(recordId, label, Unit.CrmUnitId, Contact.CrmContactId, Unit.UnitNumber, new DateTime(2025, 3, 1)), type);

    public CrmDocumentRecord Add(CrmDocumentRecord record, CrmDocumentType type = CrmDocumentType.Contract)
    {
        Gateway.Records.Add(record);
        Gateway.TypeByRecord[record.RecordId] = type;
        return record;
    }
}
