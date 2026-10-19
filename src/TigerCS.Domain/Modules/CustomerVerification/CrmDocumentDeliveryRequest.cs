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

    /// <summary>Which door asked: a customer proven by a verified OTP session (Genesys), or a signed-in agent on Customer Details.</summary>
    public DocumentDeliveryOrigin Origin { get; private set; }

    /// <summary>
    /// The real verification session that proved the customer — <b>set for <see cref="DocumentDeliveryOrigin.GenesysVerifiedSession"/>
    /// and null for <see cref="DocumentDeliveryOrigin.AgentDesk"/></b>, which has no verification session and must never be given
    /// an invented one.
    /// </summary>
    public Guid? VerificationSessionId { get; private set; }

    /// <summary>CRM customer the copy is for — set for <see cref="DocumentDeliveryOrigin.AgentDesk"/> (the agent route's only scope).</summary>
    public int? CrmCustomerId { get; private set; }

    /// <summary>CRM lead (the unit) the copy is for — set for <see cref="DocumentDeliveryOrigin.AgentDesk"/>.</summary>
    public int? CrmLeadId { get; private set; }

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

    /// <summary>A Genesys-origin delivery: proven by a real, verified OTP session.</summary>
    public CrmDocumentDeliveryRequest(
        Guid callerEmployeeId, string idempotencyKey, string fingerprint, Guid verificationSessionId,
        CrmDocumentType documentType, string crmRecordId, DocumentDeliveryChannel channel,
        string? maskedDestination, DateTime nowUtc)
        : this(callerEmployeeId, idempotencyKey, fingerprint, DocumentDeliveryOrigin.GenesysVerifiedSession,
            RequireSession(verificationSessionId), null, null, documentType, crmRecordId, channel, maskedDestination, nowUtc)
    {
    }

    /// <summary>
    /// An agent-desk delivery: scoped to the CRM customer and lead the agent acted for, with <b>no</b> verification session
    /// (the column stays null — an invented id there would look like a session reference to every consumer of it).
    /// </summary>
    public static CrmDocumentDeliveryRequest ForAgentDesk(
        Guid callerEmployeeId, string idempotencyKey, string fingerprint, int crmCustomerId, int crmLeadId,
        CrmDocumentType documentType, string crmRecordId, DocumentDeliveryChannel channel, string? maskedDestination, DateTime nowUtc)
    {
        if (crmCustomerId <= 0 || crmLeadId <= 0)
        {
            throw new ArgumentException("An agent-desk delivery needs the CRM customer and lead.");
        }

        return new CrmDocumentDeliveryRequest(
            callerEmployeeId, idempotencyKey, fingerprint, DocumentDeliveryOrigin.AgentDesk, null, crmCustomerId, crmLeadId,
            documentType, crmRecordId, channel, maskedDestination, nowUtc);
    }

    private CrmDocumentDeliveryRequest(
        Guid callerEmployeeId, string idempotencyKey, string fingerprint, DocumentDeliveryOrigin origin, Guid? verificationSessionId,
        int? crmCustomerId, int? crmLeadId, CrmDocumentType documentType, string crmRecordId, DocumentDeliveryChannel channel,
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
        Origin = origin;
        VerificationSessionId = verificationSessionId;
        CrmCustomerId = crmCustomerId;
        CrmLeadId = crmLeadId;
        DocumentType = documentType;
        CrmRecordId = crmRecordId.Length <= RecordIdMaxLength ? crmRecordId : crmRecordId[..RecordIdMaxLength];
        Channel = channel;
        MaskedDestination = Truncate(maskedDestination, MaskedDestinationMaxLength);
        Status = DocumentDeliveryStatus.InProgress;
        AttemptCount = 1;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    private static Guid RequireSession(Guid verificationSessionId) =>
        verificationSessionId != Guid.Empty
            ? verificationSessionId
            : throw new ArgumentException("A Genesys-origin delivery needs the real verification session.", nameof(verificationSessionId));

    /// <summary>True when this row was made for exactly this agent scope (customer + lead).</summary>
    public bool IsAgentScope(int crmCustomerId, int crmLeadId) =>
        Origin == DocumentDeliveryOrigin.AgentDesk && CrmCustomerId == crmCustomerId && CrmLeadId == crmLeadId;

    /// <summary>An in-progress claim whose worker evidently died: older than <paramref name="staleAfter"/>, so a retry may take it over.</summary>
    public bool IsStale(DateTime nowUtc, TimeSpan staleAfter) =>
        Status == DocumentDeliveryStatus.InProgress && nowUtc - UpdatedAtUtc > staleAfter;

    /// <summary>
    /// True when the document may already be in the customer's mailbox: it was accepted (<see cref="DocumentDeliveryStatus.Sent"/>) or
    /// it was handed to the channel and the outcome was never recorded (<see cref="DocumentDeliveryStatus.Dispatching"/>).
    /// </summary>
    public bool MayHaveBeenDelivered => Status is DocumentDeliveryStatus.Sent or DocumentDeliveryStatus.Dispatching;

    /// <summary>
    /// Records — and the caller must SAVE this before calling the delivery channel — that the document is about to be handed over.
    /// From here a crash or a failed save can no longer make the request look like it never reached the provider.
    /// </summary>
    public void MarkDispatching(DateTime nowUtc)
    {
        if (Status != DocumentDeliveryStatus.InProgress)
        {
            throw new InvalidOperationException("Only a claimed (in-progress) request can start dispatching.");
        }

        Status = DocumentDeliveryStatus.Dispatching;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Claims a retry of a failed (or abandoned) attempt. Refused for a Sent or Dispatching request — those may already have been delivered.</summary>
    public void Restart(DateTime nowUtc)
    {
        if (MayHaveBeenDelivered)
        {
            throw new InvalidOperationException("A request that was sent, or handed to the delivery channel, is never retried.");
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
