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

    /// <summary>
    /// Non-null when this channel must refuse every delivery right now (e.g. verification test mode is enabled but invalid).
    /// Checked before a request is claimed or a document fetched, so a blocked environment creates no delivery row and downloads nothing.
    /// </summary>
    string? BlockedReason => null;

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

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Code">Machine-readable reason for a failure.</param>
/// <param name="TestRedirected">True when verification test mode sent the message to the test mailbox instead of the customer's address.</param>
public sealed record DocumentDeliveryResult(DocumentDeliveryOutcome Outcome, string? Code = null, bool TestRedirected = false);

/// <summary>The repository over <see cref="CrmDocumentDeliveryRequest"/>.</summary>
public interface ICrmDocumentDeliveryRepository
{
    Task<CrmDocumentDeliveryRequest?> GetByKeyAsync(Guid callerEmployeeId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// A request that already SENT this same record for this session and channel since <paramref name="sinceUtc"/> — or that was
    /// handed to the channel (<see cref="DocumentDeliveryStatus.Dispatching"/>) in that window and never recorded as failed: either
    /// way the document may be in the mailbox. The second line of duplicate defence, for a retry under a new idempotency key.
    /// </summary>
    Task<CrmDocumentDeliveryRequest?> FindRecentSentAsync(
        Guid verificationSessionId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel,
        DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>The agent-desk counterpart of <see cref="FindRecentSentAsync"/>: scoped by CRM customer + lead, never by a verification session.</summary>
    Task<CrmDocumentDeliveryRequest?> FindRecentSentForAgentAsync(
        int crmCustomerId, int crmLeadId, CrmDocumentType type, string crmRecordId, DocumentDeliveryChannel channel,
        DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many OTHER requests of this verification session are still being processed — claimed or handed to the channel
    /// with their last update at or after <paramref name="activeSinceUtc"/> (older ones are abandoned, not in flight). Decides whether
    /// the conversation's document workflow is complete.
    /// </summary>
    Task<int> CountInFlightForSessionAsync(
        Guid verificationSessionId, long excludingRequestId, DateTime activeSinceUtc, CancellationToken cancellationToken = default);

    Task AddAsync(CrmDocumentDeliveryRequest request, CancellationToken cancellationToken = default);
}
