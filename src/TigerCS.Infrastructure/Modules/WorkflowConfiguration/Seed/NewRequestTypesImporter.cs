using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;
using static TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed.NewRequestTypesBusinessReview;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>What the importer did (or declined to do) for one workbook row.</summary>
public enum NewRequestTypeImportOutcome : byte
{
    /// <summary>Created inactive, with its workflow version 1 Published (every hand-off destination exists).</summary>
    Created = 1,

    /// <summary>Created inactive, with its workflow version 1 left as a <b>Draft</b>: a hand-off destination department does not exist. Administration refuses to activate a request type whose workflow has no Published version, so it cannot go live until the destination is decided.</summary>
    CreatedWithDraftWorkflow = 2,

    /// <summary>This import already created it (its workflow carries the Request Code). Left exactly as it is — administration edits made during UAT are never overwritten.</summary>
    AlreadyImported = 3,

    /// <summary>The owning department does not exist. Nothing created; departments are never created by this import.</summary>
    SkippedOwningDepartmentMissing = 4,

    /// <summary>A request type of the same name already exists in the owning department and was NOT created by this import. Never modified.</summary>
    SkippedExistingRequestType = 5,

    /// <summary>A workflow or workflow version already uses the Request Code as its code but is not this import's request type. Never modified.</summary>
    SkippedCodeInUse = 6
}

/// <param name="RequestCode">The workbook's Request Code.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="RequestTypeId">The request type created or matched, when there is one.</param>
/// <param name="UnresolvedDestinations">Hand-off destinations that did not resolve to an existing department.</param>
/// <param name="SlaConfigured">True when a <see cref="RequestTypeSlaPolicy"/> row exists for it from this import.</param>
public sealed record NewRequestTypeImportRowResult(
    string RequestCode,
    NewRequestTypeImportOutcome Outcome,
    int? RequestTypeId,
    IReadOnlyList<string> UnresolvedDestinations,
    bool SlaConfigured);

public sealed record NewRequestTypeImportResult(IReadOnlyList<NewRequestTypeImportRowResult> Rows)
{
    public int Count(NewRequestTypeImportOutcome outcome) => Rows.Count(r => r.Outcome == outcome);
}

/// <summary>
/// Imports <see cref="NewRequestTypesBusinessReview"/> into a database for
/// UAT review. <c>ImportNewRequestTypes_UAT.sql</c> is the equivalent for an
/// existing UAT database and follows exactly the same rules.
///
/// <list type="bullet">
///   <item><description><b>Idempotent.</b> A row is recognised as already imported by its Request Code (the code of the workflow its request type points at); a second run creates nothing.</description></item>
///   <item><description><b>Additive only.</b> Never updates or deletes an existing row: request types, workflows, departments, approval requirements (ReopenApproval included) and tickets are untouched.</description></item>
///   <item><description><b>Inactive.</b> Every created request type is <c>IsActive = false</c> — nothing reaches ticket intake until an administrator activates an approved row.</description></item>
///   <item><description><b>No guessed configuration.</b> No approval requirement is created (no workbook approval role maps exactly onto the supported approval model); no SLA row is created where the workbook gives no number; no department is created.</description></item>
///   <item><description><b>Transactional</b> on a relational provider: all rows or none.</description></item>
/// </list>
///
/// <para>
/// <b>First Response is not stored.</b> A <see cref="RequestTypeSlaPolicy"/>
/// row has one <see cref="SlaDurationUnit"/> for both of its deadlines; the
/// workbook gives First Response in business hours and Resolution in
/// business days. Resolution is stored exactly (Days, business-hours clock);
/// converting it to hours would bake the current calendar's day length into
/// configuration, so First Response is reported as a pending SLA decision
/// instead.
/// </para>
/// </summary>
public static class NewRequestTypesImporter
{
    public static async Task<NewRequestTypeImportResult> ImportAsync(
        TigerCsDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var departments = await dbContext.Departments.AsNoTracking()
            .Select(d => new { d.DepartmentId, d.Code, d.Name })
            .ToListAsync(cancellationToken);

        int? Resolve(DepartmentRef reference) =>
            (reference.Code is null
                ? null
                : departments.FirstOrDefault(d => string.Equals(d.Code, reference.Code, StringComparison.OrdinalIgnoreCase))?.DepartmentId)
            ?? departments.FirstOrDefault(d => string.Equals(d.Name, reference.Name, StringComparison.OrdinalIgnoreCase))?.DepartmentId;

        var results = new List<NewRequestTypeImportRowResult>();
        foreach (var row in Rows())
        {
            results.Add(await ImportRowAsync(dbContext, row, Resolve, utcNow, cancellationToken));
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new NewRequestTypeImportResult(results);
    }

    private static async Task<NewRequestTypeImportRowResult> ImportRowAsync(
        TigerCsDbContext dbContext, Row row, Func<DepartmentRef, int?> resolve, DateTime utcNow, CancellationToken cancellationToken)
    {
        var unresolved = row.Destinations.Where(d => resolve(d) is null).Select(d => d.Name).ToList();

        NewRequestTypeImportRowResult Result(NewRequestTypeImportOutcome outcome, int? requestTypeId = null, bool sla = false) =>
            new(row.RequestCode, outcome, requestTypeId, unresolved, sla);

        if (resolve(row.OwningDepartment) is not { } departmentId)
        {
            return Result(NewRequestTypeImportOutcome.SkippedOwningDepartmentMissing);
        }

        var workflow = await dbContext.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Code == row.WorkflowCode, cancellationToken);
        if (workflow is not null)
        {
            var imported = await dbContext.RequestTypes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowId == workflow.WorkflowId && r.DepartmentId == departmentId, cancellationToken);
            if (imported is null)
            {
                return Result(NewRequestTypeImportOutcome.SkippedCodeInUse);
            }

            var hasSla = await dbContext.RequestTypeSlaPolicies.AnyAsync(p => p.RequestTypeId == imported.RequestTypeId, cancellationToken);
            return Result(NewRequestTypeImportOutcome.AlreadyImported, imported.RequestTypeId, hasSla);
        }

        if (await dbContext.WorkflowTemplates.AnyAsync(t => t.Code == row.WorkflowCode, cancellationToken))
        {
            return Result(NewRequestTypeImportOutcome.SkippedCodeInUse);
        }

        var existing = await dbContext.RequestTypes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DepartmentId == departmentId && r.Name == row.Name, cancellationToken);
        if (existing is not null)
        {
            return Result(NewRequestTypeImportOutcome.SkippedExistingRequestType, existing.RequestTypeId);
        }

        workflow = new Workflow(row.WorkflowCode, row.WorkflowName, row.WorkflowDescription, utcNow);
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Version 1's code is the workflow code — the same convention the
        // Workflow Designer uses (AdminWorkflowAppService.VersionCode).
        var version = new WorkflowTemplate(
            workflow.WorkflowId, versionNumber: 1, row.WorkflowCode, row.WorkflowName, row.WorkflowDescription,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false,
            utcNow, createdByEmployeeId: null);
        byte sequence = 1;
        foreach (var step in row.Steps)
        {
            version.AddStep(sequence++, step.Name, step.Kind, step.IsOptional);
        }

        // The validated publish path, never PublishAsSeededBaseline: a flow
        // that the Workflow Designer would reject must not go live.
        var publish = unresolved.Count == 0;
        if (publish)
        {
            version.Publish(utcNow, publishedByEmployeeId: null);
        }

        dbContext.WorkflowTemplates.Add(version);

        var requestType = new RequestType(
            departmentId,
            row.Name,
            workflow.WorkflowId,
            (byte)row.Priority,
            allowAgentPriorityChange: false,
            allowPendingCustomer: false,
            allowPendingInternal: false,
            allowReopen: row.AllowReopenFlag,
            requiredFieldsJson: row.RequiredFieldsJson,
            isActive: false);
        dbContext.RequestTypes.Add(requestType);
        await dbContext.SaveChangesAsync(cancellationToken);

        var slaConfigured = false;
        if (row.ResolutionBusinessDays is { } days)
        {
            dbContext.RequestTypeSlaPolicies.Add(new RequestTypeSlaPolicy(
                requestType.RequestTypeId,
                (byte)row.Priority,
                SlaTriggerType.TicketCreated,
                SlaDurationUnit.Days,
                firstResponseTargetValue: null,
                firstResponseMaximumValue: null,
                resolutionTargetValue: days,
                resolutionMaximumValue: null,
                clockBasis: SlaClockBasis.BusinessHours));
            await dbContext.SaveChangesAsync(cancellationToken);
            slaConfigured = true;
        }

        return Result(
            publish ? NewRequestTypeImportOutcome.Created : NewRequestTypeImportOutcome.CreatedWithDraftWorkflow,
            requestType.RequestTypeId,
            slaConfigured);
    }
}
