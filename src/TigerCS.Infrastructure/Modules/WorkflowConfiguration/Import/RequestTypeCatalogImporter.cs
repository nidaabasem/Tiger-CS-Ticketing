using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <param name="Apply">False (the default) is a dry run: everything is planned and validated, nothing is written.</param>
/// <param name="ActivateResolved">
/// When true, a row whose every value resolved is created Active with its
/// workflow Published. When false, every created row stays an inactive
/// draft regardless — for an environment where nothing may go live before
/// the business has signed off the workbook.
/// </param>
public sealed record RequestTypeCatalogImportOptions(DateTime NowUtc, bool Apply = false, bool ActivateResolved = true);

public enum RequestTypeImportOutcome
{
    /// <summary>Created, every value resolved: request type Active, workflow v1 Published.</summary>
    CreatedActive,

    /// <summary>Created as an inactive draft: request type and workflow inactive, workflow v1 left as an unpublished Draft.</summary>
    CreatedInactiveDraft,

    /// <summary>A request type with the same department and name already existed without a code; the code was attached and nothing else changed.</summary>
    LinkedToExisting,

    /// <summary>A request type with this code already exists (a re-run); left exactly as it is.</summary>
    AlreadyImported,

    /// <summary>Nothing could be created — see the decisions (e.g. the department does not exist).</summary>
    Skipped
}

public sealed record RequestTypeImportResult(
    RequestTypeImportPlan Plan,
    RequestTypeImportOutcome Outcome,
    IReadOnlyList<CatalogDecision> Decisions,
    int? RequestTypeId = null);

/// <summary>
/// Imports the request-type catalog into the existing Department → Request
/// Type → Workflow → SLA configuration. Safe to re-run: the catalog's
/// Request Code is stored on the request type (unique) and every row whose
/// code already exists is left untouched, so a second run creates nothing
/// and changes nothing — configuration edits after import are deliberate
/// Administration changes, never overwritten by a re-import (the same rule as
/// <see cref="WorkflowReferenceData.SeedAsync"/>).
///
/// <para>
/// <b>Nothing unresolved goes live.</b> A row with any open
/// <see cref="CatalogDecision"/> is created with its request type and
/// workflow inactive and its workflow version left as an unpublished Draft,
/// so no ticket can use it and an administrator can finish it in the
/// Workflow Designer once the business decides. Unresolved approvals create
/// no approval requirement at all (an approval type or approver would have to
/// be invented). The ticket lifecycle, step kinds, approval types and role
/// permissions are all used as they are.
/// </para>
/// </summary>
public static class RequestTypeCatalogImporter
{
    public const string WorkflowCodePrefix = "RT-";

    public static string WorkflowCodeFor(string requestCode) => WorkflowCodePrefix + requestCode.Trim();

    public static async Task<RequestTypeCatalogImportReport> ImportAsync(
        TigerCsDbContext dbContext,
        IReadOnlyList<RequestTypeCatalogRow> rows,
        RequestTypeCatalogImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);

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
            results.Add(await ImportRowAsync(dbContext, plan, options, cancellationToken));
        }

        if (options.Apply)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return new RequestTypeCatalogImportReport(results, options.Apply, options.ActivateResolved);
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

    private static async Task<RequestTypeImportResult> ImportRowAsync(
        TigerCsDbContext dbContext, RequestTypeImportPlan plan, RequestTypeCatalogImportOptions options, CancellationToken cancellationToken)
    {
        var code = plan.Code;

        // ---- re-run: the code is the identity ---------------------------------
        var byCode = await dbContext.RequestTypes.FirstOrDefaultAsync(r => r.Code == code, cancellationToken);
        if (byCode is not null)
        {
            // Still-open decisions stay on the report for a row that is
            // inactive, or that was linked rather than created (its workflow
            // is not the import's own) — an active row the import created
            // resolved everything, or was finished in Administration since.
            var createdByImport = await dbContext.Workflows.AnyAsync(
                w => w.WorkflowId == byCode.WorkflowId && w.Code == WorkflowCodeFor(code), cancellationToken);
            var stillOpen = !byCode.IsActive || !createdByImport ? plan.Decisions : [];
            return new(plan, RequestTypeImportOutcome.AlreadyImported, stillOpen, byCode.RequestTypeId);
        }

        if (!plan.CanCreate)
        {
            return new(plan, RequestTypeImportOutcome.Skipped, plan.Decisions);
        }

        var department = plan.Department!;
        var name = plan.Source.Name.Trim();

        // ---- a same-named request type already exists in the department ---------
        var byName = await dbContext.RequestTypes.FirstOrDefaultAsync(
            r => r.DepartmentId == department.DepartmentId && r.Name == name, cancellationToken);
        if (byName is not null)
        {
            if (byName.Code is not null)
            {
                return new(plan, RequestTypeImportOutcome.Skipped,
                [
                    .. plan.Decisions,
                    new(CatalogDecisionArea.ExistingRequestType,
                        $"'{name}' in {department.Name} already carries catalog code '{byName.Code}', not '{code}'. Confirm which code is correct.")
                ]);
            }

            if (options.Apply)
            {
                byName.AssignCode(code);
            }

            return new(plan, RequestTypeImportOutcome.LinkedToExisting,
                [.. await DescribeExistingDifferencesAsync(dbContext, byName, plan, cancellationToken), .. plan.Decisions],
                byName.RequestTypeId);
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
        var decisions = plan.Decisions.ToList();
        var validation = BuildVersion(plan, workflowId: 0, options.NowUtc, plan.Decisions.Count).Validate()
            .Where(i => i.Severity == WorkflowValidationSeverity.Error)
            .ToList();
        foreach (var issue in validation)
        {
            decisions.Add(new(CatalogDecisionArea.Workflow, "The mapped workflow is not publishable: " + issue.Message));
        }

        var activate = options.ActivateResolved && decisions.Count == 0;
        var outcome = activate ? RequestTypeImportOutcome.CreatedActive : RequestTypeImportOutcome.CreatedInactiveDraft;

        if (!options.Apply)
        {
            return new(plan, outcome, decisions);
        }

        var workflow = new Workflow(workflowCode, WorkflowName(plan), WorkflowDescription(plan, decisions.Count), options.NowUtc, isActive: activate);
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var version = BuildVersion(plan, workflow.WorkflowId, options.NowUtc, decisions.Count);
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
            // The workbook says nothing about agent priority changes or
            // Pending Customer; both stay off rather than being assumed on.
            allowAgentPriorityChange: false,
            allowPendingCustomer: false,
            allowPendingInternal: false,
            plan.AllowReopen,
            plan.RequiredFieldsJson,
            isActive: activate);
        requestType.SetCatalogDetails(code, plan.Source.RequestGroup, plan.Source.BusinessDescription, plan.RequiredDocumentsJson);
        dbContext.RequestTypes.Add(requestType);
        await dbContext.SaveChangesAsync(cancellationToken);

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

        // The approved business rule (WorkflowReferenceData.ApprovalRequirements):
        // whatever supports Reopen supports asking for one — the same
        // CS Manager, non-blocking requirement every other such type has.
        if (plan.AllowReopen)
        {
            dbContext.RequestTypeApprovalRequirements.Add(RequestTypeApprovalRequirement.ForRole(
                requestType.RequestTypeId,
                ApprovalType.ReopenApproval,
                WorkflowReferenceData.ReopenApprovalApproverRole,
                blocksWorkUntilApproved: WorkflowReferenceData.ReopenApprovalBlocksWork));
        }

        return new(plan, outcome, decisions, requestType.RequestTypeId);
    }

    private static WorkflowTemplate BuildVersion(RequestTypeImportPlan plan, int workflowId, DateTime nowUtc, int openDecisions)
    {
        var version = new WorkflowTemplate(
            workflowId,
            versionNumber: 1,
            WorkflowCodeFor(plan.Code) + "-V1",
            WorkflowName(plan),
            WorkflowDescription(plan, openDecisions),
            allowsPendingCustomer: false,
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

    private static async Task<List<CatalogDecision>> DescribeExistingDifferencesAsync(
        TigerCsDbContext dbContext, RequestType existing, RequestTypeImportPlan plan, CancellationToken cancellationToken)
    {
        var differences = new List<string>();
        if (plan.DefaultPriority is { } priority && existing.DefaultPriorityId != (byte)priority)
        {
            differences.Add($"default priority {(PriorityLevel)existing.DefaultPriorityId} vs catalog {priority}");
        }

        var slas = await dbContext.RequestTypeSlaPolicies.AsNoTracking()
            .Where(p => p.RequestTypeId == existing.RequestTypeId)
            .ToListAsync(cancellationToken);
        if (plan.Sla is { } sla)
        {
            var current = slas.FirstOrDefault(p => p.PriorityId == (byte)sla.Priority);
            var catalogSla = $"first response {Duration(sla.FirstResponseValue, null, sla.FirstResponseUnit ?? sla.ResolutionUnit)}, "
                + $"resolution {Duration(sla.ResolutionValue, null, sla.ResolutionUnit)} (business time)";
            var currentSla = current is null
                ? "none at this priority"
                : $"first response {Duration(current.FirstResponseTargetValue, current.FirstResponseMaximumValue, current.EffectiveFirstResponseUnit)}, "
                  + $"resolution {Duration(current.ResolutionTargetValue, current.ResolutionMaximumValue, current.Unit)}";
            differences.Add($"SLA today {currentSla} vs catalog {catalogSla}");
        }

        if (existing.AllowReopen != plan.AllowReopen)
        {
            differences.Add($"Allow Reopen {existing.AllowReopen} vs catalog {plan.AllowReopen}");
        }

        differences.Add($"its current workflow vs the catalog's '{plan.Source.ProposedWorkflow.Trim()}'");

        return
        [
            new(CatalogDecisionArea.ExistingRequestType,
                $"'{existing.Name}' already exists in {plan.Department!.Name} and is {(existing.IsActive ? "active" : "inactive")}; the import linked it to {plan.Code} "
                + $"and changed nothing else. Confirm whether the catalog should replace its configuration: {string.Join("; ", differences)}.")
        ];
    }

    private static string Duration(int? target, int? maximum, SlaDurationUnit unit) => (target, maximum) switch
    {
        (null, null) => "not set",
        ({ } t, null) => $"{t} {unit.ToString().ToLowerInvariant()}",
        (null, { } m) => $"up to {m} {unit.ToString().ToLowerInvariant()}",
        ({ } t, { } m) => $"{t}–{m} {unit.ToString().ToLowerInvariant()}"
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
