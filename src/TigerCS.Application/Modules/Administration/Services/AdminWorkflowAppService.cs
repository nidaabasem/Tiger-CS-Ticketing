using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// The Workflow Designer's application service: logical workflows, their
/// numbered versions, the Draft → Validate → Publish lifecycle, and the
/// step/branch editing that is only ever allowed on a Draft. Every rule
/// about immutability, deletion and "the active version" is enforced here
/// or in the aggregate — never only in the UI.
/// </summary>
public sealed class AdminWorkflowAppService(
    IWorkflowRepository workflowRepository,
    IWorkflowTemplateRepository versionRepository,
    IRequestTypeRepository requestTypeRepository,
    IDepartmentRepository departmentRepository,
    IEmployeeRepository employeeRepository,
    IWorkflowConfigurationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public static WorkflowDesignerCatalogDto Catalog() => new(
        WorkflowStepKinds.All
            .Select(k => new WorkflowStepKindDto(k.Kind, k.Label, k.Description, k.RequiresApprovalType, k.SupportsOutcomeBranches, k.IsStart, k.IsTerminal))
            .ToList(),
        Enum.GetValues<ApprovalType>().Select(t => new ApprovalTypeOptionDto(t, ApprovalTypeLabels.Label(t))).ToList());

    public async Task<IReadOnlyList<AdminWorkflowSummaryDto>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var workflows = await workflowRepository.ListAsync(includeInactive, cancellationToken);
        var result = new List<AdminWorkflowSummaryDto>(workflows.Count);
        foreach (var workflow in workflows)
        {
            var versions = await versionRepository.ListByWorkflowIdAsync(workflow.WorkflowId, cancellationToken);
            var published = versions.FirstOrDefault(v => v.IsPublished);
            var draft = versions.FirstOrDefault(v => v.IsDraft);
            var counts = await versionRepository.CountPinnedTicketsByVersionAsync(workflow.WorkflowId, cancellationToken);
            var requestTypes = await requestTypeRepository.ListByWorkflowIdAsync(workflow.WorkflowId, cancellationToken);
            result.Add(new AdminWorkflowSummaryDto(
                workflow.WorkflowId, workflow.Name, workflow.Description, workflow.IsActive,
                published?.WorkflowTemplateId, published?.VersionNumber,
                draft?.WorkflowTemplateId, draft?.VersionNumber,
                versions.Count, requestTypes.Count, counts.Values.Sum()));
        }

        return result;
    }

    public async Task<AdminWorkflowDetailDto?> GetAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return null;
        }

        var versions = await versionRepository.ListByWorkflowIdAsync(workflowId, cancellationToken);
        var counts = await versionRepository.CountPinnedTicketsByVersionAsync(workflowId, cancellationToken);

        var versionDtos = new List<WorkflowVersionSummaryDto>(versions.Count);
        foreach (var version in versions.OrderByDescending(v => v.VersionNumber))
        {
            var ticketCount = counts.GetValueOrDefault(version.WorkflowTemplateId);
            versionDtos.Add(new WorkflowVersionSummaryDto(
                version.WorkflowTemplateId, version.VersionNumber, version.Status, version.Name,
                version.CreatedAtUtc, await NameOfAsync(version.CreatedByEmployeeId, cancellationToken),
                version.PublishedAtUtc, await NameOfAsync(version.PublishedByEmployeeId, cancellationToken),
                version.Steps.Count, ticketCount,
                CanDelete: version.IsDraft && ticketCount == 0));
        }

        var requestTypes = await requestTypeRepository.ListByWorkflowIdAsync(workflowId, cancellationToken);
        var departments = (await departmentRepository.ListAsync(activeOnly: false, cancellationToken)).ToDictionary(d => d.DepartmentId, d => d.Name);

        return new AdminWorkflowDetailDto(
            workflow.WorkflowId, workflow.Name, workflow.Description, workflow.IsActive, workflow.CreatedAtUtc,
            versionDtos,
            requestTypes.Select(r => new WorkflowRequestTypeUsageDto(
                r.RequestTypeId, r.Name, departments.GetValueOrDefault(r.DepartmentId, "Unknown department"), r.IsActive)).ToList());
    }

    public async Task<WorkflowVersionDetailDto?> GetVersionAsync(int workflowTemplateId, CancellationToken cancellationToken = default)
    {
        var version = await versionRepository.GetByIdAsync(workflowTemplateId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var workflow = await workflowRepository.GetByIdAsync(version.WorkflowId, cancellationToken);
        var ticketCount = await versionRepository.CountPinnedTicketsAsync(workflowTemplateId, cancellationToken);
        var validation = version.IsDraft ? version.Validate() : [];

        return new WorkflowVersionDetailDto(
            version.WorkflowTemplateId,
            version.WorkflowId,
            workflow?.Name ?? version.Name,
            version.VersionNumber,
            version.Status,
            version.Name,
            version.Description,
            version.AllowsPendingCustomer,
            version.AllowsPendingInternal,
            version.RequiresApproval,
            version.CreatedAtUtc,
            await NameOfAsync(version.CreatedByEmployeeId, cancellationToken),
            version.PublishedAtUtc,
            await NameOfAsync(version.PublishedByEmployeeId, cancellationToken),
            ticketCount,
            IsEditable: version.IsDraft,
            CanPublish: version.IsDraft && validation.All(i => i.Severity != WorkflowValidationSeverity.Error),
            CanDelete: version.IsDraft && ticketCount == 0,
            version.Steps.Select(ToStepDto).ToList(),
            validation.Select(i => new WorkflowValidationIssueDto(i.Severity, i.StepSequence, i.Message)).ToList());
    }

    /// <summary>Creates a logical workflow plus its Draft version 1, pre-filled with the standard skeleton so the designer starts from something valid.</summary>
    public async Task<AdminResult<AdminWorkflowDetailDto>> CreateAsync(
        Guid actorEmployeeId, CreateWorkflowRequestDto request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminResult<AdminWorkflowDetailDto>.Invalid("Name is required.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var code = await UniqueCodeAsync(Workflow.CodeFromName(request.Name), cancellationToken);
        var workflow = new Workflow(code, request.Name, request.Description, now);
        await workflowRepository.AddAsync(workflow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var draft = new WorkflowTemplate(
            workflow.WorkflowId, 1, VersionCode(workflow.Code, 1), workflow.Name, workflow.Description,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false,
            now, actorEmployeeId);
        draft.AppendStep("Ticket Created", WorkflowStepKind.Created);
        draft.AppendStep("Department Queue", WorkflowStepKind.Assigned);
        draft.AppendStep("Department Work", WorkflowStepKind.InProgress);
        draft.AppendStep("Resolve", WorkflowStepKind.Resolved);
        draft.AppendStep("Close", WorkflowStepKind.Closed);
        await versionRepository.AddAsync(draft, cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateWorkflow", "Workflow", workflow.WorkflowId.ToString(),
            beforeValue: null, afterValue: $"Code={workflow.Code};Name={workflow.Name};DraftVersion=1", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminWorkflowDetailDto>.Success((await GetAsync(workflow.WorkflowId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminWorkflowDetailDto>> UpdateAsync(
        Guid actorEmployeeId, int workflowId, UpdateWorkflowRequestDto request, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return AdminResult<AdminWorkflowDetailDto>.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return AdminResult<AdminWorkflowDetailDto>.Invalid("Name is required.");
        }

        var before = $"Name={workflow.Name};Description={workflow.Description}";
        workflow.Rename(request.Name, request.Description);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateWorkflow", "Workflow", workflowId.ToString(),
            before, $"Name={workflow.Name};Description={workflow.Description}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminWorkflowDetailDto>.Success((await GetAsync(workflowId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminWorkflowDetailDto>> SetActivationAsync(
        Guid actorEmployeeId, int workflowId, SetActiveRequestDto request, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return AdminResult<AdminWorkflowDetailDto>.NotFound();
        }

        if (!request.IsActive)
        {
            var activeUsers = (await requestTypeRepository.ListByWorkflowIdAsync(workflowId, cancellationToken)).Where(r => r.IsActive).ToList();
            if (activeUsers.Count > 0)
            {
                return AdminResult<AdminWorkflowDetailDto>.Conflict(
                    $"This workflow is still used by active request type(s): {string.Join(", ", activeUsers.Select(r => r.Name))}. Re-point or deactivate them first.");
            }
        }

        var before = workflow.IsActive;
        if (request.IsActive)
        {
            workflow.Activate();
        }
        else
        {
            workflow.Deactivate();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, request.IsActive ? "AdminActivateWorkflow" : "AdminDeactivateWorkflow", "Workflow", workflowId.ToString(),
            $"IsActive={before}", $"IsActive={workflow.IsActive};Reason={request.Reason}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminWorkflowDetailDto>.Success((await GetAsync(workflowId, cancellationToken))!);
    }

    /// <summary>"Create New Version": a new Draft copied from the Published version (or, before any publication, the latest version).</summary>
    public async Task<AdminResult<WorkflowVersionDetailDto>> CreateVersionAsync(
        Guid actorEmployeeId, int workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await workflowRepository.GetByIdAsync(workflowId, cancellationToken);
        if (workflow is null)
        {
            return AdminResult<WorkflowVersionDetailDto>.NotFound();
        }

        var versions = await versionRepository.ListByWorkflowIdAsync(workflowId, cancellationToken);
        var existingDraft = versions.FirstOrDefault(v => v.IsDraft);
        if (existingDraft is not null)
        {
            return AdminResult<WorkflowVersionDetailDto>.Conflict(
                $"Version {existingDraft.VersionNumber} is still a Draft. Publish or delete it before creating another version.");
        }

        var source = versions.FirstOrDefault(v => v.IsPublished) ?? versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        var nextNumber = versions.Count == 0 ? 1 : versions.Max(v => v.VersionNumber) + 1;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var draft = new WorkflowTemplate(
            workflowId, nextNumber, VersionCode(workflow.Code, nextNumber),
            source?.Name ?? workflow.Name, source?.Description ?? workflow.Description,
            source?.AllowsPendingCustomer ?? false, source?.AllowsPendingInternal ?? false, source?.RequiresApproval ?? false,
            now, actorEmployeeId);
        if (source is not null)
        {
            draft.CopyStepsFrom(source);
        }

        await versionRepository.AddAsync(draft, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateWorkflowVersion", "WorkflowTemplate", draft.WorkflowTemplateId.ToString(),
            beforeValue: source is null ? null : $"CopiedFromVersion={source.VersionNumber}",
            afterValue: $"WorkflowId={workflowId};Version={nextNumber};Status=Draft", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<WorkflowVersionDetailDto>.Success((await GetVersionAsync(draft.WorkflowTemplateId, cancellationToken))!);
    }

    public Task<AdminResult<WorkflowVersionDetailDto>> UpdateVersionSettingsAsync(
        Guid actorEmployeeId, int workflowTemplateId, UpdateVersionSettingsRequestDto request, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminUpdateWorkflowVersionSettings", cancellationToken, version =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return "Name is required.";
            }

            version.UpdateSettings(request.Name, request.Description, request.AllowsPendingCustomer, request.AllowsPendingInternal, request.RequiresApproval);
            return null;
        });

    public Task<AdminResult<WorkflowVersionDetailDto>> AddStepAsync(
        Guid actorEmployeeId, int workflowTemplateId, SaveStepRequestDto request, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminAddWorkflowStep", cancellationToken, version =>
        {
            if (ValidateStep(request) is { } error)
            {
                return error;
            }

            version.AppendStep(request.Name, request.Kind, request.IsOptional, request.ApprovalType);
            return null;
        });

    public Task<AdminResult<WorkflowVersionDetailDto>> UpdateStepAsync(
        Guid actorEmployeeId, int workflowTemplateId, int stepId, SaveStepRequestDto request, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminUpdateWorkflowStep", cancellationToken, version =>
        {
            if (ValidateStep(request) is { } error)
            {
                return error;
            }

            version.UpdateStep(stepId, request.Name, request.Kind, request.IsOptional, request.ApprovalType);
            return null;
        });

    public Task<AdminResult<WorkflowVersionDetailDto>> RemoveStepAsync(
        Guid actorEmployeeId, int workflowTemplateId, int stepId, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminRemoveWorkflowStep", cancellationToken, version =>
        {
            version.RemoveStep(stepId);
            return null;
        });

    public Task<AdminResult<WorkflowVersionDetailDto>> MoveStepAsync(
        Guid actorEmployeeId, int workflowTemplateId, int stepId, MoveStepRequestDto request, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminMoveWorkflowStep", cancellationToken, version =>
        {
            if (request.Direction == StepMoveDirection.Up)
            {
                version.MoveStepUp(stepId);
            }
            else
            {
                version.MoveStepDown(stepId);
            }

            return null;
        });

    public Task<AdminResult<WorkflowVersionDetailDto>> SetTransitionAsync(
        Guid actorEmployeeId, int workflowTemplateId, int stepId, SetTransitionRequestDto request, CancellationToken cancellationToken = default) =>
        EditDraftAsync(actorEmployeeId, workflowTemplateId, "AdminSetWorkflowStepTransition", cancellationToken, version =>
        {
            if (!Enum.IsDefined(request.Outcome))
            {
                return "The outcome is not one of the supported outcomes.";
            }

            version.SetStepTransition(stepId, request.Outcome, request.TargetStepId);
            return null;
        });

    /// <summary>Draft → Published: validates, publishes, and marks the previously Published version Historical. Existing tickets are never touched.</summary>
    public async Task<AdminResult<WorkflowVersionDetailDto>> PublishAsync(
        Guid actorEmployeeId, int workflowTemplateId, CancellationToken cancellationToken = default)
    {
        var version = await versionRepository.GetByIdAsync(workflowTemplateId, cancellationToken);
        if (version is null)
        {
            return AdminResult<WorkflowVersionDetailDto>.NotFound();
        }

        if (!version.IsDraft)
        {
            return AdminResult<WorkflowVersionDetailDto>.Conflict(
                $"Version {version.VersionNumber} is {version.Status} and cannot be published again. Create a new version instead.");
        }

        var issues = version.Validate();
        var errors = issues.Where(i => i.Severity == WorkflowValidationSeverity.Error).Select(i => i.Message).ToList();
        if (errors.Count > 0)
        {
            return AdminResult<WorkflowVersionDetailDto>.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previous = await versionRepository.GetPublishedAsync(version.WorkflowId, cancellationToken);
        previous?.MarkHistorical();
        version.Publish(now, actorEmployeeId);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminPublishWorkflowVersion", "WorkflowTemplate", workflowTemplateId.ToString(),
            previous is null ? null : $"PreviousPublishedVersion={previous.VersionNumber}",
            $"WorkflowId={version.WorkflowId};Version={version.VersionNumber};Status=Published", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<WorkflowVersionDetailDto>.Success((await GetVersionAsync(workflowTemplateId, cancellationToken))!);
    }

    /// <summary>Deletes a Draft that nothing references. Published and Historical versions are never deletable — tickets pin them.</summary>
    public async Task<AdminResult> DeleteDraftAsync(Guid actorEmployeeId, int workflowTemplateId, CancellationToken cancellationToken = default)
    {
        var version = await versionRepository.GetByIdAsync(workflowTemplateId, cancellationToken);
        if (version is null)
        {
            return AdminResult.NotFound();
        }

        if (!version.IsDraft)
        {
            return AdminResult.Conflict($"Version {version.VersionNumber} is {version.Status}; published and historical versions are never deleted.");
        }

        if (await versionRepository.CountPinnedTicketsAsync(workflowTemplateId, cancellationToken) > 0)
        {
            return AdminResult.Conflict("This version is referenced by tickets and cannot be deleted.");
        }

        var versions = await versionRepository.ListByWorkflowIdAsync(version.WorkflowId, cancellationToken);
        if (versions.Count == 1 && (await requestTypeRepository.ListByWorkflowIdAsync(version.WorkflowId, cancellationToken)).Count > 0)
        {
            return AdminResult.Conflict("This is the workflow's only version and request types still reference the workflow.");
        }

        versionRepository.Remove(version);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminDeleteWorkflowDraft", "WorkflowTemplate", workflowTemplateId.ToString(),
            $"WorkflowId={version.WorkflowId};Version={version.VersionNumber};Status=Draft", afterValue: null, Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult.Success();
    }

    private async Task<AdminResult<WorkflowVersionDetailDto>> EditDraftAsync(
        Guid actorEmployeeId, int workflowTemplateId, string auditAction, CancellationToken cancellationToken,
        Func<WorkflowTemplate, string?> edit)
    {
        var version = await versionRepository.GetByIdAsync(workflowTemplateId, cancellationToken);
        if (version is null)
        {
            return AdminResult<WorkflowVersionDetailDto>.NotFound();
        }

        if (!version.IsDraft)
        {
            return AdminResult<WorkflowVersionDetailDto>.Conflict(
                $"Version {version.VersionNumber} is {version.Status} and is read-only. Create a new version to make changes.");
        }

        var before = DescribeSteps(version);
        try
        {
            if (edit(version) is { } error)
            {
                return AdminResult<WorkflowVersionDetailDto>.Invalid(error);
            }
        }
        catch (WorkflowStepNotFoundException)
        {
            return AdminResult<WorkflowVersionDetailDto>.NotFound();
        }
        catch (WorkflowConfigurationException ex)
        {
            return AdminResult<WorkflowVersionDetailDto>.Invalid(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return AdminResult<WorkflowVersionDetailDto>.Invalid(ex.Message);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, auditAction, "WorkflowTemplate", workflowTemplateId.ToString(),
            before, DescribeSteps(version), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<WorkflowVersionDetailDto>.Success((await GetVersionAsync(workflowTemplateId, cancellationToken))!);
    }

    private static string? ValidateStep(SaveStepRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Step name is required.";
        }

        if (!WorkflowStepKinds.IsSupported(request.Kind))
        {
            return "The step type is not one of the supported step types.";
        }

        if (request.ApprovalType is { } type && !Enum.IsDefined(type))
        {
            return "The approval type is not one of the supported approval types.";
        }

        return null;
    }

    private static WorkflowVersionStepDto ToStepDto(WorkflowTemplateStep step)
    {
        var info = WorkflowStepKinds.Describe(step.Kind);
        return new WorkflowVersionStepDto(
            step.WorkflowTemplateStepId,
            step.Sequence,
            step.Name,
            step.Kind,
            info.Label,
            step.IsOptional,
            step.ApprovalType,
            step.ApprovalType is { } type ? ApprovalTypeLabels.Label(type) : null,
            info.RequiresApprovalType,
            info.SupportsOutcomeBranches,
            step.Transitions
                .Where(t => t.TargetStep is not null)
                .OrderBy(t => t.Outcome)
                .Select(t => new StepTransitionDto(t.Outcome, t.TargetStep!.WorkflowTemplateStepId, t.TargetStep.Sequence, t.TargetStep.Name))
                .ToList());
    }

    private static string DescribeSteps(WorkflowTemplate version) =>
        $"Name={version.Name};Steps=" + string.Join("|", version.Steps.Select(s =>
            $"{s.Sequence}:{s.Name}:{s.Kind}" + (s.ApprovalType is { } t ? $":{t}" : string.Empty)
            + string.Concat(s.Transitions.Where(x => x.TargetStep is not null).Select(x => $":{x.Outcome}->{x.TargetStep!.Sequence}"))));

    private static string VersionCode(string workflowCode, int versionNumber) =>
        versionNumber == 1 ? workflowCode : $"{workflowCode}-V{versionNumber}";

    private async Task<string> UniqueCodeAsync(string baseCode, CancellationToken cancellationToken)
    {
        var code = baseCode;
        var suffix = 2;
        while (await workflowRepository.CodeExistsAsync(code, cancellationToken))
        {
            var tail = $"-{suffix++}";
            code = baseCode[..Math.Min(baseCode.Length, Workflow.CodeMaxLength - tail.Length)] + tail;
        }

        return code;
    }

    private async Task<string?> NameOfAsync(Guid? employeeId, CancellationToken cancellationToken)
    {
        if (employeeId is not { } id)
        {
            return null;
        }

        var employee = await employeeRepository.GetByIdAsync(id, cancellationToken);
        return employee?.DisplayName ?? "Unknown user";
    }
}
