using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>
/// "Send me a copy of my contract / reservation form / unit layout /
/// registration receipt" — the chatbot's request, answered end to end.
///
/// <para>
/// <b>Identity is proven, not asserted.</b> The only identity input is a
/// <see cref="VerificationSession"/> from the existing verification flow: it
/// must be owned by the calling integration account, confirmed (or already
/// consumed by a ticket), unexpired, and verified by a method this API accepts
/// (<see cref="CrmDocumentOptions.AcceptedVerificationMethods"/> — a one-time
/// code or an authenticated digital user by default). A phone number or a
/// customer id is not even a field of the request.
/// </para>
///
/// <para>
/// <b>Ownership is enforced twice.</b> CRM is asked for the records of this
/// verified unit and contact; whatever comes back is filtered again here, and
/// the document fetched for sending is checked once more against the session
/// before a byte leaves. A record id the caller names that is not in the
/// customer's own list is an ownership mismatch — never fetched, so a guessed
/// id cannot retrieve another customer's document.
/// </para>
///
/// <para>
/// <b>Never an arbitrary document.</b> Several matching records → the choices
/// are returned and nothing is sent; the chatbot asks the customer and calls
/// again with <c>recordId</c>.
/// </para>
///
/// <para>
/// <b>Never twice.</b> The caller's idempotency key claims the send in a
/// unique-indexed row before anything is fetched or sent. A replay of a sent
/// request answers Sent without sending; a concurrent replay answers Queued;
/// a failed one may be retried; the same key with a different request is
/// refused. A retry under a <i>new</i> key within
/// <see cref="CrmDocumentOptions.DuplicateSuppressionMinutes"/> is caught by
/// the second check (same session, document, channel) and also sends nothing.
/// </para>
///
/// <para>
/// <b>Private.</b> The document travels as an attachment to the customer's own
/// email address as held by CRM — no link, so there is nothing to expire or
/// leak — and is never stored or logged here.
/// </para>
/// </summary>
public sealed partial class CrmDocumentCopyAppService(
    CrmDocumentOptions options,
    IVerificationSessionRepository sessionRepository,
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    ICrmDocumentGateway documentGateway,
    ICrmDocumentDeliveryRepository deliveryRepository,
    IEnumerable<IDocumentDeliveryChannelSender> channelSenders,
    ICustomerVerificationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<CrmDocumentCopyAppService> logger)
{
    public const string AuditEntityType = "CrmDocumentDelivery";

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,128}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public async Task<CrmDocumentCopyResult> SendAsync(
        Guid callerEmployeeId, CrmDocumentCopyRequestDto request, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return Fail(CrmDocumentCopyStatus.Disabled, CrmDocumentCodes.DocumentCopyDisabled,
                "Sending document copies is switched off (CrmDocuments:Enabled).");
        }

        // ---- 1. Shape of the request ----
        if (string.IsNullOrWhiteSpace(idempotencyKey) || !IdempotencyKeyPattern().IsMatch(idempotencyKey.Trim()))
        {
            return Invalid("An Idempotency-Key header of 1–128 letters, digits and . _ : - is required, and must be reused on a retry.");
        }

        var key = idempotencyKey.Trim();

        if (request.VerificationSessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            return Invalid("verificationSessionId is required — the document is released only to a verified customer.");
        }

        if (!TryParseType(request.DocumentType, out var type))
        {
            return Invalid("documentType must be Contract, ReservationForm, UnitLayout or RegistrationReceipt.");
        }

        if (!TryParseChannel(request.DeliveryChannel, out var channel))
        {
            return Invalid("deliveryChannel must be Email, WhatsApp or Sms (only Email is integrated).");
        }

        if (channel != DocumentDeliveryChannel.Email || channelSenders.All(s => s.Channel != channel))
        {
            return Fail(CrmDocumentCopyStatus.DeliveryFailed, CrmDocumentCodes.DeliveryChannelNotIntegrated,
                $"No {channel} delivery integration exists in TigerCS or CRM. Documents can be sent by Email only.",
                documentType: type.ToString(), channel: channel.ToString(), retryable: false);
        }

        var requestedRecordId = string.IsNullOrWhiteSpace(request.RecordId) ? null : request.RecordId.Trim();
        var requestedUnitId = string.IsNullOrWhiteSpace(request.CrmUnitId) ? null : request.CrmUnitId.Trim();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, options.InProgressStaleAfterMinutes));

        // ---- 2. A replay of the same request ----
        var existing = await deliveryRepository.GetByKeyAsync(callerEmployeeId, key, cancellationToken);
        if (existing is not null)
        {
            var replay = ReplayOrNull(existing, sessionId, type, requestedRecordId, channel, now, staleAfter);
            if (replay is not null)
            {
                return replay;
            }
        }

        // ---- 3. Who is this customer? Proven by the verification session only. ----
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken);
        var verified = session is not null
            && session.IsOwnedBy(callerEmployeeId)
            && session.Status is VerificationSessionStatus.Confirmed or VerificationSessionStatus.Consumed
            && session.ExpiresAtUtc > now
            && options.IsAccepted(session.VerificationMethod);

        if (!verified)
        {
            // One answer for unknown, someone else's, unconfirmed, expired and
            // weak-method sessions: nothing to learn by probing.
            return Fail(CrmDocumentCopyStatus.VerificationFailed, CrmDocumentCodes.VerificationFailed,
                "The customer is not verified for this request. Complete verification (one-time code or authenticated user) and send its verificationSessionId.");
        }

        var unit = await unitRepository.GetByIdAsync(session!.UnitReferenceId, cancellationToken);
        var contact = await contactRepository.GetByIdAsync(session.ContactReferenceId, cancellationToken);
        if (unit is null || contact is null)
        {
            return Fail(CrmDocumentCopyStatus.VerificationFailed, CrmDocumentCodes.VerificationFailed,
                "The verified unit or contact is no longer on record. Verify the customer again.");
        }

        if (requestedUnitId is not null && !string.Equals(requestedUnitId, unit.CrmUnitId, StringComparison.OrdinalIgnoreCase))
        {
            return Mismatch(type, "The requested unit is not the unit this customer verified.");
        }

        // ---- 4. Which record? Only ones this customer owns. ----
        CrmDocumentListing listing;
        try
        {
            listing = await documentGateway.ListAsync(type, unit.CrmUnitId, contact.CrmContactId, cancellationToken);
        }
        catch (CrmDocumentSourceUnavailableException ex)
        {
            logger.LogWarning(ex, "CRM document source unavailable while listing {DocumentType}.", type);
            return SourceUnavailable(type);
        }

        var owned = listing.Records
            .Where(r => Same(r.CrmUnitId, unit.CrmUnitId) && Same(r.OwnerCrmContactId, contact.CrmContactId))
            .DistinctBy(r => r.RecordId, StringComparer.Ordinal)
            .ToList();

        if (owned.Count != listing.Records.Count)
        {
            logger.LogWarning(
                "CRM returned {Dropped} {DocumentType} record(s) not owned by the verified unit/contact; they were dropped.",
                listing.Records.Count - owned.Count, type);
        }

        CrmDocumentRecord selected;
        if (requestedRecordId is not null)
        {
            var match = owned.FirstOrDefault(r => string.Equals(r.RecordId, requestedRecordId, StringComparison.Ordinal));
            if (match is null)
            {
                return Mismatch(type, "That record does not belong to the verified customer.");
            }

            selected = match;
        }
        else if (owned.Count == 0)
        {
            return Fail(CrmDocumentCopyStatus.DocumentUnavailable, CrmDocumentCodes.DocumentNotFound,
                $"No {Describe(type)} is on record for this customer and unit.", documentType: type.ToString());
        }
        else if (owned.Count > 1)
        {
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.SelectionRequired, CrmDocumentCodes.SelectionRequired,
                $"More than one {Describe(type)} matches. Ask the customer which one, then call again with its recordId.",
                DocumentType: type.ToString(),
                Choices: owned.Select(r => new CrmDocumentChoice(r.RecordId, r.Label, r.UnitNumber ?? unit.UnitNumber, r.IssuedOn)).ToList());
        }
        else
        {
            selected = owned[0];
        }

        // A retried key is bound to the record it first claimed: if CRM's data
        // moved so that the same request now selects a different record, that
        // is a different request and needs a new key.
        if (existing is not null && !string.Equals(existing.CrmRecordId, selected.RecordId, StringComparison.Ordinal))
        {
            return Fail(CrmDocumentCopyStatus.IdempotencyConflict, CrmDocumentCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different record. Use a new key for a new request.");
        }

        // ---- 5. Where does it go? CRM's email for this contact — never the caller's. ----
        var destination = listing.CustomerEmail?.Trim();
        if (string.IsNullOrEmpty(destination) || !EmailPattern().IsMatch(destination))
        {
            return Fail(CrmDocumentCopyStatus.DeliveryFailed, CrmDocumentCodes.DeliveryDestinationUnavailable,
                "CRM has no valid email address on record for this customer, so the document cannot be delivered.",
                documentType: type.ToString(), recordId: selected.RecordId, channel: channel.ToString(), retryable: false);
        }

        var masked = MaskEmail(destination);

        // ---- 6. Second line of duplicate defence: same send under a new key ----
        var recent = await deliveryRepository.FindRecentSentAsync(
            sessionId, type, selected.RecordId, channel,
            now - TimeSpan.FromMinutes(Math.Max(0, options.DuplicateSuppressionMinutes)), cancellationToken);
        if (recent is not null)
        {
            return SentResult(recent, duplicate: true);
        }

        // ---- 7. Claim the send (unique index on caller + key) ----
        var fingerprint = Fingerprint(sessionId, type, selected.RecordId, channel);
        CrmDocumentDeliveryRequest claim;
        if (existing is null)
        {
            claim = new CrmDocumentDeliveryRequest(
                callerEmployeeId, key, fingerprint, sessionId, type, selected.RecordId, channel, masked, now);
            await deliveryRepository.AddAsync(claim, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateWriteException)
            {
                // A concurrent identical request claimed it first.
                var winner = await deliveryRepository.GetByKeyAsync(callerEmployeeId, key, cancellationToken);
                return winner is null ? Queued(type, selected.RecordId, channel, masked, null) : ConcurrentOutcome(winner, fingerprint);
            }
        }
        else
        {
            // A failed (or abandoned) attempt being retried — ReplayOrNull already refused every other state.
            claim = existing;
            claim.Restart(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // ---- 8. Fetch, re-verify, send ----
        CrmDocumentContent? content;
        try
        {
            content = await documentGateway.GetContentAsync(type, selected.RecordId, cancellationToken);
        }
        catch (CrmDocumentSourceUnavailableException ex)
        {
            logger.LogWarning(ex, "CRM document source unavailable while fetching a {DocumentType}.", type);
            await FinishFailedAsync(claim, CrmDocumentCodes.DocumentSourceUnavailable, cancellationToken);
            return SourceUnavailable(type, claim);
        }

        if (content is null)
        {
            await FinishFailedAsync(claim, CrmDocumentCodes.DocumentNotFound, cancellationToken);
            return Fail(CrmDocumentCopyStatus.DocumentUnavailable, CrmDocumentCodes.DocumentNotFound,
                $"The {Describe(type)} is no longer available in CRM.", documentType: type.ToString(),
                recordId: selected.RecordId, deliveryRequestId: claim.CrmDocumentDeliveryRequestId);
        }

        if (!Same(content.RecordId, selected.RecordId) || !Same(content.CrmUnitId, unit.CrmUnitId)
            || !Same(content.OwnerCrmContactId, contact.CrmContactId))
        {
            logger.LogError("CRM returned content that does not match the selected, owned {DocumentType} record; nothing was sent.", type);
            await FinishFailedAsync(claim, CrmDocumentCodes.RecordOwnershipMismatch, cancellationToken);
            return Mismatch(type, "The document CRM returned does not belong to the verified customer. Nothing was sent.", claim);
        }

        if (content.Bytes.Length == 0 || content.Bytes.Length > options.MaxAttachmentBytes)
        {
            await FinishFailedAsync(claim, CrmDocumentCodes.DocumentTooLarge, cancellationToken);
            return Fail(CrmDocumentCopyStatus.DeliveryFailed, CrmDocumentCodes.DocumentTooLarge,
                content.Bytes.Length == 0
                    ? "CRM returned an empty document."
                    : $"The document is larger than the {options.MaxAttachmentBytes / (1024 * 1024)} MB delivery limit.",
                documentType: type.ToString(), recordId: selected.RecordId, channel: channel.ToString(),
                retryable: false, deliveryRequestId: claim.CrmDocumentDeliveryRequestId);
        }

        var sender = channelSenders.First(s => s.Channel == channel);
        DocumentDeliveryResult delivery;
        try
        {
            delivery = await sender.SendAsync(
                new DocumentDeliveryMessage(
                    destination, contact.DisplayName ?? string.Empty, type, content.FileName, content.ContentType,
                    content.Bytes, Guid.NewGuid()),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Document delivery threw for a {DocumentType}.", type);
            delivery = new DocumentDeliveryResult(DocumentDeliveryOutcome.TransientFailure, "DELIVERY_EXCEPTION");
        }

        if (delivery.Outcome != DocumentDeliveryOutcome.Sent)
        {
            var retryable = delivery.Outcome == DocumentDeliveryOutcome.TransientFailure;
            await FinishFailedAsync(claim, delivery.Code ?? CrmDocumentCodes.DeliveryFailed, cancellationToken);
            return Fail(CrmDocumentCopyStatus.DeliveryFailed, CrmDocumentCodes.DeliveryFailed,
                retryable
                    ? "The document could not be delivered right now. Retry with the same Idempotency-Key."
                    : "The delivery channel rejected the document. Retrying will not help; check the customer's email on record in CRM.",
                documentType: type.ToString(), recordId: selected.RecordId, channel: channel.ToString(),
                maskedDestination: masked, retryable: retryable, deliveryRequestId: claim.CrmDocumentDeliveryRequestId);
        }

        // ---- 9. Done: record and audit ----
        claim.MarkSent(timeProvider.GetUtcNow().UtcDateTime);
        await auditWriter.WriteAsync(
            callerEmployeeId, "CrmDocumentCopySent", AuditEntityType, claim.CrmDocumentDeliveryRequestId.ToString(),
            beforeValue: null,
            afterValue: $"DocumentType={type};RecordId={selected.RecordId};Channel={channel};Destination={masked};VerificationSessionId={sessionId}",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SentResult(claim, duplicate: false);
    }

    // ----------------------------------------------------------------------

    /// <summary>The answer to a request whose key already has a row — or null when a failed/abandoned row should be retried.</summary>
    private CrmDocumentCopyResult? ReplayOrNull(
        CrmDocumentDeliveryRequest existing, Guid sessionId, CrmDocumentType type, string? requestedRecordId,
        DocumentDeliveryChannel channel, DateTime now, TimeSpan staleAfter)
    {
        // The key is bound to what it first sent. The record is compared only
        // when the replay names one: a retry that omits recordId (single match)
        // is the same request.
        var sameRequest = existing.VerificationSessionId == sessionId
            && existing.DocumentType == type
            && existing.Channel == channel
            && (requestedRecordId is null || string.Equals(existing.CrmRecordId, requestedRecordId, StringComparison.Ordinal));

        if (!sameRequest)
        {
            return Fail(CrmDocumentCopyStatus.IdempotencyConflict, CrmDocumentCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different request. Use a new key for a new request.");
        }

        return existing.Status switch
        {
            DocumentDeliveryStatus.Sent => SentResult(existing, duplicate: true),
            DocumentDeliveryStatus.InProgress when !existing.IsStale(now, staleAfter) =>
                Queued(type, existing.CrmRecordId, channel, existing.MaskedDestination, existing.CrmDocumentDeliveryRequestId),
            _ => null
        };
    }

    private CrmDocumentCopyResult ConcurrentOutcome(CrmDocumentDeliveryRequest winner, string fingerprint) =>
        winner.Fingerprint != fingerprint
            ? Fail(CrmDocumentCopyStatus.IdempotencyConflict, CrmDocumentCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different request. Use a new key for a new request.")
            : winner.Status == DocumentDeliveryStatus.Sent
                ? SentResult(winner, duplicate: true)
                : Queued(winner.DocumentType, winner.CrmRecordId, winner.Channel, winner.MaskedDestination, winner.CrmDocumentDeliveryRequestId);

    private async Task FinishFailedAsync(CrmDocumentDeliveryRequest claim, string code, CancellationToken cancellationToken)
    {
        claim.MarkFailed(code, timeProvider.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static CrmDocumentCopyResult SentResult(CrmDocumentDeliveryRequest r, bool duplicate) => new(
        CrmDocumentCopyStatus.Sent, Message: duplicate
            ? "This document was already sent; nothing was sent again."
            : "The document was sent to the customer's email on record.",
        DocumentType: r.DocumentType.ToString(), RecordId: r.CrmRecordId, DeliveryChannel: r.Channel.ToString(),
        MaskedDestination: r.MaskedDestination, DeliveryRequestId: r.CrmDocumentDeliveryRequestId, Duplicate: duplicate);

    private static CrmDocumentCopyResult Queued(
        CrmDocumentType type, string recordId, DocumentDeliveryChannel channel, string? masked, long? id) => new(
        CrmDocumentCopyStatus.Queued, CrmDocumentCodes.RequestInProgress,
        "An identical request is already being processed; nothing new was sent. Repeat the call with the same Idempotency-Key to get the result.",
        DocumentType: type.ToString(), RecordId: recordId, DeliveryChannel: channel.ToString(),
        MaskedDestination: masked, DeliveryRequestId: id, Duplicate: true);

    private static CrmDocumentCopyResult SourceUnavailable(CrmDocumentType type, CrmDocumentDeliveryRequest? claim = null) => Fail(
        CrmDocumentCopyStatus.DocumentUnavailable, CrmDocumentCodes.DocumentSourceUnavailable,
        "CRM's document source is not available (no Tiger CRM document endpoint is integrated, or CRM cannot be reached). Nothing was sent.",
        documentType: type.ToString(), retryable: true, deliveryRequestId: claim?.CrmDocumentDeliveryRequestId);

    private static CrmDocumentCopyResult Mismatch(CrmDocumentType type, string message, CrmDocumentDeliveryRequest? claim = null) => Fail(
        CrmDocumentCopyStatus.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch, message,
        documentType: type.ToString(), deliveryRequestId: claim?.CrmDocumentDeliveryRequestId);

    private static CrmDocumentCopyResult Invalid(string message) =>
        Fail(CrmDocumentCopyStatus.InvalidRequest, CrmDocumentCodes.InvalidRequest, message);

    private static CrmDocumentCopyResult Fail(
        CrmDocumentCopyStatus status, string code, string message, string? documentType = null, string? recordId = null,
        string? channel = null, string? maskedDestination = null, bool? retryable = null, long? deliveryRequestId = null) =>
        new(status, code, message, documentType, recordId, channel, maskedDestination, deliveryRequestId, Retryable: retryable);

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(Guid sessionId, CrmDocumentType type, string recordId, DocumentDeliveryChannel channel) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:N}|{(byte)type}|{recordId}|{(byte)channel}")));

    internal static bool TryParseType(string? value, out CrmDocumentType type)
    {
        // "Reservation Form", "reservation-form", "UnitLayout" all name one type.
        var normalized = new string((value ?? string.Empty).Where(char.IsLetter).ToArray());
        return Enum.TryParse(normalized, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    private static bool TryParseChannel(string? value, out DocumentDeliveryChannel channel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            channel = DocumentDeliveryChannel.Email;
            return true;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out channel) && Enum.IsDefined(channel);
    }

    private static string Describe(CrmDocumentType type) => type switch
    {
        CrmDocumentType.ReservationForm => "reservation form",
        CrmDocumentType.UnitLayout => "unit layout",
        CrmDocumentType.RegistrationReceipt => "registration receipt",
        _ => "contract"
    };

    internal static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at < 1)
        {
            return "***";
        }

        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var host = dot > 0 ? domain[..dot] : domain;
        var tld = dot > 0 ? domain[dot..] : string.Empty;
        return $"{email[0]}***@{(host.Length > 0 ? host[0] : '*')}***{tld}";
    }
}
