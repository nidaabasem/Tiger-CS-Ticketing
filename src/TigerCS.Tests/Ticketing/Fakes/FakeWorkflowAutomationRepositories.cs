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
}

/// <summary>In-memory workflow-template store for service tests.</summary>
public sealed class FakeWorkflowTemplateRepository : IWorkflowTemplateRepository
{
    private readonly Dictionary<int, WorkflowTemplate> _templates = [];
    private int _nextId = 1;

    public WorkflowTemplate Add(WorkflowTemplate template)
    {
        typeof(WorkflowTemplate).GetProperty(nameof(WorkflowTemplate.WorkflowTemplateId))!.SetValue(template, _nextId++);
        _templates[template.WorkflowTemplateId] = template;
        return template;
    }

    public Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.GetValueOrDefault(workflowTemplateId));

    public Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Values.FirstOrDefault(t => t.Code == code));
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
}

/// <summary>In-memory department workflow settings — absent rows behave exactly like a department that predates the configuration.</summary>
public sealed class FakeDepartmentWorkflowSettingsRepository : IDepartmentWorkflowSettingsRepository
{
    private readonly Dictionary<int, DepartmentWorkflowSettings> _settings = [];

    public void Add(DepartmentWorkflowSettings settings) => _settings[settings.DepartmentId] = settings;

    public Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings.GetValueOrDefault(departmentId));
}
