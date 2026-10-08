using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Domain.Modules.Ticketing;
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
/// <b>Who CRM is asked about.</b> The verified session's contact (on record
/// in CRM, never the caller) gives the phone for the existing CRM buyer
/// lookup; that returns CRM's <c>CustomerID</c> and the customer's units with
/// their <c>LeadID</c>s. The lead is the verified unit, or one the caller names
/// that must be among <i>that customer's</i> units — anything else is an
/// ownership mismatch and CRM is never asked. <c>GetCustomerDocuments</c> is
/// then called with exactly that CustomerID/LeadID.
/// </para>
///
/// <para>
/// <b>Ownership is enforced at every step.</b> The lead belongs to the
/// customer (above); CRM must echo that customer and lead back (the gateway
/// refuses a response that does not); a <c>recordId</c> the caller names must
/// be one CRM just listed for them — never fetched otherwise, so a guessed id
/// cannot retrieve another customer's document; and the file is fetched with
/// the server-side CRM credential from the reference in that same listing,
/// never from anything the caller supplied.
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
    CrmBuyerLookupAppService buyerLookup,
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
            && options.IsAccepted(session.VerificationMethod)
            // Server-side proof: the session came out of a verified OTP challenge and carries the CRM customer and
            // lead that challenge was bound to. An agent-asserted session — whatever method it names — never qualifies.
            && session.ProofChallengeId is not null
            && session.CrmBuyerCustomerId is not null
            && session.CrmBuyerLeadId is not null;

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

        // ---- 4. The CRM customer and lead the proof was bound to — re-confirmed against CRM now ----
        var boundCustomerId = session.CrmBuyerCustomerId!.Value;
        var boundLeadId = session.CrmBuyerLeadId!.Value;

        if (request.CrmLeadId is { } requestedLeadId && requestedLeadId != boundLeadId)
        {
            // The proof covers one unit. Another unit needs its own verification, even for the same customer.
            return Mismatch(type, "The verification covers a different unit. Verify for the requested unit first.");
        }

        if (!CustomerPhoneNumber.LooksLikeNumber(contact.ContactChannel)
            || CustomerPhoneNumber.Normalize(contact.ContactChannel).Length < 7)
        {
            return Fail(CrmDocumentCopyStatus.VerificationFailed, CrmDocumentCodes.CrmCustomerNotResolved,
                "The verified contact has no phone number on record, so the CRM customer cannot be re-confirmed. Nothing was sent.",
                documentType: type.ToString());
        }

        var lookup = await buyerLookup.GetBuyerByPhoneAsync("+" + CustomerPhoneNumber.Normalize(contact.ContactChannel), cancellationToken);
        switch (lookup.Outcome)
        {
            case CrmBuyerLookupOutcome.Success when lookup.Buyers is { Count: 1 }:
                break;
            case CrmBuyerLookupOutcome.NotFound:
            case CrmBuyerLookupOutcome.AmbiguousCustomerMatch:
            case CrmBuyerLookupOutcome.Success:
                return Fail(CrmDocumentCopyStatus.VerificationFailed, CrmDocumentCodes.CrmCustomerNotResolved,
                    "The verified contact is not a single CRM buyer, so no documents can be released. Nothing was sent.",
                    documentType: type.ToString());
            default:
                logger.LogWarning("CRM buyer lookup {Outcome} while resolving a document request.", lookup.Outcome);
                return SourceUnavailable(type);
        }

        var buyer = lookup.Buyers![0];

        // Same customer, and the bound lead still belongs to them: otherwise the proof no longer describes anyone CRM agrees with.
        var lead = buyer.Customer.CustomerId == boundCustomerId
            ? buyer.Units.FirstOrDefault(u => u.LeadId == boundLeadId)
            : null;
        if (lead is null)
        {
            return Mismatch(type, "The verified customer and unit no longer match CRM. Verify again.");
        }

        var customerId = boundCustomerId;

        // ---- 5. Which document? Only ones CRM lists for this customer and lead. ----
        CrmDocumentListing listing;
        try
        {
            listing = await documentGateway.ListAsync(type, customerId, lead.LeadId, cancellationToken);
        }
        catch (CrmDocumentSourceException ex)
        {
            logger.LogWarning(ex, "CRM document listing failed ({Failure}) for a {DocumentType} request.", ex.Failure, type);
            return SourceFailure(type, ex.Failure);
        }

        var records = listing.Records.DistinctBy(r => r.RecordId, StringComparer.Ordinal).ToList();

        CrmDocumentRecord selected;
        if (requestedRecordId is not null)
        {
            var match = records.FirstOrDefault(r => string.Equals(r.RecordId, requestedRecordId, StringComparison.Ordinal));
            if (match is null)
            {
                return Mismatch(type, "That record does not belong to the verified customer.");
            }

            selected = match;
        }
        else if (records.Count == 0)
        {
            return Fail(CrmDocumentCopyStatus.DocumentUnavailable, CrmDocumentCodes.DocumentNotFound,
                $"No {Describe(type)} is on record for this customer and unit.", documentType: type.ToString());
        }
        else if (listing.SelectionRequired || records.Count > 1)
        {
            // CRM says (or the count shows) there is more than one: never pick one for the customer.
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.SelectionRequired, CrmDocumentCodes.SelectionRequired,
                $"More than one {Describe(type)} matches. Ask the customer which one, then call again with its recordId.",
                DocumentType: type.ToString(), ChoiceKind: "Document",
                Choices: records.Select(r => new CrmDocumentChoice(r.RecordId, r.Label, lead.UnitNumber, null)).ToList());
        }
        else
        {
            selected = records[0];
        }

        // A retried key is bound to the record it first claimed: if CRM's data
        // moved so that the same request now selects a different record, that
        // is a different request and needs a new key.
        if (existing is not null && !string.Equals(existing.CrmRecordId, selected.RecordId, StringComparison.Ordinal))
        {
            return Fail(CrmDocumentCopyStatus.IdempotencyConflict, CrmDocumentCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different record. Use a new key for a new request.");
        }

        // ---- Where does it go? CRM's email for this customer — never the caller's. ----
        var destination = buyer.Customer.Email?.Trim();
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

        // ---- 8. Fetch with the server-side CRM credential, then send ----
        CrmDocumentContent? content;
        try
        {
            content = await documentGateway.DownloadAsync(selected, cancellationToken);
        }
        catch (CrmDocumentSourceException ex)
        {
            logger.LogWarning(ex, "CRM document download failed ({Failure}) for a {DocumentType}.", ex.Failure, type);
            var code = SourceFailureCode(ex.Failure);
            await FinishFailedAsync(claim, code, cancellationToken);
            return SourceFailure(type, ex.Failure, claim);
        }

        if (content is null)
        {
            await FinishFailedAsync(claim, CrmDocumentCodes.DocumentNotFound, cancellationToken);
            return Fail(CrmDocumentCopyStatus.DocumentUnavailable, CrmDocumentCodes.DocumentNotFound,
                $"The {Describe(type)} file is no longer available in CRM.", documentType: type.ToString(),
                recordId: selected.RecordId, deliveryRequestId: claim.CrmDocumentDeliveryRequestId);
        }

        if (!string.Equals(content.RecordId, selected.RecordId, StringComparison.Ordinal))
        {
            logger.LogError("CRM download returned content for a different record than the one selected; nothing was sent.");
            await FinishFailedAsync(claim, CrmDocumentCodes.RecordOwnershipMismatch, cancellationToken);
            return Mismatch(type, "The document CRM returned does not match the selected record. Nothing was sent.", claim);
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
                    destination, buyer.Customer.FullNameEnglish ?? contact.DisplayName ?? string.Empty, type, content.FileName, content.ContentType,
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
            afterValue: $"DocumentType={type};CrmCustomerId={customerId};CrmLeadId={lead.LeadId};RecordId={selected.RecordId};Channel={channel};Destination={masked};VerificationSessionId={sessionId}",
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

    private static CrmDocumentCopyResult SourceUnavailable(CrmDocumentType type, CrmDocumentDeliveryRequest? claim = null) =>
        SourceFailure(type, CrmDocumentSourceFailure.Unavailable, claim);

    /// <summary>Each CRM failure has its own code; only the transient one is marked retryable.</summary>
    private static CrmDocumentCopyResult SourceFailure(
        CrmDocumentType type, CrmDocumentSourceFailure failure, CrmDocumentDeliveryRequest? claim = null) => Fail(
        CrmDocumentCopyStatus.DocumentUnavailable,
        SourceFailureCode(failure),
        failure switch
        {
            CrmDocumentSourceFailure.RequestRejected => "CRM rejected the document request as malformed (400). Nothing was sent; this needs a TigerCS fix.",
            CrmDocumentSourceFailure.AuthenticationFailed => "CRM rejected TigerCS's credential (401). Check Crm:SecretKey. Nothing was sent.",
            CrmDocumentSourceFailure.AccessDenied => "CRM denied access to this customer's documents (403). Nothing was sent.",
            CrmDocumentSourceFailure.InvalidResponse => "CRM returned a response that does not match its contract. Nothing was sent.",
            CrmDocumentSourceFailure.ReferenceRejected => "CRM pointed at a file location TigerCS is not allowed to fetch with its credential. Nothing was sent.",
            _ => "CRM's document source is unavailable right now. Nothing was sent; retry with the same Idempotency-Key."
        },
        documentType: type.ToString(), retryable: failure == CrmDocumentSourceFailure.Unavailable,
        deliveryRequestId: claim?.CrmDocumentDeliveryRequestId);

    private static string SourceFailureCode(CrmDocumentSourceFailure failure) => failure switch
    {
        CrmDocumentSourceFailure.RequestRejected => CrmDocumentCodes.CrmRequestRejected,
        CrmDocumentSourceFailure.AuthenticationFailed => CrmDocumentCodes.CrmAuthenticationFailed,
        CrmDocumentSourceFailure.AccessDenied => CrmDocumentCodes.CrmAccessDenied,
        CrmDocumentSourceFailure.InvalidResponse or CrmDocumentSourceFailure.ReferenceRejected => CrmDocumentCodes.CrmInvalidResponse,
        _ => CrmDocumentCodes.DocumentSourceUnavailable
    };

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
        return NamedEnum.TryParse(normalized, out type);
    }

    private static bool TryParseChannel(string? value, out DocumentDeliveryChannel channel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            channel = DocumentDeliveryChannel.Email;
            return true;
        }

        return NamedEnum.TryParse(value, out channel);
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
