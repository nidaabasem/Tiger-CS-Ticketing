using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>A scriptable Tiger CRM document source — what CRM returns for (customer, lead, type), and what it was asked.</summary>
public sealed class FakeCrmDocumentGateway : ICrmDocumentGateway
{
    private sealed record Row(int CustomerId, int LeadId, CrmDocumentType Type, CrmDocumentRecord Record);

    private readonly List<Row> _rows = [];

    public Dictionary<string, CrmDocumentContent?> Contents { get; } = [];

    /// <summary>CRM's own "selectionRequired" flag, forced on even for a single record.</summary>
    public bool ForceSelectionRequired { get; set; }

    public CrmDocumentSourceFailure? ListFailure { get; set; }
    public CrmDocumentSourceFailure? DownloadFailure { get; set; }

    public List<(CrmDocumentType Type, int CustomerId, int LeadId)> ListCalls { get; } = [];
    public List<string> DownloadCalls { get; } = [];

    public CrmDocumentRecord Add(int customerId, int leadId, CrmDocumentType type, string recordId, string name = "Sale and Purchase Agreement")
    {
        var record = new CrmDocumentRecord(recordId, name, $"files/{recordId}.pdf");
        _rows.Add(new Row(customerId, leadId, type, record));
        return record;
    }

    public Task<CrmDocumentListing> ListAsync(CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken = default)
    {
        ListCalls.Add((type, customerId, leadId));
        if (ListFailure is { } failure)
        {
            throw new CrmDocumentSourceException(failure, "scripted");
        }

        var rows = _rows.Where(r => r.CustomerId == customerId && r.LeadId == leadId && r.Type == type).Select(r => r.Record).ToList();
        return Task.FromResult(new CrmDocumentListing(customerId, leadId, ForceSelectionRequired || rows.Count > 1, rows));
    }

    public Task<CrmDocumentContent?> DownloadAsync(CrmDocumentRecord record, CancellationToken cancellationToken = default)
    {
        DownloadCalls.Add(record.RecordId);
        if (DownloadFailure is { } failure)
        {
            throw new CrmDocumentSourceException(failure, "scripted");
        }

        if (Contents.TryGetValue(record.RecordId, out var scripted))
        {
            return Task.FromResult(scripted);
        }

        return Task.FromResult<CrmDocumentContent?>(new CrmDocumentContent(record.RecordId, [1, 2, 3], "application/pdf", $"{record.RecordId}.pdf"));
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

    public void DiscardPendingChanges()
    {
    }

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

    /// <summary>The verified customer's CRM ids (the example ids from CRM's contract).</summary>
    public const int CustomerId = 9001;
    public const int LeadId = 12345;
    public const int OtherLeadId = 12346;
    public const string VerifiedPhone = "+971501234567";

    public CrmDocumentOptions Options { get; } = new() { Enabled = true };
    public FakeVerificationSessionRepository Sessions { get; } = new();
    public FakeUnitReferenceRepository Units { get; } = new();
    public FakeContactReferenceRepository Contacts { get; } = new();
    public FakeCrmDocumentGateway Gateway { get; } = new();
    public FakeCrmBuyerLookupGateway Buyers { get; } = new();
    public FakeCrmDocumentDeliveryRepository Deliveries { get; } = new();
    public FakeDocumentChannelSender Email { get; } = new();
    public CommittingUnitOfWork UnitOfWork { get; }
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc));
    public CrmDocumentCopyAppService Service { get; }

    public UnitReference Unit { get; }
    public ContactReference Contact { get; }
    public Guid SessionId { get; }

    public DocumentServiceFixture(
        VerificationMethod method = VerificationMethod.Otp, bool confirm = true, Guid? owner = null,
        string contactChannel = VerifiedPhone, string? customerEmail = "customer@example.com", bool serverProof = true)
    {
        UnitOfWork = new CommittingUnitOfWork(Deliveries);
        Unit = Units.Seed("CRM-UNIT-1001", "1204", "Tiger Tower A");
        Contact = Contacts.Seed(Unit.UnitReferenceId, "CRM-CONTACT-2001", "Ahmed Al-Farsi", contactChannel: contactChannel);
        SessionId = AddSession(method, confirm, owner ?? Caller, Unit, Contact, serverProof: serverProof);

        // CRM knows the verified customer: customer 9001 with two units (leads 12345 = the verified 1204, and 12346 = 1403).
        Buyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(CustomerId, "Ahmed Al-Farsi", null, VerifiedPhone, customerEmail),
                [BuyerUnit(LeadId, 77, "1204", "Tiger Tower A"), BuyerUnit(OtherLeadId, 78, "1403", "Tiger Tower A")])
        ]));

        Service = new CrmDocumentCopyAppService(
            Options, Sessions, Units, Contacts, Gateway,
            new CrmBuyerLookupAppService(Buyers, NullLogger<CrmBuyerLookupAppService>.Instance),
            Deliveries, [Email], UnitOfWork, Audit, Clock, NullLogger<CrmDocumentCopyAppService>.Instance);
    }

    public static CrmBuyerUnitDto BuyerUnit(int leadId, int unitId, string unitNumber, string project) =>
        new(leadId, 8, "Sold", unitId, unitNumber, 3, 2, 12, 79, project, null, 1, "Buyer");

    /// <summary>A session as the OTP flow produces it (<paramref name="serverProof"/>: challenge + CRM customer/lead attached), or an agent-asserted one.</summary>
    public Guid AddSession(
        VerificationMethod method, bool confirm, Guid owner, UnitReference unit, ContactReference contact, TimeSpan? lifetime = null,
        bool serverProof = true, int customerId = CustomerId, int leadId = LeadId)
    {
        var id = Guid.NewGuid();
        var now = Clock.GetUtcNow().UtcDateTime;
        var session = new VerificationSession(
            id, owner, unit.UnitReferenceId, contact.ContactReferenceId, unit.UnitNumber, unit.PropertyName, null, null,
            contact.DisplayName, contact.ContactChannel, now, now + (lifetime ?? TimeSpan.FromMinutes(30)), null);
        if (serverProof)
        {
            session.AttachOtpProof(Guid.NewGuid(), customerId, leadId);
        }

        if (confirm)
        {
            session.Confirm(now, method);
        }

        Sessions.AddAsync(session);
        return id;
    }

    /// <summary>A document CRM lists for the verified customer's verified unit (lead 12345).</summary>
    public CrmDocumentRecord Own(string recordId, string label = "Sale and Purchase Agreement", CrmDocumentType type = CrmDocumentType.Contract) =>
        Gateway.Add(CustomerId, LeadId, type, recordId, label);
}
