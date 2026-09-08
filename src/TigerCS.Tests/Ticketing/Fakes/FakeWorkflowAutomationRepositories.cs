using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>In-memory structured-pending store, mirroring the real repository's "at most one open record per ticket" read.</summary>
public sealed class FakeTicketPendingRecordRepository : ITicketPendingRecordRepository
{
    private readonly List<TicketPendingRecord> _records = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every record added so far, in insertion order.</summary>
    public IReadOnlyList<TicketPendingRecord> All => _records;

    public Task<TicketPendingRecord?> GetOpenAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.FirstOrDefault(r => r.TicketId == ticketId && r.ResumedAtUtc is null));

    public Task<IReadOnlyList<TicketPendingRecord>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketPendingRecord>>(
            _records.Where(r => r.TicketId == ticketId).OrderBy(r => r.StartedAtUtc).ToList());

    public Task AddAsync(TicketPendingRecord record, CancellationToken cancellationToken = default)
    {
        typeof(TicketPendingRecord).GetProperty(nameof(TicketPendingRecord.TicketPendingRecordId))!.SetValue(record, _nextId++);
        _records.Add(record);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory interaction store — many per ticket, mirroring the real table's at-most-one-originating rule.</summary>
public sealed class FakeTicketInteractionRepository : ITicketInteractionRepository
{
    private readonly List<TicketInteraction> _interactions = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every interaction added so far, in insertion order.</summary>
    public IReadOnlyList<TicketInteraction> All => _interactions;

    public Task<TicketInteraction?> GetOriginatingAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_interactions.FirstOrDefault(i => i.TicketId == ticketId && i.IsOriginatingInteraction));

    public Task<IReadOnlyList<TicketInteraction>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketInteraction>>(
            _interactions.Where(i => i.TicketId == ticketId).OrderBy(i => i.CreatedAtUtc).ToList());

    public Task AddAsync(TicketInteraction interaction, CancellationToken cancellationToken = default)
    {
        // Mirror the database's filtered unique index so a test can never
        // pass while violating the one-originating-per-ticket invariant.
        if (interaction.IsOriginatingInteraction
            && _interactions.Any(i => i.TicketId == interaction.TicketId && i.IsOriginatingInteraction))
        {
            throw new InvalidOperationException(
                $"Ticket {interaction.TicketId} already has an originating interaction.");
        }

        typeof(TicketInteraction).GetProperty(nameof(TicketInteraction.TicketInteractionId))!.SetValue(interaction, _nextId++);
        _interactions.Add(interaction);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory request-type store for service tests; ids are assigned on add, mirroring the identity column.</summary>
public sealed class FakeRequestTypeRepository : IRequestTypeRepository
{
    private readonly Dictionary<int, RequestType> _requestTypes = [];
    private int _nextId = 1;

    public RequestType Add(RequestType requestType)
    {
        typeof(RequestType).GetProperty(nameof(RequestType.RequestTypeId))!.SetValue(requestType, _nextId++);
        _requestTypes[requestType.RequestTypeId] = requestType;
        return requestType;
    }

    public Task<RequestType?> GetByIdAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requestTypes.GetValueOrDefault(requestTypeId));

    public Task<IReadOnlyList<RequestType>> ListActiveByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestType>>(
            _requestTypes.Values.Where(r => r.DepartmentId == departmentId && r.IsActive).OrderBy(r => r.Name).ToList());

    public Task<IReadOnlyList<RequestType>> ListAsync(int? departmentId, bool includeInactive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestType>>(_requestTypes.Values
            .Where(r => departmentId is null || r.DepartmentId == departmentId)
            .Where(r => includeInactive || r.IsActive)
            .OrderBy(r => r.DepartmentId).ThenBy(r => r.Name)
            .ToList());

    public Task<IReadOnlyList<RequestType>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestType>>(_requestTypes.Values.Where(r => r.WorkflowId == workflowId).OrderBy(r => r.Name).ToList());

    public Task<bool> NameExistsAsync(int departmentId, string name, int? excludeRequestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requestTypes.Values.Any(r =>
            r.DepartmentId == departmentId && r.Name == name && (excludeRequestTypeId is null || r.RequestTypeId != excludeRequestTypeId)));

    public Task AddAsync(RequestType requestType, CancellationToken cancellationToken = default)
    {
        Add(requestType);
        return Task.CompletedTask;
    }

    public Dictionary<int, int> TicketCounts { get; } = [];

    public Task<int> CountTicketsAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TicketCounts.GetValueOrDefault(requestTypeId));
}

/// <summary>In-memory workflow-template store for service tests.</summary>
public sealed class FakeWorkflowTemplateRepository : IWorkflowTemplateRepository
{
    private readonly Dictionary<int, WorkflowTemplate> _templates = [];
    private int _nextId = 1;

    private int _nextStepId = 1;

    public WorkflowTemplate Add(WorkflowTemplate template)
    {
        typeof(WorkflowTemplate).GetProperty(nameof(WorkflowTemplate.WorkflowTemplateId))!.SetValue(template, _nextId++);
        _templates[template.WorkflowTemplateId] = template;
        AssignStepIds(template);
        return template;
    }

    /// <summary>Mirrors what a database save does: every step appended since the last read gets a real id, so id-based editing works exactly as it does over EF.</summary>
    private void AssignStepIds(WorkflowTemplate template)
    {
        foreach (var step in template.Steps.Where(s => s.WorkflowTemplateStepId == 0))
        {
            typeof(WorkflowTemplateStep).GetProperty(nameof(WorkflowTemplateStep.WorkflowTemplateStepId))!.SetValue(step, _nextStepId++);
        }
    }

    public Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default)
    {
        var template = _templates.GetValueOrDefault(workflowTemplateId);
        if (template is not null)
        {
            AssignStepIds(template);
        }

        return Task.FromResult(template);
    }

    public Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Values.FirstOrDefault(t => t.Code == code));

    public Task<IReadOnlyList<WorkflowTemplate>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowTemplate>>(_templates.Values.Where(t => t.WorkflowId == workflowId).OrderBy(t => t.VersionNumber).ToList());

    public Task<WorkflowTemplate?> GetPublishedAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Values.FirstOrDefault(t => t.WorkflowId == workflowId && t.IsPublished));

    public Task<WorkflowTemplate?> GetDraftAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Values.FirstOrDefault(t => t.WorkflowId == workflowId && t.IsDraft));

    public Task AddAsync(WorkflowTemplate version, CancellationToken cancellationToken = default)
    {
        Add(version);
        return Task.CompletedTask;
    }

    public void Remove(WorkflowTemplate version) => _templates.Remove(version.WorkflowTemplateId);

    public Dictionary<int, int> PinnedTicketCounts { get; } = [];

    public Task<int> CountPinnedTicketsAsync(int workflowTemplateId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PinnedTicketCounts.GetValueOrDefault(workflowTemplateId));

    public Task<IReadOnlyDictionary<int, int>> CountPinnedTicketsByVersionAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<int, int>>(_templates.Values
            .Where(t => t.WorkflowId == workflowId && PinnedTicketCounts.ContainsKey(t.WorkflowTemplateId))
            .ToDictionary(t => t.WorkflowTemplateId, t => PinnedTicketCounts[t.WorkflowTemplateId]));
}

public sealed class FakeWorkflowRepository : IWorkflowRepository
{
    private readonly Dictionary<int, Workflow> _workflows = [];
    private int _nextId = 1;

    public Workflow Add(Workflow workflow)
    {
        typeof(Workflow).GetProperty(nameof(Workflow.WorkflowId))!.SetValue(workflow, _nextId++);
        _workflows[workflow.WorkflowId] = workflow;
        return workflow;
    }

    public Task<Workflow?> GetByIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_workflows.GetValueOrDefault(workflowId));

    public Task<Workflow?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_workflows.Values.FirstOrDefault(w => w.Code == code));

    public Task<IReadOnlyList<Workflow>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Workflow>>(_workflows.Values.Where(w => includeInactive || w.IsActive).OrderBy(w => w.Name).ToList());

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_workflows.Values.Any(w => w.Code == code));

    public Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        Add(workflow);
        return Task.CompletedTask;
    }
}

public sealed class FakeWorkflowConfigurationUnitOfWork : IWorkflowConfigurationUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory assignment-rule store — one rule per request type, like the real unique index.</summary>
public sealed class FakeRequestTypeAssignmentRuleRepository : IRequestTypeAssignmentRuleRepository
{
    private readonly Dictionary<int, RequestTypeAssignmentRule> _rulesByRequestTypeId = [];
    private int _nextId = 1;

    public RequestTypeAssignmentRule Add(RequestTypeAssignmentRule rule)
    {
        typeof(RequestTypeAssignmentRule).GetProperty(nameof(RequestTypeAssignmentRule.RequestTypeAssignmentRuleId))!
            .SetValue(rule, _nextId++);
        _rulesByRequestTypeId[rule.RequestTypeId] = rule;
        return rule;
    }

    public Task<RequestTypeAssignmentRule?> GetByRequestTypeIdAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rulesByRequestTypeId.GetValueOrDefault(requestTypeId));

    public Task AddAsync(RequestTypeAssignmentRule rule, CancellationToken cancellationToken = default)
    {
        Add(rule);
        return Task.CompletedTask;
    }

    public void Remove(RequestTypeAssignmentRule rule) => _rulesByRequestTypeId.Remove(rule.RequestTypeId);
}

/// <summary>In-memory department workflow settings — absent rows behave exactly like a department that predates the configuration.</summary>
public sealed class FakeDepartmentWorkflowSettingsRepository : IDepartmentWorkflowSettingsRepository
{
    private readonly Dictionary<int, DepartmentWorkflowSettings> _settings = [];

    public void Add(DepartmentWorkflowSettings settings) => _settings[settings.DepartmentId] = settings;

    public Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings.GetValueOrDefault(departmentId));
}

/// <summary>In-memory approval store — append-plus-supersede, mirroring the real table's one-pending-per-type filtered index.</summary>
public sealed class FakeTicketApprovalRepository : ITicketApprovalRepository
{
    private readonly List<TicketApproval> _approvals = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketApproval> All => _approvals;

    public Task<TicketApproval?> GetByIdAsync(long ticketApprovalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_approvals.FirstOrDefault(a => a.TicketApprovalId == ticketApprovalId));

    public Task<TicketApproval?> GetPendingAsync(long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        Task.FromResult(_approvals.FirstOrDefault(
            a => a.TicketId == ticketId && a.ApprovalType == approvalType && a.Status == ApprovalStatus.Pending));

    public Task<TicketApproval?> GetCurrentAsync(long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        Task.FromResult(_approvals.FirstOrDefault(
            a => a.TicketId == ticketId && a.ApprovalType == approvalType && a.IsCurrent));

    public Task<IReadOnlyList<TicketApproval>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketApproval>>(
            _approvals.Where(a => a.TicketId == ticketId).OrderBy(a => a.RequestedAtUtc).ThenBy(a => a.TicketApprovalId).ToList());

    public Task AddAsync(TicketApproval approval, CancellationToken cancellationToken = default)
    {
        // Mirror the database's filtered unique index so no test can pass
        // while two Pending cycles of one type coexist.
        if (approval.Status == ApprovalStatus.Pending
            && _approvals.Any(a => a.TicketId == approval.TicketId && a.ApprovalType == approval.ApprovalType && a.Status == ApprovalStatus.Pending))
        {
            throw new InvalidOperationException(
                $"Ticket {approval.TicketId} already has a pending {approval.ApprovalType} cycle.");
        }

        typeof(TicketApproval).GetProperty(nameof(TicketApproval.TicketApprovalId))!.SetValue(approval, _nextId++);
        _approvals.Add(approval);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory typed workflow event store — append-only.</summary>
public sealed class FakeTicketWorkflowEventRepository : ITicketWorkflowEventRepository
{
    private readonly List<TicketWorkflowEvent> _events = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketWorkflowEvent> All => _events;

    public Task<TicketWorkflowEvent?> GetFirstAsync(long ticketId, WorkflowEventType eventType, CancellationToken cancellationToken = default) =>
        Task.FromResult(_events
            .Where(e => e.TicketId == ticketId && e.EventType == eventType)
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.TicketWorkflowEventId)
            .FirstOrDefault());

    public Task<TicketWorkflowEvent?> GetLatestAsync(
        long ticketId, IReadOnlyCollection<WorkflowEventType> eventTypes, CancellationToken cancellationToken = default) =>
        Task.FromResult(_events
            .Where(e => e.TicketId == ticketId && eventTypes.Contains(e.EventType))
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.TicketWorkflowEventId)
            .FirstOrDefault());

    public Task<IReadOnlyList<TicketWorkflowEvent>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketWorkflowEvent>>(
            _events.Where(e => e.TicketId == ticketId).OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.TicketWorkflowEventId).ToList());

    public Task AddAsync(TicketWorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
    {
        typeof(TicketWorkflowEvent).GetProperty(nameof(TicketWorkflowEvent.TicketWorkflowEventId))!.SetValue(workflowEvent, _nextId++);
        _events.Add(workflowEvent);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory approval requirement configuration — one per (request type, approval type).</summary>
public sealed class FakeRequestTypeApprovalRequirementRepository : IRequestTypeApprovalRequirementRepository
{
    private readonly List<RequestTypeApprovalRequirement> _requirements = [];
    private int _nextId = 1;

    public RequestTypeApprovalRequirement Add(RequestTypeApprovalRequirement requirement)
    {
        typeof(RequestTypeApprovalRequirement).GetProperty(nameof(RequestTypeApprovalRequirement.RequestTypeApprovalRequirementId))!
            .SetValue(requirement, _nextId++);
        _requirements.Add(requirement);
        return requirement;
    }

    public Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListActiveByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestTypeApprovalRequirement>>(
            _requirements.Where(r => r.RequestTypeId == requestTypeId && r.IsActive).OrderBy(r => r.ApprovalType).ToList());

    public Task<RequestTypeApprovalRequirement?> GetActiveAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requirements.FirstOrDefault(
            r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType && r.IsActive));

    public Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestTypeApprovalRequirement>>(
            _requirements.Where(r => r.RequestTypeId == requestTypeId).OrderBy(r => r.ApprovalType).ToList());

    public Task<RequestTypeApprovalRequirement?> GetAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requirements.FirstOrDefault(r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType));

    public Task AddAsync(RequestTypeApprovalRequirement requirement, CancellationToken cancellationToken = default)
    {
        Add(requirement);
        return Task.CompletedTask;
    }
}

public sealed class FakeRequestTypeSlaPolicyRepository : IRequestTypeSlaPolicyRepository
{
    private readonly List<RequestTypeSlaPolicy> _policies = [];
    private int _nextId = 1;

    public Task<RequestTypeSlaPolicy?> GetActiveAsync(int requestTypeId, byte priorityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_policies.FirstOrDefault(p => p.RequestTypeId == requestTypeId && p.PriorityId == priorityId && p.IsActive));

    public Task<RequestTypeSlaPolicy?> GetAsync(int requestTypeId, byte priorityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_policies.FirstOrDefault(p => p.RequestTypeId == requestTypeId && p.PriorityId == priorityId));

    public Task<IReadOnlyList<RequestTypeSlaPolicy>> ListByRequestTypeAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RequestTypeSlaPolicy>>(_policies.Where(p => p.RequestTypeId == requestTypeId).OrderBy(p => p.PriorityId).ToList());

    public Task AddAsync(RequestTypeSlaPolicy policy, CancellationToken cancellationToken = default)
    {
        typeof(RequestTypeSlaPolicy).GetProperty(nameof(RequestTypeSlaPolicy.RequestTypeSlaPolicyId))!.SetValue(policy, _nextId++);
        _policies.Add(policy);
        return Task.CompletedTask;
    }
}
