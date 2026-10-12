using Microsoft.Extensions.Logging;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.CrmDocuments.Services;

/// <summary>
/// The Customer Details <b>Documents</b> tab: the customer's Reservation Form (CRM type 4), Tiger Contract (5) and
/// Registration Receipt (6) for the selected unit, the file behind each, and "Send to customer".
///
/// <para>
/// <b>No parallel integration.</b> Documents come from the existing <see cref="ICrmDocumentGateway"/>
/// (<c>TicketingSystem/GetCustomerDocuments</c> and the credentialed file fetch); the customer and unit from the
/// existing <see cref="CrmBuyerLookupAppService"/>; sending is <see cref="CrmDocumentCopyAppService"/> — the very
/// service behind <c>/api/genesys/documents/send-copy</c>.
/// </para>
///
/// <para>
/// <b>Ownership, in order, on every call.</b> (1) The customer must be visible to the signed-in agent — the same
/// department-scoped check as the Customer Profile itself, so a customer key the agent cannot open yields nothing.
/// (2) It must be a CRM customer, and CRM must resolve one of the phones TigerCS holds for them to <i>that same CRM
/// customer</i>. (3) The unit (<c>crmLeadId</c>) must be one of that customer's CRM units — a lead id typed into the URL
/// is checked, never trusted. (4) A file or send names a record only if CRM has <i>just listed</i> it for that customer,
/// unit and type; CRM's storage reference never leaves the server, and the gateway refuses a listing that echoes another
/// customer or lead.
/// </para>
/// </summary>
public sealed class CustomerDocumentsAppService(
    CustomerDirectoryAppService directory,
    CrmBuyerLookupAppService buyerLookup,
    ICrmDocumentGateway documentGateway,
    CrmDocumentCopyAppService copyService,
    IEnumerable<IDocumentDeliveryChannelSender> channelSenders,
    CrmDocumentOptions options,
    CustomerNotificationPolicy emailPolicy,
    ILogger<CustomerDocumentsAppService> logger)
{
    /// <summary>The three types the tab shows, in display order.</summary>
    public static readonly IReadOnlyList<CrmDocumentType> TabTypes =
        [CrmDocumentType.ReservationForm, CrmDocumentType.Contract, CrmDocumentType.RegistrationReceipt];

    private const int MaxPhonesTried = 5;

    /// <summary>
    /// The explicit document-send grant (<see cref="CrmDocumentOptions.AgentSendRoles"/>), distinct from being able to see the
    /// customer. The System Administrator override applies through <see cref="AuthorizationGate"/>, as everywhere else.
    /// </summary>
    public bool CanSend(IReadOnlyCollection<string> callerRoles) =>
        AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(r => options.EffectiveAgentSendRoles.Contains(r, StringComparer.Ordinal)));

    public static bool IsTabType(CrmDocumentType type) => TabTypes.Contains(type);

    public static string Label(CrmDocumentType type) => type switch
    {
        CrmDocumentType.ReservationForm => "Reservation Form",
        CrmDocumentType.RegistrationReceipt => "Registration Receipt",
        _ => "Tiger Contract"
    };

    // ---------------------------------------------------------------- list

    public async Task<CustomerDocumentsResult<CustomerDocumentsDto>> ListAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, int? crmLeadId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveCustomerAsync(callerEmployeeId, callerRoles, customerKey, cancellationToken);
        if (resolved.Early is not null)
        {
            return resolved.Early;
        }

        var customer = resolved.Customer!;
        var units = customer.Units
            .DistinctBy(u => u.LeadId)
            .Select(u => new CustomerDocumentUnitDto(u.LeadId, u.UnitNumber, u.ProjectName, u.LeadStatusName))
            .ToList();

        if (units.Count == 0)
        {
            return Ok("NoUnits", "CRM lists no eligible unit for this customer, so there are no documents to show.", units, null);
        }

        int leadId;
        if (crmLeadId is { } requested)
        {
            if (units.All(u => u.LeadId != requested))
            {
                return CustomerDocumentsResult<CustomerDocumentsDto>.Fail(
                    CustomerDocumentsOutcome.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch,
                    "That unit is not one of this customer's units.");
            }

            leadId = requested;
        }
        else if (units.Count == 1)
        {
            leadId = units[0].LeadId;
        }
        else
        {
            // Several units and none chosen: show the selector and nothing else — never guess whose documents to show.
            return Ok("UnitSelectionRequired", "This customer has more than one unit. Select a unit to see its documents.", units, null);
        }

        var groups = await Task.WhenAll(TabTypes.Select(type => ListGroupAsync(type, customer.CustomerId, leadId, cancellationToken)));
        return CustomerDocumentsResult<CustomerDocumentsDto>.Ok(new CustomerDocumentsDto(
            "Ready", null, units, leadId, groups, SendAvailability(callerRoles, customer.Customer.Email, hasAnyDocument: groups.Any(g => g.Documents.Count > 0))));
    }

    private async Task<CustomerDocumentGroupDto> ListGroupAsync(CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken)
    {
        var attachmentType = CrmDocumentTypeMapping.ToCrmAttachmentType(type)!.Value;
        try
        {
            var listing = await documentGateway.ListAsync(type, customerId, leadId, cancellationToken);
            return new CustomerDocumentGroupDto(
                type.ToString(), Label(type), attachmentType, "Ok", null,
                listing.Records.DistinctBy(r => r.RecordId, StringComparer.Ordinal).Select(r => new CustomerDocumentDto(r.RecordId, r.Label)).ToList());
        }
        catch (CrmDocumentSourceException ex)
        {
            logger.LogWarning(ex, "CRM document listing failed ({Failure}) for the Documents tab ({DocumentType}).", ex.Failure, type);
            return new CustomerDocumentGroupDto(type.ToString(), Label(type), attachmentType, "Unavailable", FailureMessage(ex.Failure), []);
        }
    }

    // ---------------------------------------------------------------- file

    public async Task<CustomerDocumentsResult<CrmDocumentContent>> GetFileAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, int crmLeadId, string? documentType, string? recordId,
        CancellationToken cancellationToken = default)
    {
        if (!TryTabType(documentType, out var type) || string.IsNullOrWhiteSpace(recordId))
        {
            return CustomerDocumentsResult<CrmDocumentContent>.Fail(
                CustomerDocumentsOutcome.InvalidRequest, CrmDocumentCodes.InvalidRequest,
                "documentType must be ReservationForm, Contract or RegistrationReceipt, and a recordId is required.");
        }

        var selected = await SelectRecordAsync(callerEmployeeId, callerRoles, customerKey, crmLeadId, type, recordId.Trim(), cancellationToken);
        if (selected.Early is not null)
        {
            return CustomerDocumentsResult<CrmDocumentContent>.Fail(selected.Early.Outcome, selected.Early.Code!, selected.Early.Message!);
        }

        try
        {
            var content = await documentGateway.DownloadAsync(selected.Record!, cancellationToken);
            if (content is null)
            {
                return CustomerDocumentsResult<CrmDocumentContent>.Fail(
                    CustomerDocumentsOutcome.NotFound, CrmDocumentCodes.DocumentNotFound, "The file is no longer available in CRM.");
            }

            // What the gateway returned must be the record asked for — the same guard the send path applies.
            if (!string.Equals(content.RecordId, selected.Record!.RecordId, StringComparison.Ordinal))
            {
                logger.LogError("CRM download returned content for a different record than the one selected; nothing was returned.");
                return CustomerDocumentsResult<CrmDocumentContent>.Fail(
                    CustomerDocumentsOutcome.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch,
                    "The file CRM returned does not match the selected record.");
            }

            return CustomerDocumentsResult<CrmDocumentContent>.Ok(content);
        }
        catch (CrmDocumentSourceException ex)
        {
            logger.LogWarning(ex, "CRM document download failed ({Failure}) for the Documents tab ({DocumentType}).", ex.Failure, type);
            return FromFailure<CrmDocumentContent>(ex.Failure);
        }
    }

    // ---------------------------------------------------------------- send

    public async Task<CrmDocumentCopyResult> SendAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CustomerDocumentSendRequestDto request,
        string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        // The send grant is checked before anything else — before the customer is even resolved, and with no CRM call.
        if (!CanSend(callerRoles))
        {
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.NotPermitted, CrmDocumentCodes.DocumentSendNotPermitted,
                "Your role has not been granted permission to send documents to customers.");
        }

        if (request.CrmLeadId is not { } leadId || !TryTabType(request.DocumentType, out var type) || string.IsNullOrWhiteSpace(request.RecordId))
        {
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.InvalidRequest, CrmDocumentCodes.InvalidRequest,
                "crmLeadId, documentType (ReservationForm, Contract or RegistrationReceipt) and recordId are required.");
        }

        var resolved = await ResolveCustomerAsync(callerEmployeeId, callerRoles, customerKey, cancellationToken);
        if (resolved.Customer is null)
        {
            return ToCopyFailure(resolved.Early!, type);
        }

        var customer = resolved.Customer;
        var unit = customer.Units.FirstOrDefault(u => u.LeadId == leadId);
        if (unit is null)
        {
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch,
                "That unit is not one of this customer's units.", DocumentType: type.ToString());
        }

        // The record is validated against CRM's own listing inside the shared delivery core.
        return await copyService.SendToCustomerAsync(
            callerEmployeeId,
            new AgentDocumentRecipient(customer.CustomerId, unit.LeadId, unit.UnitNumber, customer.Customer.Email, customer.Customer.FullNameEnglish ?? string.Empty),
            type, request.RecordId, idempotencyKey, cancellationToken);
    }

    // ---------------------------------------------------------------- shared

    private CustomerDocumentSendDto SendAvailability(IReadOnlyCollection<string> callerRoles, string? crmEmail, bool hasAnyDocument)
    {
        var email = crmEmail?.Trim();
        var valid = CrmDocumentCopyAppService.IsEmailAddress(email);
        var masked = valid ? CrmDocumentCopyAppService.MaskEmail(email!) : null;

        if (!CanSend(callerRoles))
        {
            return new(false, "Your role has not been granted permission to send documents to customers (CrmDocuments:AgentSendRoles). You can still open and download them.", masked);
        }

        if (!options.Enabled)
        {
            return new(false, "Sending is switched off in this environment (CrmDocuments:Enabled).", masked);
        }

        if (!emailPolicy.Enabled)
        {
            return new(false, "Email delivery is switched off in this environment (EmailNotifications:Enabled), so no attachment can be sent.", masked);
        }

        if (channelSenders.FirstOrDefault(c => c.Channel == DocumentDeliveryChannel.Email) is not { SendsRealMail: true })
        {
            return new(false, "This environment's mail provider only records messages in memory (EmailNotifications:Provider = Recording), so nothing can be sent to a customer. Use the Smtp provider.", masked);
        }

        if (!valid)
        {
            return new(false, "CRM holds no valid email address for this customer. Documents are sent only to the address CRM holds.", null);
        }

        return hasAnyDocument
            ? new(true, null, masked)
            : new(false, "There is no document to send for this unit.", masked);
    }

    private sealed record ResolvedCustomer(int CustomerId, CrmCustomerDto Customer, IReadOnlyList<CrmBuyerUnitDto> Units);

    private sealed record ResolveOutcome(ResolvedCustomer? Customer, CustomerDocumentsResult<CustomerDocumentsDto>? Early);

    private sealed record SelectOutcome(CrmDocumentRecord? Record, CustomerDocumentsResult<CrmDocumentRecord>? Early);

    private async Task<ResolveOutcome> ResolveCustomerAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken)
    {
        var profile = await directory.GetProfileAsync(callerEmployeeId, callerRoles, customerKey, cancellationToken);
        switch (profile.Outcome)
        {
            case CustomerDirectoryProfileOutcome.InvalidKey:
                return Early(CustomerDocumentsOutcome.InvalidRequest, CrmDocumentCodes.InvalidRequest, "The customer key is not valid.");
            case CustomerDirectoryProfileOutcome.NotFound:
                // Not visible to this agent and not existing look the same.
                return Early(CustomerDocumentsOutcome.NotFound, "CUSTOMER_NOT_FOUND", "No customer with that key is visible to you.");
        }

        var p = profile.Response!;
        if (p.IdentityKind != "Crm" || p.CrmBuyerCustomerId is not { } crmCustomerId)
        {
            return new ResolveOutcome(null, CustomerDocumentsResult<CustomerDocumentsDto>.Ok(Empty(
                "NotCrmCustomer", "Documents are held in Tiger CRM, so they are available only for customers identified in CRM.")));
        }

        var phones = p.PhoneNumbers
            .Where(CustomerPhoneNumber.LooksLikeNumber)
            .Select(CustomerPhoneNumber.Normalize)
            .Where(n => n.Length >= 7)
            .Distinct()
            .Take(MaxPhonesTried)
            .ToList();

        var sourceFailed = false;
        foreach (var phone in phones)
        {
            var lookup = await buyerLookup.GetBuyerByPhoneAsync("+" + phone, cancellationToken);
            switch (lookup.Outcome)
            {
                case CrmBuyerLookupOutcome.Success:
                    var match = lookup.Buyers?.FirstOrDefault(b => b.Customer.CustomerId == crmCustomerId);
                    if (match is not null)
                    {
                        return new ResolveOutcome(new ResolvedCustomer(crmCustomerId, match.Customer, match.Units), null);
                    }

                    break;
                case CrmBuyerLookupOutcome.Unavailable or CrmBuyerLookupOutcome.Unauthorized or CrmBuyerLookupOutcome.InvalidResponse:
                    sourceFailed = true;
                    break;
            }
        }

        if (sourceFailed)
        {
            logger.LogWarning("CRM buyer lookup failed while resolving the Documents tab for a CRM customer.");
            return Early(CustomerDocumentsOutcome.SourceUnavailable, CrmDocumentCodes.DocumentSourceUnavailable,
                "CRM could not be reached to confirm this customer, so no documents are shown.");
        }

        return new ResolveOutcome(null, CustomerDocumentsResult<CustomerDocumentsDto>.Ok(Empty(
            "CustomerNotResolved",
            phones.Count == 0
                ? "TigerCS holds no usable phone number for this customer, so CRM cannot confirm whose documents these would be."
                : "CRM does not resolve any phone TigerCS holds to this customer, so no documents are shown.")));
    }

    private async Task<SelectOutcome> SelectRecordAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, int leadId, CrmDocumentType type, string recordId,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCustomerAsync(callerEmployeeId, callerRoles, customerKey, cancellationToken);
        if (resolved.Customer is null)
        {
            var e = resolved.Early!;
            // A resolvable-but-not-CRM / unresolved customer has no files to give.
            return new SelectOutcome(null, e.IsSuccess
                ? CustomerDocumentsResult<CrmDocumentRecord>.Fail(CustomerDocumentsOutcome.NotFound, e.Value!.Status.ToUpperInvariant(), e.Value.Message ?? "No documents.")
                : CustomerDocumentsResult<CrmDocumentRecord>.Fail(e.Outcome, e.Code!, e.Message!));
        }

        if (resolved.Customer.Units.All(u => u.LeadId != leadId))
        {
            return new SelectOutcome(null, CustomerDocumentsResult<CrmDocumentRecord>.Fail(
                CustomerDocumentsOutcome.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch, "That unit is not one of this customer's units."));
        }

        try
        {
            var listing = await documentGateway.ListAsync(type, resolved.Customer.CustomerId, leadId, cancellationToken);
            var record = listing.Records.FirstOrDefault(r => string.Equals(r.RecordId, recordId, StringComparison.Ordinal));
            return record is null
                ? new SelectOutcome(null, CustomerDocumentsResult<CrmDocumentRecord>.Fail(
                    CustomerDocumentsOutcome.OwnershipMismatch, CrmDocumentCodes.RecordOwnershipMismatch,
                    "That record is not one CRM lists for this customer and unit."))
                : new SelectOutcome(record, null);
        }
        catch (CrmDocumentSourceException ex)
        {
            logger.LogWarning(ex, "CRM document listing failed ({Failure}) while resolving a file ({DocumentType}).", ex.Failure, type);
            var failed = FromFailure<CrmDocumentRecord>(ex.Failure);
            return new SelectOutcome(null, failed);
        }
    }

    private static CustomerDocumentsResult<T> FromFailure<T>(CrmDocumentSourceFailure failure) => CustomerDocumentsResult<T>.Fail(
        failure == CrmDocumentSourceFailure.Unavailable ? CustomerDocumentsOutcome.SourceUnavailable : CustomerDocumentsOutcome.SourceRejected,
        failure switch
        {
            CrmDocumentSourceFailure.RequestRejected => CrmDocumentCodes.CrmRequestRejected,
            CrmDocumentSourceFailure.AuthenticationFailed => CrmDocumentCodes.CrmAuthenticationFailed,
            CrmDocumentSourceFailure.AccessDenied => CrmDocumentCodes.CrmAccessDenied,
            CrmDocumentSourceFailure.InvalidResponse or CrmDocumentSourceFailure.ReferenceRejected => CrmDocumentCodes.CrmInvalidResponse,
            _ => CrmDocumentCodes.DocumentSourceUnavailable
        },
        FailureMessage(failure));

    private static string FailureMessage(CrmDocumentSourceFailure failure) => failure switch
    {
        CrmDocumentSourceFailure.RequestRejected => "CRM rejected the document request as malformed (400).",
        CrmDocumentSourceFailure.AuthenticationFailed => "CRM rejected TigerCS's credential (401). Check Crm:SecretKey.",
        CrmDocumentSourceFailure.AccessDenied => "CRM denied access to this customer's documents (403).",
        CrmDocumentSourceFailure.InvalidResponse => "CRM returned a response that does not match its contract.",
        CrmDocumentSourceFailure.ReferenceRejected => "CRM pointed at a file location TigerCS is not allowed to fetch.",
        _ => "CRM's document source is unavailable right now. Try again shortly."
    };

    private static CrmDocumentCopyResult ToCopyFailure(CustomerDocumentsResult<CustomerDocumentsDto> early, CrmDocumentType type)
    {
        if (early.IsSuccess)
        {
            // NotCrmCustomer / CustomerNotResolved: the customer cannot be tied to a CRM record, so nothing is released.
            return new CrmDocumentCopyResult(
                CrmDocumentCopyStatus.VerificationFailed, CrmDocumentCodes.CrmCustomerNotResolved,
                early.Value!.Message, DocumentType: type.ToString());
        }

        return early.Outcome switch
        {
            CustomerDocumentsOutcome.InvalidRequest => new(CrmDocumentCopyStatus.InvalidRequest, early.Code, early.Message, DocumentType: type.ToString()),
            CustomerDocumentsOutcome.NotFound => new(CrmDocumentCopyStatus.VerificationFailed, early.Code, early.Message, DocumentType: type.ToString()),
            _ => new(CrmDocumentCopyStatus.DocumentUnavailable, early.Code, early.Message, DocumentType: type.ToString(), Retryable: true)
        };
    }

    private static bool TryTabType(string? value, out CrmDocumentType type) =>
        CrmDocumentCopyAppService.TryParseType(value, out type) && IsTabType(type);

    private static ResolveOutcome Early(CustomerDocumentsOutcome outcome, string code, string message) =>
        new(null, CustomerDocumentsResult<CustomerDocumentsDto>.Fail(outcome, code, message));

    private static CustomerDocumentsDto Empty(string status, string message) =>
        new(status, message, [], null, [], new CustomerDocumentSendDto(false, message, null));

    private static CustomerDocumentsResult<CustomerDocumentsDto> Ok(
        string status, string message, IReadOnlyList<CustomerDocumentUnitDto> units, int? selected) =>
        CustomerDocumentsResult<CustomerDocumentsDto>.Ok(new CustomerDocumentsDto(
            status, message, units, selected, [], new CustomerDocumentSendDto(false, "Select a unit to see its documents.", null)));
}
