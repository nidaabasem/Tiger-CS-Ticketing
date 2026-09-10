using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Genesys integration phase 1 — the <b>one</b> ingestion flow every Genesys
/// channel converges on. Phone, website chat, WhatsApp and social media do
/// not get separate ticket-creation implementations: each arrives as the
/// same normalized <see cref="GenesysInquiryDto"/> and runs the same
/// sequence — idempotency check, channel resolution, department resolution,
/// customer lookup, ticket creation, conversation link.
///
/// <para>
/// <b>One inquiry, exactly one ticket.</b>
/// <see cref="GenesysInquiryDto.ConversationId"/> is the identity of an
/// inquiry. The first accepted event for a conversation creates a ticket;
/// every retry, redelivery, or duplicate event returns <i>that same ticket</i>
/// (<see cref="GenesysIngestionOutcome.AlreadyIngested"/>) and creates
/// nothing. The guarantee is not only this service's read-before-write: the
/// conversation id carries a unique index on <c>TicketInteractions</c>, so
/// two concurrent deliveries that both pass the read still produce one
/// ticket — the loser's unique-constraint violation is caught below and
/// answered with the winner's ticket.
/// </para>
///
/// <para>
/// <b>A ringing phone never gets here.</b> Reaching this service means a real
/// inquiry exists: an agent picked up the call, or a digital conversation
/// started. TigerCS is not an event receiver for call progress, so there is no
/// Ringing/Answered vocabulary for Genesys to send — the phone flow simply
/// begins at pickup, with the customer lookup and then this.
/// </para>
///
/// <para>
/// <b>Nothing is re-implemented.</b> The intake record comes from
/// <see cref="IntakeRecordAppService"/>, the ticket from
/// <see cref="TicketCreationAppService"/> (ticket number, status history, SLA
/// clock, auto-assignment, outbox, audit and the originating interaction all
/// unchanged), and the customer identification from
/// <see cref="CustomerSearchAppService"/> — the same lookup the New Ticket
/// wizard uses. Genesys never reaches the CRM/PACT/Tasleeh gateways, or any
/// customer database, directly.
/// </para>
///
/// <para>
/// <b>Lookup never gates the inquiry.</b> A customer that cannot be found,
/// or a lookup source that is down, changes nothing: the ticket is created
/// as an unverified customer inquiry with whatever the channel collected
/// (name, email, tower/unit typed into the website form). A CRM/PACT outage
/// must never make a Genesys inquiry disappear, so the lookup is wrapped and
/// its failure recorded rather than propagated. Nor is a customer match ever
/// auto-selected — that stays the agent's explicit act, exactly as
/// everywhere else in this system.
/// </para>
///
/// <para>
/// <b>The ticket is created Unclassified, and nothing is inferred.</b> The
/// department (the website form's Leasing / Customer Service / Maintenance
/// choice, or the queue mapping) is a department and nothing else: it is not
/// a Category, not a Request Type, and not a Priority. An inquiry reaches an
/// agent before anyone has read the request, so the ticket is created with
/// <c>CategoryId = null</c> and <c>RequestTypeId = null</c> rather than
/// filed under a placeholder — and consequently with <b>no SLA period</b>,
/// since the SLA policy is chosen by priority and no real priority exists
/// yet. <c>TicketClassificationAppService</c> completes all of it, on the
/// same ticket, when the agent classifies.
/// </para>
/// </summary>
public sealed class GenesysInquiryIngestionAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    IGenesysQueueMappingRepository queueMappingRepository,
    IDepartmentRepository departmentRepository,
    IChannelRepository channelRepository,
    ITicketRepository ticketRepository,
    IntakeRecordAppService intakeRecordAppService,
    TicketCreationAppService ticketCreationAppService,
    CustomerSearchAppService customerSearchAppService,
    IAuditEntryWriter auditWriter,
    ITicketingUnitOfWork unitOfWork)
{
    public async Task<GenesysIngestionResult> IngestAsync(
        Guid callerEmployeeId, GenesysInquiryDto inquiry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        if (!options.Enabled)
        {
            return GenesysIngestionResult.Failure(GenesysIngestionOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(inquiry.ConversationId))
        {
            return GenesysIngestionResult.Failure(GenesysIngestionOutcome.ConversationIdRequired);
        }

        var conversationId = inquiry.ConversationId.Trim();

        // Idempotency, first — before an intake record or anything else is
        // written, so a retry never leaves debris behind either.
        if (await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken) is { } existing)
        {
            return await AlreadyIngestedAsync(existing, cancellationToken);
        }

        var channel = await channelRepository.GetByCodeAsync(GenesysChannelResolver.CodeFor(inquiry.Channel), cancellationToken);
        if (channel is null || !channel.IsActive)
        {
            return GenesysIngestionResult.Failure(
                GenesysIngestionOutcome.ChannelNotConfigured,
                $"No active channel is configured under code '{GenesysChannelResolver.CodeFor(inquiry.Channel)}'.");
        }

        var department = await ResolveDepartmentAsync(inquiry, cancellationToken);
        if (department is null)
        {
            return GenesysIngestionResult.Failure(
                GenesysIngestionOutcome.DepartmentNotResolved,
                inquiry.QueueId is null
                    ? "The inquiry named no department and carried no queue id."
                    : $"Genesys queue '{inquiry.QueueId}' has no active department mapping.");
        }

        // Customer lookup through the EXISTING flow — enrichment only, and
        // never able to stop the inquiry (see this type's remarks).
        var lookup = await LookUpCustomerAsync(inquiry.CustomerPhone, cancellationToken);

        var intake = await intakeRecordAppService.CreateAsync(
            callerEmployeeId,
            new CreateIntakeRecordRequestDto(
                ChannelId: channel.ChannelId.ToString(),
                // Whatever the channel actually supplied — a withheld caller
                // id or a social DM legitimately carries none, and losing the
                // inquiry over a missing number is exactly what must not
                // happen (see the enforceChannelPhoneRequirement argument).
                PhoneNumber: inquiry.CustomerPhone ?? string.Empty,
                DepartmentId: department.DepartmentId,
                IsUnitRelated: false,
                RawUnitNumberEntered: inquiry.UnitNumber,
                PriorityHint: null),
            cancellationToken,
            enforceChannelPhoneRequirement: false);

        if (intake.Outcome != IntakeRecordOutcome.Success || intake.Response is null)
        {
            // Everything the intake can reject on — the channel and the
            // department — was already resolved and validated above, so this
            // is a genuinely unexpected state (a row deactivated between the
            // two calls). Reported as the specific configuration problem it
            // is, never flattened into a misleading one.
            return GenesysIngestionResult.Failure(
                intake.Outcome == IntakeRecordOutcome.DepartmentNotFound
                    ? GenesysIngestionOutcome.DepartmentNotResolved
                    : GenesysIngestionOutcome.ChannelNotConfigured,
                $"The intake record could not be created ({intake.Outcome}).");
        }

        var request = new CreateTicketRequestDto(
            IntakeRecordId: intake.Response.IntakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            // Unclassified, on purpose: the agent has taken the inquiry but
            // has not read the request yet, so there is neither a category
            // nor a priority to give. The department — which IS known —
            // places the ticket, and nothing else about the request is
            // guessed. Priority in particular is left null rather than
            // defaulted: it drives the dashboard counts, the queue order and
            // the attention ranking, so a default would rank an inquiry
            // nobody has read among tickets a human actually triaged.
            CategoryId: null,
            PriorityId: null,
            RequestSummary: BuildRequestSummary(inquiry),
            // The website form's tower/unit, when the customer typed one —
            // the same manual snapshot an agent would enter by hand. Never a
            // verified match, and never used to run a second lookup.
            ManualProjectName: NullIfBlank(inquiry.TowerName),
            ManualUnitNumber: NullIfBlank(inquiry.UnitNumber),
            DepartmentId: department.DepartmentId,
            // Deliberately no RequestTypeId: nothing about a department, a
            // queue or a channel implies a request type.
            GenesysContext: new GenesysInteractionContextDto(
                ConversationId: conversationId,
                CalledNumber: inquiry.CalledNumber,
                QueueId: inquiry.QueueId,
                QueueName: inquiry.QueueName,
                AgentId: inquiry.AgentId,
                AgentName: inquiry.AgentName,
                InteractionStartedAtUtc: inquiry.StartedAtUtc,
                Direction: inquiry.Direction,
                CustomerName: inquiry.CustomerName ?? lookup.CustomerName,
                CustomerEmail: inquiry.CustomerEmail));

        TicketCreationResult creation;
        try
        {
            creation = await ticketCreationAppService.CreateAsync(callerEmployeeId, request, cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent delivery of the SAME conversation won the race and
            // its interaction row already claimed the conversation id (unique
            // index). That is the guarantee working, not a failure: answer
            // with the ticket that won.
            var winner = await conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken);
            return winner is null
                ? GenesysIngestionResult.CreationFailed(TicketCreationOutcome.TicketNumberCollision)
                : await AlreadyIngestedAsync(winner, cancellationToken);
        }

        if (creation.Outcome != TicketCreationOutcome.Success || creation.Response is null)
        {
            return GenesysIngestionResult.CreationFailed(creation.Outcome);
        }

        // The integration-level audit entry, written after the ticket's own
        // transaction has committed. The ticket's essential "Create" audit is
        // written INSIDE that transaction by ticket creation itself and is
        // atomic with it; this one additionally records the Genesys context
        // the ticket came from (conversation, channel, event, queue, lookup
        // outcome), which only exists once the ticket does.
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.InquiryIngested,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={creation.Response.TicketId};TicketNumber={creation.Response.TicketNumber};"
                + $"Channel={inquiry.Channel};DepartmentId={department.DepartmentId};"
                + $"QueueId={inquiry.QueueId ?? "(none)"};CustomerLookup={lookup.Status};Classification=Unclassified",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return GenesysIngestionResult.Created(creation.Response);
    }

    /// <summary>
    /// The idempotent answer: the ticket this conversation already produced.
    /// Reads the ticket back so a retry receives the same body the original
    /// call did, rather than a bare acknowledgement the caller cannot act on.
    /// </summary>
    private async Task<GenesysIngestionResult> AlreadyIngestedAsync(
        TicketInteraction interaction, CancellationToken cancellationToken)
    {
        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);
        return ticket is null
            ? GenesysIngestionResult.Failure(
                GenesysIngestionOutcome.AlreadyIngested,
                $"Conversation already linked to ticket {interaction.TicketId}.")
            : GenesysIngestionResult.AlreadyIngested(TicketProjection.ToResponseDto(ticket));
    }

    /// <summary>
    /// Department resolution, in the approved priority order:
    /// <list type="number">
    ///   <item><description>the customer's explicit selection (the website chat's Leasing / Customer Service / Maintenance), by id or by department code;</description></item>
    ///   <item><description>otherwise the configured Genesys Queue → Department mapping.</description></item>
    /// </list>
    /// No department name is compared against a literal anywhere, and an
    /// unmapped queue resolves to nothing rather than to a fallback
    /// department — an inquiry routed somewhere nobody configured is a
    /// configuration gap that must be visible.
    /// </summary>
    private async Task<Department?> ResolveDepartmentAsync(GenesysInquiryDto inquiry, CancellationToken cancellationToken)
    {
        if (inquiry.DepartmentId is { } departmentId)
        {
            var selected = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
            return selected is { IsActive: true } ? selected : null;
        }

        if (!string.IsNullOrWhiteSpace(inquiry.DepartmentCode))
        {
            var code = inquiry.DepartmentCode.Trim();
            var all = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);
            return all.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(inquiry.QueueId))
        {
            return null;
        }

        var mapping = await queueMappingRepository.GetActiveByQueueIdAsync(inquiry.QueueId.Trim(), cancellationToken);
        if (mapping is null)
        {
            return null;
        }

        var mapped = await departmentRepository.GetByIdAsync(mapping.DepartmentId, cancellationToken);
        return mapped is { IsActive: true } ? mapped : null;
    }

    /// <summary>
    /// Runs the existing customer lookup for the inquiry's mobile number and
    /// reduces it to what ingestion actually needs: whether a customer was
    /// identified, and a display name to enrich the interaction with. Every
    /// failure mode — no phone, nothing found, a source down, an unexpected
    /// fault — comes back as a status, never as an exception: the inquiry
    /// must survive a CRM/PACT outage.
    /// </summary>
    private async Task<CustomerLookupSummary> LookUpCustomerAsync(string? phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new CustomerLookupSummary("NoPhoneSupplied", null);
        }

        try
        {
            var result = await customerSearchAppService.SearchByPhoneAsync(phoneNumber, cancellationToken);

            var crmName = result.CrmBuyers
                .Select(b => b.Customer.FullNameEnglish ?? b.Customer.FullNameArabic)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            var externalName = result.ExternalSources
                .SelectMany(source => source.Customers)
                .Select(c => c.DisplayName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            var found = result.CrmStatus == "Found"
                || result.ExternalSources.Any(s => s.Status == "Found");

            return new CustomerLookupSummary(
                found ? "Found" : $"NotFound(Crm={result.CrmStatus})",
                crmName ?? externalName);
        }
        catch (Exception ex)
        {
            // Deliberately broad: an unexpected fault in an enrichment step
            // must never cost us the inquiry. What went wrong is recorded on
            // the audit entry; the ticket is created regardless.
            return new CustomerLookupSummary($"LookupFailed({ex.GetType().Name})", null);
        }
    }

    /// <summary>
    /// The ticket's request summary. Whatever the channel collected wins;
    /// otherwise a factual, channel-named placeholder — never an invented
    /// description of what the customer wanted, which only the conversation
    /// itself can say.
    /// </summary>
    private static string BuildRequestSummary(GenesysInquiryDto inquiry)
    {
        if (!string.IsNullOrWhiteSpace(inquiry.Subject))
        {
            return inquiry.Subject.Trim();
        }

        return inquiry.Channel switch
        {
            GenesysChannel.Phone => "Phone call received via Genesys",
            GenesysChannel.WebsiteChat => "Website chat started via Genesys",
            GenesysChannel.WhatsApp => "WhatsApp conversation started via Genesys",
            GenesysChannel.SocialMedia => "Social media conversation started via Genesys",
            _ => "Customer inquiry received via Genesys"
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record CustomerLookupSummary(string Status, string? CustomerName);
}

/// <summary>The audit vocabulary of the Genesys integration, named once so no call site spells one differently.</summary>
public static class GenesysAuditActions
{
    public const string ConversationEntityType = "GenesysConversation";
    public const string InquiryIngested = "GenesysInquiryIngested";
    public const string ConversationEnded = "GenesysConversationEnded";
}
