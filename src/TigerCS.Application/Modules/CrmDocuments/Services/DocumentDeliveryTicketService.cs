using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>How one document request ended, as far as its ticket is concerned.</summary>
public enum DocumentDeliveryTicketEvent
{
    /// <summary>The channel accepted the document.</summary>
    Sent,

    /// <summary>The document was found but could not be delivered (or could not be found / fetched).</summary>
    Failed,

    /// <summary>Handed to the provider but the outcome was never recorded — it may be in the mailbox.</summary>
    Unconfirmed
}

/// <summary>Who asked: the integration account, the verified session, the conversation the chatbot named, the ticket that already consumed the session, and what the session proves about the customer (CRM id, phone).</summary>
public sealed class DocumentDeliveryTicketContext(
    Guid callerEmployeeId, Guid verificationSessionId, string? conversationId, long? consumedByTicketId, int crmCustomerId, string? contactPhone)
{
    public Guid CallerEmployeeId { get; } = callerEmployeeId;
    public Guid VerificationSessionId { get; } = verificationSessionId;
    public string? ConversationId { get; } = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
    public long? ConsumedByTicketId { get; } = consumedByTicketId;
    public int CrmCustomerId { get; } = crmCustomerId;
    public string? ContactPhone { get; } = contactPhone;

    /// <summary>The delivery channel's own failure code, when it gave one — recorded as the actual reason.</summary>
    public string? ProviderFailureCode { get; set; }
}

/// <summary>One request's outcome: the event, document type, record, machine-readable code, the actual reason text, and the delivery request id (excluded when counting what is still in flight).</summary>
public sealed record DocumentDeliveryTicketUpdate(
    DocumentDeliveryTicketEvent Event, string? DocumentType, string? RecordId, string? Code, string? Reason, long? DeliveryRequestId);

/// <summary>Port so the document service stays testable and the ticket side is optional.</summary>
public interface IDocumentDeliveryTicketHandler
{
    Task HandleAsync(DocumentDeliveryTicketContext context, DocumentDeliveryTicketUpdate update, CancellationToken cancellationToken = default);
}

/// <summary>
/// Closes — through the existing lifecycle (Resolved → Closed, audit, history, Outbox) — the ticket behind a Genesys
/// document request, once the request's workflow has ended.
///
/// <para>
/// <b>Which ticket.</b> Only one the existing associations name: the ticket of the Genesys conversation the chatbot sent
/// (<c>conversationId</c> → its interaction), else the ticket that consumed the verification session. It must still be open, belong to
/// the same CRM customer when it has one, and — for a conversation — have been started from the verified contact's phone number.
/// Anything else is left alone: this never searches for "a ticket of this customer".
/// </para>
///
/// <para>
/// <b>When.</b> Sent closes as Resolved with the fixed reason; a failure closes as Cancelled with
/// "Document delivery failed — [actual reason]" and the code, document type and time in the history/audit. Both wait while another
/// request of the same verification session is still being processed. An unconfirmed outcome never closes anything and is
/// never described as success or failure — it is recorded on the ticket for a person. A failed ticket is never transferred.
/// Re-running on a closed ticket changes nothing. Failures here never reach the caller: the document has already been dealt with.
/// </para>
/// </summary>
public sealed class DocumentDeliveryTicketService(
    CrmDocumentOptions options,
    IGenesysConversationRepository conversationRepository,
    ITicketRepository ticketRepository,
    ICrmDocumentDeliveryRepository deliveryRepository,
    TicketLifecycleAppService lifecycleAppService,
    IAuditEntryWriter auditWriter,
    ICustomerVerificationUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<DocumentDeliveryTicketService> logger) : IDocumentDeliveryTicketHandler
{
    public const string SentReason = "Requested document successfully sent via email.";
    public const string FailureReasonPrefix = "Document delivery failed — ";
    public const string TicketAuditEntityType = "Ticket";

    public async Task HandleAsync(
        DocumentDeliveryTicketContext context, DocumentDeliveryTicketUpdate update, CancellationToken cancellationToken = default)
    {
        try
        {
            var ticket = await FindTicketAsync(context, cancellationToken);
            if (ticket is null)
            {
                return;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var detail = $"DocumentType={update.DocumentType};RecordId={update.RecordId};ErrorCode={update.Code ?? "(none)"};DeliveryRequestId={update.DeliveryRequestId}";

            if (update.Event == DocumentDeliveryTicketEvent.Unconfirmed)
            {
                await RecordOnTicketAsync(context, ticket, "DocumentDeliveryUnconfirmed",
                    $"{detail};AtUtc={now:O};Outcome=Unconfirmed — the document may already be in the customer's mailbox; it was not resent and the ticket was not closed.");
                return;
            }

            var staleAfter = TimeSpan.FromMinutes(Math.Max(1, options.InProgressStaleAfterMinutes));
            var inFlight = await deliveryRepository.CountInFlightForSessionAsync(
                context.VerificationSessionId, update.DeliveryRequestId ?? 0, now - staleAfter, cancellationToken);

            if (update.Event == DocumentDeliveryTicketEvent.Sent)
            {
                if (inFlight == 0)
                {
                    await lifecycleAppService.CloseForDocumentDeliveryAsync(
                        context.CallerEmployeeId, ticket.TicketId, ResolutionOutcome.Resolved, SentReason, detail, cancellationToken);
                }

                return;
            }

            var actual = string.IsNullOrWhiteSpace(update.Reason) ? "reason not reported" : update.Reason.Trim();
            if (!string.IsNullOrWhiteSpace(context.ProviderFailureCode))
            {
                actual += $" (provider code {context.ProviderFailureCode})";
            }

            var reason = FailureReasonPrefix + actual;
            var failureDetail = $"{detail};FailedAtUtc={now:O}";
            if (inFlight > 0)
            {
                // The workflow is not over: keep the failure on the ticket's trail; the last request to finish closes it.
                await RecordOnTicketAsync(context, ticket, "DocumentDeliveryFailed", $"{failureDetail};Reason={actual}");
                return;
            }

            await lifecycleAppService.CloseForDocumentDeliveryAsync(
                context.CallerEmployeeId, ticket.TicketId, ResolutionOutcome.Cancelled, reason, failureDetail, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not apply the outcome of document request {DeliveryRequestId} to its ticket.", update.DeliveryRequestId);
        }
    }

    private async Task<Ticket?> FindTicketAsync(DocumentDeliveryTicketContext context, CancellationToken cancellationToken)
    {
        long ticketId;
        if (context.ConversationId is { } conversationId)
        {
            var interaction = await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken);
            if (interaction is null || !SamePerson(interaction.CustomerPhone, context.ContactPhone))
            {
                return null;
            }

            ticketId = interaction.TicketId;
        }
        else if (context.ConsumedByTicketId is { } consumed)
        {
            ticketId = consumed;
        }
        else
        {
            return null;
        }

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null
            || ticket.TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || (ticket.CrmBuyerCustomerId is { } ticketCustomer && ticketCustomer != context.CrmCustomerId))
        {
            return null;
        }

        return ticket;
    }

    /// <summary>Equal when both are phone numbers that normalise the same; if either is not a number there is nothing to contradict.</summary>
    private static bool SamePerson(string? conversationPhone, string? verifiedPhone)
    {
        if (!CustomerPhoneNumber.LooksLikeNumber(conversationPhone) || !CustomerPhoneNumber.LooksLikeNumber(verifiedPhone))
        {
            return true;
        }

        return CustomerPhoneNumber.AreSameNumber(conversationPhone, verifiedPhone);
    }

    private async Task RecordOnTicketAsync(DocumentDeliveryTicketContext context, Ticket ticket, string action, string after)
    {
        await auditWriter.WriteAsync(
            context.CallerEmployeeId, action, TicketAuditEntityType, ticket.TicketId.ToString(),
            beforeValue: ticket.TicketStatus.ToString(), afterValue: after, Guid.NewGuid(), CancellationToken.None);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }
}
