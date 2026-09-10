using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The Unclassified → Classified transition: an agent has read the inquiry
/// and records what the customer actually wants.
///
/// <para>
/// <b>The same ticket is classified in place.</b> No second ticket is
/// created, nothing is copied, and the ticket keeps its number, its
/// department, its interactions and its whole history — a customer who
/// started one conversation has one ticket, before and after.
/// </para>
///
/// <para>
/// <b>This is where the business/resolution SLA starts — at the moment of
/// classification, not retroactively.</b> An SLA policy is a commitment to
/// resolve a <i>known</i> request within a target; while the ticket was
/// unclassified nobody knew the request, the priority or therefore the
/// policy, so there was no commitment to measure and no period was opened.
/// The clock runs from <c>now</c>, the classification timestamp. Backdating
/// it to the ticket's creation would charge the department for time during
/// which no target existed, against a policy chosen after the fact.
/// </para>
///
/// <para>
/// <b>First Response is measured separately and is unaffected.</b>
/// <c>Ticket.FirstHumanResponseAtUtc</c> lives on the ticket, not on the SLA
/// period, so an agent's first reply to an unclassified Genesys inquiry is
/// recorded with its real timestamp exactly as on any other ticket — how
/// quickly the customer reached a human stays measurable from the moment the
/// inquiry arrived, whether or not anyone has classified it yet.
/// </para>
///
/// <para>
/// A ticket that was created classified is untouched by all of this: it
/// already has its category, its priority and its SLA period, and this
/// service performs <i>initial</i> classification only
/// (<see cref="TicketMutationOutcome.AlreadyClassified"/>). That refusal is a
/// Phase 1 safeguard while reclassification semantics are undefined — see
/// <see cref="TicketAlreadyClassifiedException"/> — not a rule that a
/// classification may never change.
/// </para>
/// </summary>
public sealed class TicketClassificationAppService(
    ITicketRepository ticketRepository,
    ICategoryRepository categoryRepository,
    IPriorityRepository priorityRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    SlaDueDateService slaDueDateService,
    TimeProvider timeProvider)
{
    public async Task<TicketMutationResult> ClassifyAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ClassifyTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Classifying is ordinary work on a ticket you can already see and
        // work: the same department-scoped authority that governs status
        // changes, never a new privilege tier.
        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains)
            || callerRoles.Contains(Domain.Modules.IdentityAndAccess.Roles.CsAgent)
            || await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken));

        if (!authorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        if (ticket.IsClassified)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyClassified);
        }

        var category = await categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.CategoryNotFound);
        }

        // The department is already settled — the inquiry resolved it before
        // the ticket existed. Classification names the request, it does not
        // re-route the ticket; a category from another department would do
        // exactly that silently.
        if (category.DepartmentId != ticket.CurrentDepartmentId)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.CategoryDepartmentMismatch);
        }

        if (await priorityRepository.GetByIdAsync(request.PriorityId, cancellationToken) is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.PriorityNotFound);
        }

        // Optional request type — same rules as ticket creation applies them,
        // so a ticket classified late is pinned exactly as one classified at
        // creation would have been.
        Domain.Modules.WorkflowConfiguration.RequestType? requestType = null;
        Domain.Modules.WorkflowConfiguration.WorkflowTemplate? workflowVersion = null;
        if (request.RequestTypeId is { } requestTypeId)
        {
            requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            if (requestType is null || !requestType.IsActive)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }

            if (requestType.DepartmentId != ticket.CurrentDepartmentId)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }

            workflowVersion = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
            if (workflowVersion is null)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        try
        {
            ticket.Classify(category.CategoryId, request.PriorityId);

            if (requestType is not null && workflowVersion is not null)
            {
                ticket.ClassifyRequestType(requestType.RequestTypeId);
                ticket.PinWorkflowVersion(workflowVersion.WorkflowTemplateId);
            }

            // The SLA clock the ticket never had, started from this moment —
            // the first instant at which a target exists to measure against.
            // See this type's remarks for why it is not backdated.
            await slaDueDateService.OpenInitialPeriodAsync(
                ticket, now, callerEmployeeId, correlationId, cancellationToken);

            // The ticket's SlaState was NotApplicable while unclassified; a
            // real period now exists, so the dimension reports Running like
            // any other live ticket.
            ticket.StartSlaClock();

            // The SlaState dimension genuinely moved, so it is recorded on
            // the ticket's status history like every other dimension change.
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, TicketStatusDimension.SlaState, oldValue: (byte)SlaState.NotApplicable,
                    newValue: (byte)SlaState.Running, callerEmployeeId, actorIsSystem: false,
                    note: $"SLA clock started at classification ({now:O}).", correlationId, now),
                cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId, "ClassifyTicket", "Ticket", ticket.TicketId.ToString(),
                beforeValue: "CategoryId=(none);PriorityId=(none);RequestTypeId=(none);SlaState=NotApplicable",
                afterValue:
                    $"CategoryId={ticket.CategoryId};PriorityId={ticket.PriorityId};"
                    + $"RequestTypeId={ticket.RequestTypeId?.ToString() ?? "(none)"};SlaState={ticket.SlaState}",
                correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketAlreadyClassifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyClassified);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }
}
