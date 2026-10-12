using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Abstractions;

/// <summary>Delivers one document copy over one channel. Only an email implementation exists; WhatsApp and SMS have none.</summary>
public interface IDocumentDeliveryChannelSender
{
    DocumentDeliveryChannel Channel { get; }

    /// <summary>
    /// True when a <c>Sent</c> outcome from this channel means a real provider accepted the message. False for a recording /
    /// stand-in adapter that reports success without contacting anyone — the agent desk then refuses to send rather than
    /// show a customer-facing "accepted" that is not true.
    /// </summary>
    bool SendsRealMail => true;

    Task<DocumentDeliveryResult> SendAsync(DocumentDeliveryMessage message, CancellationToken cancellationToken = default);
}

public sealed record DocumentDeliveryMessage(
    string Destination, string CustomerDisplayName, CrmDocumentType DocumentType, string FileName, string ContentType,
    byte[] Content, Guid CorrelationId);

public enum DocumentDeliveryOutcome
{
    Sent,
    /// <summary>Worth retrying later (a transport hiccup).</summary>
    TransientFailure,
    /// <summary>Will not work as asked (rejected address, channel switched off).</summary>
    PermanentFailure
}

public sealed record DocumentDeliveryResult(DocumentDeliveryOutcome Outcome, string? Code = null);

/// <summary>The repository over <see cref="CrmDocumentDeliveryRequest"/>.</summary>
public interface ICrmDocumentDeliveryRepository
{
    Task<CrmDocumentDeliveryRequest?> GetByKeyAsync(Guid callerEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>A request that already SENT this same record for this session and channel since <paramref name="sinceUtc"/> — the second line of duplicate defence, for a retry that arrives under a new idempotency key.</summary>
    Task<CrmDocumentDeliveryRequest?> FindRecentSentAsync(
        Guid verificationSessionId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel,
        DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>The agent-desk counterpart of <see cref="FindRecentSentAsync"/>: scoped by CRM customer + lead, never by a verification session.</summary>
    Task<CrmDocumentDeliveryRequest?> FindRecentSentForAgentAsync(
        int crmCustomerId, int crmLeadId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel,
        DateTime sinceUtc, CancellationToken cancellationToken = default);

    Task AddAsync(CrmDocumentDeliveryRequest request, CancellationToken cancellationToken = default);
}
