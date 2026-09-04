using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Business-rule change: a single ticket-creation path for every IntakeRecord
/// — unit-related or not. Customer information from CRM, PACT, or Tasleeh is
/// attached when the agent already resolved a match via
/// <see cref="CustomerLookupAppService"/> (<see cref="CreateTicketRequestDto.UnitReferenceId"/>/
/// <see cref="CreateTicketRequestDto.ContactReferenceId"/>); lack of a match
/// never prevents ticket creation. The only thing every ticket requires is a
/// valid, active Ticket Category. This method inspects the request and the
/// IntakeRecord itself and creates the appropriate <see cref="Ticket"/>
/// internally — <see cref="Ticket.CreateVerified"/> when a resolved
/// unit/contact pair is supplied, <see cref="Ticket.CreateUnverified"/>
/// otherwise — rather than exposing a separate endpoint per case.
///
/// <para>
/// <b>Two SaveChanges calls, in one real transaction.</b> Both
/// <see cref="Ticket"/> and <see cref="IntakeRecord"/> use database-generated
/// identity PKs (bigint), so a <see cref="Ticket"/>'s real <c>TicketId</c> is
/// not known until its own insert commits — but linking the IntakeRecord and
/// writing the requester snapshot/status-history/audit rows all need that
/// real ID. The ticket is therefore inserted alone first; everything else
/// commits in a second call — both wrapped in one
/// <see cref="ITicketingUnitOfWork.BeginTransactionAsync"/> scope. A
/// <c>TicketNumber</c> unique-index collision on the first call
/// (<see cref="DuplicateWriteException"/>) is translated to
/// <see cref="TicketCreationOutcome.TicketNumberCollision"/> — a clean,
/// retryable <c>409</c> rather than an unhandled exception; nothing has been
/// touched yet at that point, so retrying the whole request is always
/// correct.
/// </para>
/// </summary>
public sealed class TicketCreationAppService(
    IIntakeRecordRepository intakeRecordRepository,
    IUnitReferenceRepository unitReferenceRepository,
    IContactReferenceRepository contactReferenceRepository,
    ICategoryRepository categoryRepository,
    IPriorityRepository priorityRepository,
    IDepartmentRepository departmentRepository,
    ITicketRepository ticketRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    IOutboxWriter outboxWriter,
    SlaDueDateService slaDueDateService,
    TimeProvider timeProvider,
    IRequestTypeRepository requestTypeRepository,
    ITicketInteractionRepository interactionRepository,
    TicketAutoAssignmentService autoAssignmentService)
{
    public async Task<TicketCreationResult> CreateAsync(
        Guid callerEmployeeId, CreateTicketRequestDto request, CancellationToken cancellationToken = default)
    {
        var intakeRecord = await intakeRecordRepository.GetByIdAsync(request.IntakeRecordId, cancellationToken);
        if (intakeRecord is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotFound);
        }

        if (intakeRecord.LinkedTicketId is not null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordAlreadyLinked);
        }

        if ((request.UnitReferenceId is null) != (request.ContactReferenceId is null))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.UnitOrContactReferenceMismatch);
        }

        UnitReference? unitReference = null;
        ContactReference? contactReference = null;
        if (request.UnitReferenceId is { } unitReferenceId && request.ContactReferenceId is { } contactReferenceId)
        {
            unitReference = await unitReferenceRepository.GetByIdAsync(unitReferenceId, cancellationToken);
            if (unitReference is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.UnitReferenceNotFound);
            }

            contactReference = await contactReferenceRepository.GetByIdAsync(contactReferenceId, cancellationToken);
            if (contactReference is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.ContactReferenceNotFound);
            }
        }

        // Business-rule change: the real CRM Buyer Lookup match (GET
        // /api/crm/buyers — phone search only). All four CRM ids travel
        // together or not at all, same pairing discipline as
        // UnitReferenceId/ContactReferenceId above.
        var crmBuyerIds = new[]
        {
            request.CrmBuyerCustomerId, request.CrmBuyerLeadId, request.CrmBuyerUnitId, request.CrmBuyerProjectId
        };
        var hasCrmBuyerMatch = crmBuyerIds.All(id => id is not null);
        if (!hasCrmBuyerMatch && crmBuyerIds.Any(id => id is not null))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerReferenceMismatch);
        }

        var hasManualProjectUnit = !string.IsNullOrWhiteSpace(request.ManualProjectName)
            || !string.IsNullOrWhiteSpace(request.ManualUnitNumber);
        if (hasCrmBuyerMatch && hasManualProjectUnit)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerAndManualProjectUnitBothSupplied);
        }

        // External-lookup verification (PACT/Tasleeh): the source name and
        // its own customer/unit identifiers travel together — identifiers
        // without a source are meaningless for audit/reconciliation and are
        // rejected rather than stored orphaned. Mutually exclusive with a
        // CRM Buyer match (a ticket records one verified identity), but the
        // manual Project/Unit snapshot deliberately accompanies it — that
        // pair is the human-readable snapshot for a unit with no local
        // reference.
        var hasExternalVerification = !string.IsNullOrWhiteSpace(request.CustomerVerificationSource);
        if (!hasExternalVerification
            && (!string.IsNullOrWhiteSpace(request.ExternalCustomerId) || !string.IsNullOrWhiteSpace(request.ExternalUnitId)))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.ExternalVerificationSourceMissing);
        }

        if (hasCrmBuyerMatch && hasExternalVerification)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerAndExternalVerificationBothSupplied);
        }

        // The New Ticket wizard's own "Project and Unit Number are required
        // when no CRM Buyer match was selected" business rule is enforced in
        // NewTicketModel.OnPostCreateAsync (TigerCS.Web), not here: Ticket
        // Category remains the only universal requirement POST /api/tickets
        // itself enforces for every caller (business-rule change predating
        // CRM Buyer Lookup — see this method's own remarks). ManualProjectName/
        // ManualUnitNumber are accepted and stored as optional pass-through
        // fields whenever a caller does supply them.

        var routing = await ResolveRoutingAsync(request.CategoryId, request.PriorityId, intakeRecord.DepartmentId, cancellationToken);
        if (routing.Failure is { } routingFailure)
        {
            return TicketCreationResult.Failure(routingFailure);
        }

        var category = routing.Category!;
        var priority = routing.Priority!;
        var department = routing.Department!;

        // Workflow/Automation phase 2 — optional request-type classification.
        // When supplied it must be an active request type of the department
        // the ticket routes to (request types are never offered across
        // departments); when absent, everything below behaves exactly as
        // before this phase.
        RequestType? requestType = null;
        if (request.RequestTypeId is { } requestTypeId)
        {
            requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            if (requestType is null || !requestType.IsActive)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.RequestTypeNotFound);
            }

            if (requestType.DepartmentId != category.DepartmentId)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.RequestTypeDepartmentMismatch);
            }
        }

        if (request.GenesysContext is { } genesysContext && string.IsNullOrWhiteSpace(genesysContext.ConversationId))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.GenesysConversationIdRequired);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        Ticket ticket;
        try
        {
            var ticketNumber = await GenerateTicketNumberAsync(department, now, cancellationToken);
            ticket = (unitReference, contactReference, hasCrmBuyerMatch, hasExternalVerification) switch
            {
                (not null, not null, _, _) => Ticket.CreateVerified(
                    ticketNumber, category.DepartmentId, unitReference!.UnitReferenceId, contactReference!.ContactReferenceId,
                    category.CategoryId, priority.PriorityId, request.RequestSummary, now),
                (_, _, true, _) => Ticket.CreateVerifiedFromCrmBuyer(
                    ticketNumber, category.DepartmentId,
                    request.CrmBuyerCustomerId!.Value, request.CrmBuyerLeadId!.Value, request.CrmBuyerUnitId!.Value, request.CrmBuyerProjectId!.Value,
                    request.CrmBuyerCustomerName, request.CrmBuyerProjectName, request.CrmBuyerUnitNumber,
                    category.CategoryId, priority.PriorityId, request.RequestSummary, now),
                (_, _, _, true) => Ticket.CreateFromExternalLookup(
                    ticketNumber, category.DepartmentId,
                    request.CustomerVerificationSource!, request.ExternalCustomerId, request.ExternalUnitId,
                    request.ManualProjectName, request.ManualUnitNumber,
                    category.CategoryId, priority.PriorityId, request.RequestSummary, now),
                _ => Ticket.CreateUnverified(
                    ticketNumber, category.DepartmentId, category.CategoryId, priority.PriorityId, request.RequestSummary, now,
                    request.ManualProjectName, request.ManualUnitNumber)
            };

            if (requestType is not null)
            {
                ticket.ClassifyRequestType(requestType.RequestTypeId);
            }

            await ticketRepository.AddAsync(ticket, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // Nothing else has been touched yet — the IntakeRecord is still
            // unlinked. Safe to retry the whole request unchanged.
            return TicketCreationResult.Failure(TicketCreationOutcome.TicketNumberCollision);
        }

        // hasSelectedUnit is the real, source-agnostic rule for
        // IntakeRecord.IsUnitRelated: a resolved local Unit/Contact
        // reference — or, business-rule change, a real CRM Buyer Lookup
        // match — was linked; not ticket.VerificationStatus, which is a
        // separate, CRM-named concept (see IntakeRecord.LinkToTicket's
        // remarks).
        intakeRecord.LinkToTicket(ticket.TicketId, ticket.VerificationStatus, unitReference is not null || hasCrmBuyerMatch);

        if (unitReference is not null && contactReference is not null)
        {
            await snapshotRepository.AddAsync(
                new TicketRequesterSnapshot(
                    ticket.TicketId,
                    unitReference.UnitNumber,
                    unitReference.PropertyName,
                    unitReference.TowerName,
                    unitReference.UnitType,
                    contactReference.DisplayName,
                    contactReference.ContactChannel,
                    now),
                cancellationToken);
        }

        await SeedStatusHistoryAsync(ticket, callerEmployeeId, now, cancellationToken);

        var correlationId = Guid.NewGuid();

        // Workflow/Automation phase 2 — record the ORIGINATING interaction
        // the ticket was created from (a ticket accumulates further
        // interaction rows over its lifetime; this first one carries
        // IsOriginatingInteraction). Channel and customer phone come from
        // the intake record (the phone stays the verification identity
        // input); a supplied Genesys context marks the row Genesys-sourced
        // and is stored verbatim for traceability — Ticketing never
        // re-derives routing from it. Face-to-Face and every other
        // locally-created interaction records a Ticketing-sourced row with
        // all Genesys fields null.
        var originatingInteraction = request.GenesysContext is { } genesys
            ? TicketInteraction.CreateFromGenesys(
                ticket.TicketId, intakeRecord.ChannelId, intakeRecord.PhoneNumber,
                genesys.ConversationId, genesys.CalledNumber, genesys.QueueId, genesys.QueueName,
                genesys.AgentId, genesys.AgentName, genesys.InteractionStartedAtUtc, genesys.Direction, now,
                isOriginatingInteraction: true)
            : TicketInteraction.CreateLocal(
                ticket.TicketId, intakeRecord.ChannelId, intakeRecord.PhoneNumber, now, isOriginatingInteraction: true);
        await interactionRepository.AddAsync(originatingInteraction, cancellationToken);

        // Workflow/Automation phase 2 — Department + Request Type resolve
        // the configured assignment rule; no rule (or any invalid target)
        // leaves the ticket in the department queue, audited as a system
        // action. Runs in this same transaction, before the SLA period and
        // creation audit commit with it.
        await autoAssignmentService.ApplyAsync(ticket, now, correlationId, cancellationToken);

        // Backlog S-08's corrected acceptance criterion: ticket creation
        // opens the ticket's initial TicketSlaInstances row with computed due
        // dates — always, immediately. Business-rule change: nothing about
        // customer lookup ever pauses this clock any more (see
        // Ticket.CreateUnverified's remarks); `now` is both the ticket's
        // CreatedAtUtc and the SLA clock-start event (ISSUE-001 Option C,
        // SLA-Architecture.md §1/§2).
        await slaDueDateService.OpenInitialPeriodAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Create", "Ticket", ticket.TicketId.ToString(),
            beforeValue: null,
            afterValue: $"TicketNumber={ticket.TicketNumber};VerificationStatus={ticket.VerificationStatus}",
            correlationId, cancellationToken);

        await EnqueueTicketCreatedAsync(ticket, callerEmployeeId, correlationId, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TicketCreationResult.Success(ToDto(ticket));
    }

    /// <summary>
    /// Writes MVP-API-Contracts.md §3.1's <c>TicketCreated</c> Outbox event —
    /// the one that "drives the automated acknowledgement notification"
    /// (FR-NOT-01) — into the same transaction as the ticket itself.
    ///
    /// <para>
    /// <b>Nothing is sent from here.</b> NFR-REL-01 is explicit that nothing
    /// is dispatched from application code inside a request handler without
    /// first being durably recorded in the same transaction as the state
    /// change. This method records; ADR-0015's recurring dispatcher delivers,
    /// after the commit. A caller that rolls back — a ticket-number
    /// collision — takes this row down with it, so a ticket that does not
    /// exist can never acknowledge anything.
    /// </para>
    ///
    /// <para>
    /// <b>Written for an unverified ticket too, deliberately.</b> FR-NOT-01's
    /// acceptance criterion is "email attempted for every ticket", and an
    /// unverified ticket has no requester snapshot to resolve a recipient
    /// from. Skipping the event would make that ticket invisible —
    /// indistinguishable from one that was acknowledged — whereas enqueuing
    /// it produces a dead-lettered, audited, countable record that no
    /// acknowledgement could be sent and why.
    /// </para>
    ///
    /// <para>
    /// A <c>null</c> return means the key was already reserved — the same
    /// logical event is already enqueued, so no second message is written.
    /// That is the normal outcome of a retried request, not an error, and no
    /// audit entry is written for it because nothing was queued.
    /// </para>
    /// </summary>
    private async Task EnqueueTicketCreatedAsync(
        Ticket ticket, Guid callerEmployeeId, Guid correlationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var payload = new TicketCreatedEventPayload(ticket.TicketId, OutboxEventTypes.TicketCreatedVersion);

        var message = await outboxWriter.WriteAsync(
            OutboxEventTypes.TicketCreated,
            payload.ToJson(),
            correlationId,
            OutboxEventTypes.IdempotencyKeyFor(
                OutboxEventTypes.TicketCreated, ticket.TicketId, OutboxEventTypes.TicketCreatedVersion),
            nowUtc,
            cancellationToken);

        if (message is null)
        {
            return;
        }

        // "Notification queued" (ADR-0018) — the one notification audit event
        // written synchronously, in the business transaction, so the audit
        // trail records the intent at the moment it became durable rather
        // than whenever a background job next runs.
        await auditWriter.WriteAsync(
            callerEmployeeId,
            NotificationAuditActions.NotificationQueued,
            NotificationAuditActions.OutboxMessageEntityType,
            message.OutboxMessageId.ToString(),
            beforeValue: null,
            afterValue: $"EventType={OutboxEventTypes.TicketCreated};TicketId={ticket.TicketId};Status=Pending",
            correlationId,
            cancellationToken);
    }

    /// <summary>
    /// Resolves and validates Category → Department routing (FR-CLS-01/
    /// FR-RTE-01) and Priority together, since ticket creation needs both.
    /// Rejects a Category that is missing/inactive, a Priority that is
    /// missing, a Category whose routed Department is itself missing/
    /// inactive (item 9 of the senior review, so a ticket can never be
    /// silently routed to a department nobody is staffing), and — the
    /// Category-directory follow-up — a Category that routes to a different
    /// Department than the one the IntakeRecord itself named. The UI's
    /// Category dropdown is always scoped to the IntakeRecord's Department
    /// (or offers every Department's Categories when none was given), so
    /// this last case only fires against a request built outside that UI.
    /// </summary>
    private async Task<RoutingResolution> ResolveRoutingAsync(
        int categoryId, byte priorityId, int? intakeDepartmentId, CancellationToken cancellationToken)
    {
        var category = await categoryRepository.GetByIdAsync(categoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.CategoryNotFound);
        }

        if (intakeDepartmentId is { } departmentIdOnIntake && category.DepartmentId != departmentIdOnIntake)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.CategoryDepartmentMismatch);
        }

        var priority = await priorityRepository.GetByIdAsync(priorityId, cancellationToken);
        if (priority is null)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.PriorityNotFound);
        }

        var department = await departmentRepository.GetByIdAsync(category.DepartmentId, cancellationToken);
        if (department is null || !department.IsActive)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.DepartmentInactive);
        }

        return RoutingResolution.Succeeded(category, priority, department);
    }

    private sealed record RoutingResolution(
        Domain.Modules.ClassificationAndRouting.Category? Category,
        Priority? Priority,
        Domain.Modules.IdentityAndAccess.Department? Department,
        TicketCreationOutcome? Failure)
    {
        public static RoutingResolution Succeeded(
            Domain.Modules.ClassificationAndRouting.Category category, Priority priority, Domain.Modules.IdentityAndAccess.Department department) =>
            new(category, priority, department, null);

        public static RoutingResolution Failed(TicketCreationOutcome outcome) => new(null, null, null, outcome);
    }

    /// <summary>
    /// Seeds one TicketStatusHistory row per dimension that actually has an
    /// initial value at creation (MVP-API-Contracts.md §3.1's audit note).
    /// ResolutionOutcome is deliberately excluded — it is null until the
    /// ticket is later resolved, and TicketStatusHistory.NewValue is NOT NULL
    /// (MVP-Data-Dictionary.md §2.13), so there is no value to seed a row
    /// with yet.
    /// </summary>
    private async Task SeedStatusHistoryAsync(
        Ticket ticket, Guid actorEmployeeId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        (TicketStatusDimension Dimension, byte NewValue)[] seeds =
        [
            (TicketStatusDimension.TicketStatus, (byte)ticket.TicketStatus),
            (TicketStatusDimension.VerificationStatus, (byte)ticket.VerificationStatus),
            (TicketStatusDimension.EscalationLevel, (byte)ticket.EscalationLevel),
            (TicketStatusDimension.SlaState, (byte)ticket.SlaState)
        ];

        foreach (var (dimension, newValue) in seeds)
        {
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, dimension, oldValue: null, newValue,
                    actorEmployeeId, actorIsSystem: false, note: null, correlationId, nowUtc),
                cancellationToken);
        }
    }

    private async Task<string> GenerateTicketNumberAsync(
        Domain.Modules.IdentityAndAccess.Department department, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var prefix = $"TG-{department.Code}-{nowUtc:yyyyMMdd}-";
        var existingCount = await ticketRepository.CountByTicketNumberPrefixAsync(prefix, cancellationToken);
        return $"{prefix}{existingCount + 1:D4}";
    }

    private static TicketResponseDto ToDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.OriginatingDepartmentId,
        ticket.CurrentDepartmentId,
        ticket.UnitReferenceId,
        ticket.ContactReferenceId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.EscalationLevel.ToString(),
        ticket.SlaState.ToString(),
        ticket.RequestSummary,
        ticket.CreatedAtUtc,
        Convert.ToBase64String(ticket.RowVersion),
        ticket.CrmBuyerCustomerId,
        ticket.CrmBuyerLeadId,
        ticket.CrmBuyerUnitId,
        ticket.CrmBuyerProjectId,
        ticket.CrmBuyerCustomerName,
        ticket.CrmBuyerProjectName,
        ticket.CrmBuyerUnitNumber,
        ticket.ManualProjectName,
        ticket.ManualUnitNumber,
        ticket.CustomerVerificationSource,
        ticket.ExternalCustomerId,
        ticket.ExternalUnitId);
}
