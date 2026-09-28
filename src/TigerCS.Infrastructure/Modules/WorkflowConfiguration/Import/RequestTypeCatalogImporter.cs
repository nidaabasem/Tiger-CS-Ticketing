using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <param name="Apply">False (the default) is a dry run: everything is planned and validated, nothing is written.</param>
/// <param name="ActivateResolved">
/// False (the default): every created request type stays an inactive draft.
/// True: a row with no open decision is created Active with its workflow
/// Published. The first import into a shared environment runs with false.
/// </param>
/// <param name="LinkExisting">
/// False (the default): a request type that already exists under the same
/// department and name is left completely untouched — not even the catalog
/// code is written — and only reported with a current-vs-workbook
/// comparison. True: the code is attached to it (nothing else changes).
/// </param>
/// <param name="AllowAgentPriorityChange">
/// The business's answer for "may agents change the priority away from the
/// default" on the created request types. The workbook has no such column
/// and TigerCS has no default to inherit, so while this is null the value is
/// an open decision on every created row (see <see cref="RequestTypeCatalogImporter"/>).
/// </param>
/// <param name="SchemaApplied">
/// False when the AddRequestTypeCatalogImport migration has not been applied
/// yet: only a dry run is possible, and it reads nothing but pre-existing
/// columns (no request type can carry a catalog code yet).
/// </param>
public sealed record RequestTypeCatalogImportOptions(
    DateTime NowUtc,
    bool Apply = false,
    bool ActivateResolved = false,
    bool LinkExisting = false,
    bool? AllowAgentPriorityChange = null,
    bool SchemaApplied = true);

public enum RequestTypeImportOutcome
{
    /// <summary>Created, every value resolved and activation requested: request type Active, workflow v1 Published.</summary>
    CreatedActive,

    /// <summary>Created as an inactive draft: request type and workflow inactive, workflow v1 left as an unpublished Draft.</summary>
    CreatedInactiveDraft,

    /// <summary>A request type with the same department and name already exists; nothing about it was written.</summary>
    ExistingUnchanged,

    /// <summary>A request type with the same department and name already existed; only the catalog code was attached (opt-in).</summary>
    LinkedToExisting,

    /// <summary>A request type with this code already exists (a re-run); left exactly as it is.</summary>
    AlreadyImported,

    /// <summary>Nothing could be created — see the decisions (e.g. the department does not exist).</summary>
    Skipped
}

/// <summary>The configuration an existing same-named request type has today, as read from the target database.</summary>
public sealed record ExistingRequestTypeSnapshot(
    int RequestTypeId,
    bool IsActive,
    PriorityLevel DefaultPriority,
    bool AllowPendingCustomer,
    bool AllowAgentPriorityChange,
    bool AllowReopen,
    string WorkflowDescription,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Slas,
    IReadOnlyList<string> Approvals,
    string? ReopenRequestRoute = null,
    bool ConfigurationEnforced = false);

public sealed record RequestTypeImportResult(
    RequestTypeImportPlan Plan,
    RequestTypeImportOutcome Outcome,
    IReadOnlyList<CatalogDecision> Decisions,
    int? RequestTypeId = null,
    ExistingRequestTypeSnapshot? Existing = null);

/// <summary>
/// Imports the request-type catalog into the existing Department → Request
/// Type → Workflow → SLA configuration. Safe to re-run: the catalog's
/// Request Code is stored on each request type it creates (unique) and a row
/// whose code already exists is left untouched, so a second run creates
/// nothing and changes nothing — configuration edits after import are
/// deliberate Administration changes, never overwritten by a re-import (the
/// same rule as <see cref="WorkflowReferenceData.SeedAsync"/>).
///
/// <para>
/// <b>Existing request types are never modified by default.</b> A same-named
/// request type in the same department is reported with a current-vs-
/// workbook comparison and left exactly as it is.
/// </para>
///
/// <para>
/// <b>Nothing goes live by default.</b> Created request types and their
/// workflows are inactive and the workflow version stays an unpublished
/// Draft unless activation is explicitly requested, and even then only for a
/// row with no open <see cref="CatalogDecision"/>. Unresolved approvals
/// create no approval requirement at all.
/// </para>
///
/// <para>
/// <b>Settings the workbook does not state are inherited, never switched
/// off.</b> Pending Customer stays available — the established behaviour for
/// a ticket whose request type does not restrict it
/// (<c>TicketLifecycleAppService</c> allows it whenever no capability says
/// otherwise). Agent priority change has no inheritable default (it is a
/// required per-request-type value, set individually on every existing
/// type, and nothing enforces it today), so it is recorded as allowed —
/// matching today's unrestricted behaviour — and flagged until
/// <see cref="RequestTypeCatalogImportOptions.AllowAgentPriorityChange"/>
/// carries the business's answer.
/// </para>
/// </summary>
public static class RequestTypeCatalogImporter
{
    public const string WorkflowCodePrefix = "RT-";

    /// <summary>What a created request type gets for the one required setting nothing can be inherited for, while the business has not answered.</summary>
    public const bool UndecidedAgentPriorityChangePlaceholder = true;

    public static string WorkflowCodeFor(string requestCode) => WorkflowCodePrefix + requestCode.Trim();

    public static CatalogDecision AgentPriorityChangeDecision { get; } = new(
        CatalogDecisionArea.Configuration,
        "Agent priority change is not in the workbook and has no default to inherit (it is set per request type). "
        + "Nothing enforces it today — agents choose any priority — so it is stored as allowed to match that. "
        + "Confirm allow or deny for the new request types.");

    public static async Task<RequestTypeCatalogImportReport> ImportAsync(
        TigerCsDbContext dbContext,
        IReadOnlyList<RequestTypeCatalogRow> rows,
        RequestTypeCatalogImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Apply && !options.SchemaApplied)
        {
            throw new InvalidOperationException("The catalog import cannot be applied before the AddRequestTypeCatalogImport migration.");
        }

        foreach (var row in rows)
        {
            if (WorkflowCodeFor(row.RequestCode).Length > Workflow.CodeMaxLength)
            {
                throw new InvalidOperationException(
                    $"Request Code '{row.RequestCode}' is too long: at most {Workflow.CodeMaxLength - WorkflowCodePrefix.Length} characters.");
            }
        }

        var departments = await LoadDepartmentsAsync(dbContext, cancellationToken);

        await using var transaction = options.Apply && dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var results = new List<RequestTypeImportResult>();
        foreach (var row in rows)
        {
            var plan = RequestTypeCatalogMapper.Map(row, departments);
            results.Add(await ImportRowAsync(dbContext, plan, departments, options, cancellationToken));
        }

        if (options.Apply)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return new RequestTypeCatalogImportReport(results, options);
    }

    private static async Task<Dictionary<string, CatalogDepartment>> LoadDepartmentsAsync(
        TigerCsDbContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.DepartmentWorkflowSettings
            .AsNoTracking()
            .ToDictionaryAsync(s => s.DepartmentId, s => s.AllowTransferToOtherDepartments, cancellationToken);

        var departments = await dbContext.Departments.AsNoTracking().ToListAsync(cancellationToken);

        return departments.ToDictionary(
            d => d.Name.Trim(),
            d => new CatalogDepartment(d.DepartmentId, d.Name, d.IsActive, settings.GetValueOrDefault(d.DepartmentId, true)),
            StringComparer.OrdinalIgnoreCase);
    }

    // Every read below projects explicit pre-existing columns, so a dry run
    // works against a database the migration has not reached yet.
    private static async Task<RequestTypeImportResult> ImportRowAsync(
        TigerCsDbContext dbContext,
        RequestTypeImportPlan plan,
        IReadOnlyDictionary<string, CatalogDepartment> departments,
        RequestTypeCatalogImportOptions options,
        CancellationToken cancellationToken)
    {
        var code = plan.Code;

        // ---- re-run: the code is the identity ---------------------------------
        if (options.SchemaApplied)
        {
            var byCode = await dbContext.RequestTypes
                .Where(r => r.Code == code)
                .Select(r => new { r.RequestTypeId, r.IsActive, r.WorkflowId })
                .FirstOrDefaultAsync(cancellationToken);
            if (byCode is not null)
            {
                // Open decisions stay on the report while the row is inactive,
                // or when it was linked rather than created (its workflow is
                // not the import's own).
                var createdByImport = await dbContext.Workflows.AnyAsync(
                    w => w.WorkflowId == byCode.WorkflowId && w.Code == WorkflowCodeFor(code), cancellationToken);
                var stillOpen = !byCode.IsActive || !createdByImport ? plan.Decisions : [];
                return new(plan, RequestTypeImportOutcome.AlreadyImported, stillOpen, byCode.RequestTypeId);
            }
        }

        if (!plan.CanCreate)
        {
            return new(plan, RequestTypeImportOutcome.Skipped, plan.Decisions);
        }

        var department = plan.Department!;
        var name = plan.Source.Name.Trim();

        // ---- a same-named request type already exists in the department ---------
        var existingId = await dbContext.RequestTypes
            .Where(r => r.DepartmentId == department.DepartmentId && r.Name == name)
            .Select(r => (int?)r.RequestTypeId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId is { } requestTypeId)
        {
            var snapshot = await SnapshotAsync(dbContext, requestTypeId, departments, options.SchemaApplied, cancellationToken);
            var decisions = new List<CatalogDecision>
            {
                new(CatalogDecisionArea.ExistingRequestType,
                    $"'{name}' already exists in {department.Name} and was left unchanged. "
                    + "Decide what, if anything, the workbook should change on it — see the current-vs-workbook comparison.")
            };
            decisions.AddRange(plan.Decisions);

            if (!options.LinkExisting)
            {
                return new(plan, RequestTypeImportOutcome.ExistingUnchanged, decisions, requestTypeId, snapshot);
            }

            var existingCode = await dbContext.RequestTypes
                .Where(r => r.RequestTypeId == requestTypeId)
                .Select(r => r.Code)
                .SingleAsync(cancellationToken);
            if (existingCode is not null)
            {
                decisions.Add(new(CatalogDecisionArea.ExistingRequestType,
                    $"'{name}' in {department.Name} already carries catalog code '{existingCode}', not '{code}'. Confirm which code is correct."));
                return new(plan, RequestTypeImportOutcome.Skipped, decisions, requestTypeId, snapshot);
            }

            if (options.Apply)
            {
                var tracked = await dbContext.RequestTypes.SingleAsync(r => r.RequestTypeId == requestTypeId, cancellationToken);
                tracked.AssignCode(code);
            }

            return new(plan, RequestTypeImportOutcome.LinkedToExisting, decisions, requestTypeId, snapshot);
        }

        var workflowCode = WorkflowCodeFor(code);
        if (await dbContext.Workflows.AnyAsync(w => w.Code == workflowCode, cancellationToken))
        {
            return new(plan, RequestTypeImportOutcome.Skipped,
            [
                .. plan.Decisions,
                new(CatalogDecisionArea.ExistingRequestType,
                    $"A workflow with code '{workflowCode}' already exists without its request type. Review it in the Workflow Designer before re-importing.")
            ]);
        }

        // ---- create -------------------------------------------------------------
        var created = plan.Decisions.ToList();
        var validation = BuildVersion(plan, workflowId: 0, options.NowUtc, created.Count).Validate()
            .Where(i => i.Severity == WorkflowValidationSeverity.Error)
            .ToList();
        foreach (var issue in validation)
        {
            created.Add(new(CatalogDecisionArea.Workflow, "The mapped workflow is not publishable: " + issue.Message));
        }

        if (options.AllowAgentPriorityChange is null)
        {
            created.Add(AgentPriorityChangeDecision);
        }

        var activate = options.ActivateResolved && created.Count == 0;
        var outcome = activate ? RequestTypeImportOutcome.CreatedActive : RequestTypeImportOutcome.CreatedInactiveDraft;

        if (!options.Apply)
        {
            return new(plan, outcome, created);
        }

        var workflow = new Workflow(workflowCode, WorkflowName(plan), WorkflowDescription(plan, created.Count), options.NowUtc, isActive: activate);
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var version = BuildVersion(plan, workflow.WorkflowId, options.NowUtc, created.Count);
        if (activate)
        {
            version.Publish(options.NowUtc, publishedByEmployeeId: null);
        }

        dbContext.WorkflowTemplates.Add(version);

        var requestType = new RequestType(
            department.DepartmentId,
            name,
            workflow.WorkflowId,
            (byte)plan.DefaultPriority!.Value,
            options.AllowAgentPriorityChange ?? UndecidedAgentPriorityChangePlaceholder,
            // Inherited, not switched off: Pending Customer is available
            // unless a request type restricts it, and the workbook does not.
            allowPendingCustomer: true,
            allowPendingInternal: false,
            plan.AllowReopen,
            plan.RequiredFieldsJson,
            isActive: activate);
        requestType.SetCatalogDetails(code, plan.Source.RequestGroup, plan.Source.BusinessDescription, plan.RequiredDocumentsJson);
        dbContext.RequestTypes.Add(requestType);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Every open question is kept with the request type: while any is
        // unresolved, Administration refuses to activate it or to enforce its
        // configuration.
        foreach (var decision in created)
        {
            dbContext.RequestTypeCatalogDecisions.Add(
                new RequestTypeCatalogDecision(requestType.RequestTypeId, decision.Area.ToString(), decision.Question, options.NowUtc));
        }

        if (plan.Sla is { } sla)
        {
            dbContext.RequestTypeSlaPolicies.Add(new RequestTypeSlaPolicy(
                requestType.RequestTypeId,
                (byte)sla.Priority,
                SlaTriggerType.TicketCreated,
                sla.ResolutionUnit,
                firstResponseTargetValue: sla.FirstResponseValue,
                firstResponseMaximumValue: null,
                resolutionTargetValue: sla.ResolutionValue,
                resolutionMaximumValue: null,
                clockBasis: sla.ClockBasis,
                firstResponseUnit: sla.FirstResponseUnit));
        }

        // Reopen is AllowReopen and nothing more: the approved rule is direct
        // Reopen by CS Agent, CS Supervisor and CS Manager (System
        // Administrator through the central override). The import creates no
        // approval requirement of any kind — in particular no Reopen Approval,
        // which would open a reopen-request path for other roles on these types.

        return new(plan, outcome, created, requestType.RequestTypeId);
    }

    private static WorkflowTemplate BuildVersion(RequestTypeImportPlan plan, int workflowId, DateTime nowUtc, int openDecisions)
    {
        var version = new WorkflowTemplate(
            workflowId,
            versionNumber: 1,
            WorkflowCodeFor(plan.Code) + "-V1",
            WorkflowName(plan),
            WorkflowDescription(plan, openDecisions),
            // Inherited Pending Customer — see the class remarks.
            allowsPendingCustomer: true,
            allowsPendingInternal: false,
            requiresApproval: false,
            nowUtc,
            createdByEmployeeId: null);

        foreach (var step in plan.Steps)
        {
            version.AppendStep(step.Name, step.Kind, step.IsOptional, approvalType: null, step.DepartmentId);
        }

        return version;
    }

    private static string WorkflowName(RequestTypeImportPlan plan) => Clip($"{plan.Source.Name.Trim()} ({plan.Code})", 100);

    private static string WorkflowDescription(RequestTypeImportPlan plan, int openDecisions) =>
        Clip(
            $"Imported from the request-type catalog ({plan.Code}). Proposed workflow: {plan.Source.ProposedWorkflow.Trim()}."
            + (openDecisions > 0 ? $" Inactive draft: {openDecisions} business decision(s) open — see the import report." : string.Empty),
            500);

    private static async Task<ExistingRequestTypeSnapshot> SnapshotAsync(
        TigerCsDbContext dbContext, int requestTypeId, IReadOnlyDictionary<string, CatalogDepartment> departments,
        bool schemaApplied, CancellationToken cancellationToken)
    {
        var requestType = await dbContext.RequestTypes
            .Where(r => r.RequestTypeId == requestTypeId)
            .Select(r => new { r.IsActive, r.DefaultPriorityId, r.AllowPendingCustomer, r.AllowAgentPriorityChange, r.AllowReopen, r.WorkflowId })
            .SingleAsync(cancellationToken);

        var workflowName = await dbContext.Workflows
            .Where(w => w.WorkflowId == requestType.WorkflowId)
            .Select(w => w.Name)
            .SingleOrDefaultAsync(cancellationToken);
        var published = await dbContext.WorkflowTemplates
            .Where(t => t.WorkflowId == requestType.WorkflowId && t.Status == WorkflowVersionStatus.Published)
            .Select(t => new { t.WorkflowTemplateId, t.VersionNumber, t.AllowsPendingCustomer })
            .SingleOrDefaultAsync(cancellationToken);

        var steps = published is null
            ? []
            : (await dbContext.Set<WorkflowTemplateStep>()
                .Where(s => s.WorkflowTemplateId == published.WorkflowTemplateId)
                .OrderBy(s => s.Sequence)
                .Select(s => new { s.Name, s.Kind, s.IsOptional, s.ApprovalType })
                .ToListAsync(cancellationToken))
                .Select(s => s.Name + (s.IsOptional ? " (optional)" : string.Empty) + (s.ApprovalType is { } a ? $" [{a}]" : string.Empty))
                .ToList();

        var slaRows = await dbContext.RequestTypeSlaPolicies
            .Where(p => p.RequestTypeId == requestTypeId)
            .OrderBy(p => p.PriorityId)
            .Select(p => new
            {
                p.RequestTypeSlaPolicyId, p.PriorityId, p.Trigger, p.Unit, p.FirstResponseTargetValue, p.FirstResponseMaximumValue,
                p.ResolutionTargetValue, p.ResolutionMaximumValue, p.IsImmediate, p.ClockBasis, p.IsActive
            })
            .ToListAsync(cancellationToken);
        var firstResponseUnits = schemaApplied
            ? await dbContext.RequestTypeSlaPolicies
                .Where(p => p.RequestTypeId == requestTypeId && p.FirstResponseUnit != null)
                .ToDictionaryAsync(p => p.RequestTypeSlaPolicyId, p => p.FirstResponseUnit!.Value, cancellationToken)
            : [];
        var slas = slaRows.Select(p =>
                $"{(PriorityLevel)p.PriorityId}: first response {Duration(p.FirstResponseTargetValue, p.FirstResponseMaximumValue, firstResponseUnits.GetValueOrDefault(p.RequestTypeSlaPolicyId, p.Unit))}, "
                + $"resolution {(p.IsImmediate ? "immediately" : Duration(p.ResolutionTargetValue, p.ResolutionMaximumValue, p.Unit))}, "
                + $"clock starts {p.Trigger}, basis {p.ClockBasis?.ToString() ?? "not decided"}{(p.IsActive ? string.Empty : " (inactive)")}")
            .ToList();

        var departmentNames = departments.Values.ToDictionary(d => d.DepartmentId, d => d.Name);
        var requirements = await dbContext.RequestTypeApprovalRequirements
            .AsNoTracking()
            .Where(a => a.RequestTypeId == requestTypeId)
            .OrderBy(a => a.ApprovalType)
            .ToListAsync(cancellationToken);

        // A ReopenApproval row is never an approval the work waits for, and
        // not part of the approved Reopen rule (direct Reopen by the CS layer,
        // System Administrator through the central override). An existing
        // type may still carry one from earlier configuration; it is reported
        // factually with Reopen, never as a gating approval, and left as is.
        var reopenRequest = requirements.FirstOrDefault(a => a.ApprovalType == ApprovalType.ReopenApproval);
        var reopenRequestRoute = reopenRequest is null
            ? null
            : $"pre-existing ReopenApproval requirement row ({(reopenRequest.TargetRoleName is { } decider ? $"role {decider}" : "configured approver")}"
              + (reopenRequest.IsActive ? ", active" : ", inactive") + ") — left unchanged; not part of the approved direct-Reopen rule";

        var approvals = requirements
            .Where(a => a.ApprovalType != ApprovalType.ReopenApproval)
            .Select(a => $"{a.ApprovalType} by " + a.TargetKind switch
            {
                ApprovalTargetKind.Department => $"department {departmentNames.GetValueOrDefault(a.TargetDepartmentId ?? 0, $"#{a.TargetDepartmentId}")}"
                                                 + (a.TargetRoleName is { } role ? $" ({role})" : string.Empty),
                ApprovalTargetKind.Role => $"role {a.TargetRoleName}",
                _ => "a named employee"
            } + (a.BlocksWorkUntilApproved ? ", blocks work" : ", does not block work") + (a.IsActive ? string.Empty : " (inactive)"))
            .ToList();

        var workflowDescription = workflowName is null
            ? "none"
            : published is null
                ? $"{workflowName} (no published version)"
                : $"{workflowName} v{published.VersionNumber}";

        return new ExistingRequestTypeSnapshot(
            requestTypeId,
            requestType.IsActive,
            (PriorityLevel)requestType.DefaultPriorityId,
            requestType.AllowPendingCustomer && (published?.AllowsPendingCustomer ?? false),
            requestType.AllowAgentPriorityChange,
            requestType.AllowReopen,
            workflowDescription,
            steps,
            slas,
            approvals,
            reopenRequestRoute,
            schemaApplied && await dbContext.RequestTypes
                .Where(r => r.RequestTypeId == requestTypeId)
                .Select(r => r.ConfigurationEnforced)
                .SingleAsync(cancellationToken));
    }

    internal static string Duration(int? target, int? maximum, SlaDurationUnit unit) => (target, maximum) switch
    {
        (null, null) => "not set",
        ({ } t, null) => $"{t} {unit.ToString().ToLowerInvariant()}",
        (null, { } m) => $"up to {m} {unit.ToString().ToLowerInvariant()}",
        ({ } t, { } m) => $"{t}–{m} {unit.ToString().ToLowerInvariant()}"
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
