namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>
/// One request to send a customer a copy of a CRM document, and what became of
/// it — the idempotency record that stops Genesys retries sending a document
/// twice, and the durable trail of who was sent what.
///
/// <para>
/// <b>Unique per (caller, idempotency key)</b> — a database index, so two
/// concurrent identical requests cannot both claim the send. The row holds
/// identifiers and status only: never the document, never the recipient's
/// address (only a masked form for support), never the customer's phone.
/// </para>
/// </summary>
public class CrmDocumentDeliveryRequest
{
    public const int IdempotencyKeyMaxLength = 128;
    public const int RecordIdMaxLength = 100;
    public const int FailureCodeMaxLength = 64;
    public const int MaskedDestinationMaxLength = 120;

    public long CrmDocumentDeliveryRequestId { get; private set; }

    /// <summary>The integration account that made the request (the Genesys service account).</summary>
    public Guid CallerEmployeeId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>SHA-256 over the request's meaning (session, type, record, channel). The same key with a different fingerprint is a client bug and is refused.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public Guid VerificationSessionId { get; private set; }
    public CrmDocumentType DocumentType { get; private set; }

    /// <summary>The CRM record that was (or is being) sent. Always a record the verified customer owns.</summary>
    public string CrmRecordId { get; private set; } = string.Empty;

    public DocumentDeliveryChannel Channel { get; private set; }
    public string? MaskedDestination { get; private set; }
    public DocumentDeliveryStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }

    private CrmDocumentDeliveryRequest() { }

    public CrmDocumentDeliveryRequest(
        Guid callerEmployeeId, string idempotencyKey, string fingerprint, Guid verificationSessionId,
        CrmDocumentType documentType, string crmRecordId, DocumentDeliveryChannel channel,
        string? maskedDestination, DateTime nowUtc)
    {
        if (callerEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("CallerEmployeeId is required.", nameof(callerEmployeeId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(crmRecordId);

        CallerEmployeeId = callerEmployeeId;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        VerificationSessionId = verificationSessionId;
        DocumentType = documentType;
        CrmRecordId = crmRecordId.Length <= RecordIdMaxLength ? crmRecordId : crmRecordId[..RecordIdMaxLength];
        Channel = channel;
        MaskedDestination = Truncate(maskedDestination, MaskedDestinationMaxLength);
        Status = DocumentDeliveryStatus.InProgress;
        AttemptCount = 1;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>An in-progress claim whose worker evidently died: older than <paramref name="staleAfter"/>, so a retry may take it over.</summary>
    public bool IsStale(DateTime nowUtc, TimeSpan staleAfter) =>
        Status == DocumentDeliveryStatus.InProgress && nowUtc - UpdatedAtUtc > staleAfter;

    /// <summary>Claims a retry of a failed (or abandoned) attempt. Refused for a Sent request — that one is final.</summary>
    public void Restart(DateTime nowUtc)
    {
        if (Status == DocumentDeliveryStatus.Sent)
        {
            throw new InvalidOperationException("A request that was sent is final and is never retried.");
        }

        Status = DocumentDeliveryStatus.InProgress;
        FailureCode = null;
        AttemptCount++;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkSent(DateTime nowUtc)
    {
        Status = DocumentDeliveryStatus.Sent;
        FailureCode = null;
        SentAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkFailed(string failureCode, DateTime nowUtc)
    {
        if (Status == DocumentDeliveryStatus.Sent)
        {
            return;
        }

        Status = DocumentDeliveryStatus.Failed;
        FailureCode = Truncate(failureCode, FailureCodeMaxLength);
        UpdatedAtUtc = nowUtc;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
