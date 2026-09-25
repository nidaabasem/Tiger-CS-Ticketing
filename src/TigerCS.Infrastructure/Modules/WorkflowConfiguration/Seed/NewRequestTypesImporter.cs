using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;
using static TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed.NewRequestTypesBusinessReview;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>What the importer did (or declined to do) for one workbook row.</summary>
public enum NewRequestTypeImportOutcome : byte
{
    /// <summary>Created inactive, with its workflow version 1 Published (no unresolved hand-off dependency).</summary>
    Created = 1,

    /// <summary>Created inactive, with its workflow version 1 left as a <b>Draft</b>: a hand-off dependency is unresolved (a missing department, or a destination whose representation is still a business decision). Administration refuses to activate a request type whose workflow has no Published version, so it cannot go live until the dependency is decided.</summary>
    CreatedWithDraftWorkflow = 2,

    /// <summary>This import already created it (its workflow carries the Request Code). Left exactly as it is — administration edits made during UAT are never overwritten.</summary>
    AlreadyImported = 3,

    /// <summary>The owning department is genuinely missing and was not created (creation not requested). Nothing created for the row.</summary>
    SkippedOwningDepartmentMissing = 4,

    /// <summary>A request type of the same name already exists in the owning department and was NOT created by this import. Never modified, merged or replaced.</summary>
    SkippedExistingRequestType = 5,

    /// <summary>A workflow or workflow version already uses the Request Code as its code but is not this import's request type. Never modified.</summary>
    SkippedCodeInUse = 6,

    /// <summary>The owning department was not found by code or exact name, but a department with a SIMILAR name exists — it may be the same department under another name/code, so none was created (no duplicates). Map it explicitly, then re-run.</summary>
    SkippedOwningDepartmentNearMatch = 7
}

/// <param name="RequestCode">The workbook's Request Code.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="RequestTypeId">The request type created or matched (for an existing-name conflict: the EXISTING one), when there is one.</param>
/// <param name="UnresolvedDestinations">Hand-off dependencies that did not resolve — the reason a workflow is (or would be) Draft.</param>
/// <param name="SlaConfigured">True when a <see cref="RequestTypeSlaPolicy"/> row exists for it from this import.</param>
/// <param name="ExistingNameConflict">The existing request type in the owning department this row duplicates — exactly (not imported) or by a similar name (imported inactive alongside it) — or null.</param>
/// <param name="ApprovalDecisionRequired">The workbook marks the row Conditional Approval; no approval requirement is configured (mapping unresolved).</param>
/// <param name="ResolutionSlaDecisionRequired">The workbook's Resolution SLA has no number; no SLA row exists. (First Response is a decision for EVERY row — see <see cref="NewRequestTypesImporter"/>.)</param>
public sealed record NewRequestTypeImportRowResult(
    string RequestCode,
    NewRequestTypeImportOutcome Outcome,
    int? RequestTypeId,
    IReadOnlyList<string> UnresolvedDestinations,
    bool SlaConfigured,
    string? ExistingNameConflict,
    bool ApprovalDecisionRequired,
    bool ResolutionSlaDecisionRequired)
{
    /// <summary>A request type from this import exists for the row (created now or earlier), inactive unless an administrator activated it.</summary>
    public bool Imported => Outcome is NewRequestTypeImportOutcome.Created or NewRequestTypeImportOutcome.CreatedWithDraftWorkflow or NewRequestTypeImportOutcome.AlreadyImported;

    public bool BlockedByMissingOwningDepartment =>
        Outcome is NewRequestTypeImportOutcome.SkippedOwningDepartmentMissing or NewRequestTypeImportOutcome.SkippedOwningDepartmentNearMatch;

    /// <summary>The workflow is (or, once imported, would be) left Draft because of an unresolved hand-off dependency.</summary>
    public bool DraftBecauseOfUnresolvedDependency => UnresolvedDestinations.Count > 0;
}

/// <summary>What happened to an owning department the importer is allowed to create (Facilities Management, Leasing Customer Services).</summary>
public enum OwningDepartmentResolution : byte
{
    /// <summary>Found by code or exact name — the existing row is used; nothing created.</summary>
    Existing = 1,

    /// <summary>Genuinely missing and created (creation was requested).</summary>
    Created = 2,

    /// <summary>Genuinely missing; creation not requested — its rows are blocked and reported.</summary>
    Missing = 3,

    /// <summary>Not found, but a similarly named department exists — never created (no duplicates); its rows are blocked.</summary>
    NearMatch = 4
}

/// <param name="Name">The department's name.</param>
/// <param name="Code">The code it is resolved by / created with.</param>
/// <param name="Resolution">What happened.</param>
/// <param name="NearMatches">Existing department names that look similar (reported, never used implicitly).</param>
public sealed record OwningDepartmentReport(string Name, string Code, OwningDepartmentResolution Resolution, IReadOnlyList<string> NearMatches);

public sealed record NewRequestTypeImportResult(
    IReadOnlyList<NewRequestTypeImportRowResult> Rows,
    IReadOnlyList<OwningDepartmentReport> OwningDepartments)
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
///   <item><description><b>Additive only.</b> Never updates or deletes an existing row: request types (including same- or similarly-named ones), workflows, departments, approval requirements (ReopenApproval included) and tickets are untouched.</description></item>
///   <item><description><b>Inactive.</b> Every created request type is <c>IsActive = false</c> — nothing reaches ticket intake until an administrator activates an approved row.</description></item>
///   <item><description><b>No guessed configuration.</b> No approval requirement is created (the 10 Conditional rows need business mapping); no SLA row is created where the workbook gives no number; no hand-off destination department is created.</description></item>
///   <item><description><b>Departments.</b> Owning departments are resolved by code, then exact name. Only Facilities Management and Leasing Customer Services — confirmed business departments — may be created, only when <c>createMissingOwningDepartments</c> is true, and never when a similarly named department exists.</description></item>
///   <item><description><b>Transactional</b> on a relational provider: all rows or none.</description></item>
/// </list>
///
/// <para>
/// <b>First Response is not stored.</b> A <see cref="RequestTypeSlaPolicy"/>
/// row has one <see cref="SlaDurationUnit"/> for both of its deadlines; the
/// workbook gives First Response in business hours and Resolution in
/// business days. Resolution is stored exactly (Days, business-hours clock);
/// converting it to hours would bake the current calendar's day length into
/// configuration, so First Response is reported as a pending decision.
/// </para>
/// </summary>
public static class NewRequestTypesImporter
{
    public static async Task<NewRequestTypeImportResult> ImportAsync(
        TigerCsDbContext dbContext, DateTime utcNow, bool createMissingOwningDepartments = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var departments = await LoadDepartmentsAsync(dbContext, cancellationToken);

        // Owning departments first, so every row (and every hand-off to them)
        // sees the same resolved set regardless of workbook order.
        var departmentReports = new List<OwningDepartmentReport>();
        foreach (var reference in CreatableOwningDepartments)
        {
            var code = reference.Code!;
            if (Find(departments, reference) is not null)
            {
                departmentReports.Add(new(reference.Name, code, OwningDepartmentResolution.Existing, []));
                continue;
            }

            var nearMatches = departments
                .Where(d => reference.NearMatchKeyword is { } keyword && d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Select(d => $"{d.Name} ({d.Code})")
                .ToList();
            if (nearMatches.Count > 0)
            {
                departmentReports.Add(new(reference.Name, code, OwningDepartmentResolution.NearMatch, nearMatches));
                continue;
            }

            if (!createMissingOwningDepartments)
            {
                departmentReports.Add(new(reference.Name, code, OwningDepartmentResolution.Missing, []));
                continue;
            }

            dbContext.Departments.Add(new Department(reference.Name, code));
            await dbContext.SaveChangesAsync(cancellationToken);
            departmentReports.Add(new(reference.Name, code, OwningDepartmentResolution.Created, []));
        }

        departments = await LoadDepartmentsAsync(dbContext, cancellationToken);
        var nearMatchNames = departmentReports
            .Where(r => r.Resolution == OwningDepartmentResolution.NearMatch)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        int? Resolve(DepartmentRef reference) =>
            reference.RepresentationPending ? null : Find(departments, reference)?.DepartmentId;

        var results = new List<NewRequestTypeImportRowResult>();
        foreach (var row in Rows())
        {
            results.Add(await ImportRowAsync(dbContext, row, Resolve, nearMatchNames.Contains(row.OwningDepartment.Name), utcNow, cancellationToken));
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new NewRequestTypeImportResult(results, departmentReports);
    }

    private sealed record DepartmentKey(int DepartmentId, string Code, string Name);

    private static async Task<List<DepartmentKey>> LoadDepartmentsAsync(TigerCsDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.Departments.AsNoTracking()
            .Select(d => new DepartmentKey(d.DepartmentId, d.Code, d.Name))
            .ToListAsync(cancellationToken);

    /// <summary>By code first (unique), then by exact name (unique) — never by similarity.</summary>
    private static DepartmentKey? Find(IReadOnlyList<DepartmentKey> departments, DepartmentRef reference) =>
        (reference.Code is null
            ? null
            : departments.FirstOrDefault(d => string.Equals(d.Code, reference.Code, StringComparison.OrdinalIgnoreCase)))
        ?? departments.FirstOrDefault(d => string.Equals(d.Name, reference.Name, StringComparison.OrdinalIgnoreCase));

    private static async Task<NewRequestTypeImportRowResult> ImportRowAsync(
        TigerCsDbContext dbContext, Row row, Func<DepartmentRef, int?> resolve, bool owningDepartmentNearMatch,
        DateTime utcNow, CancellationToken cancellationToken)
    {
        var unresolved = row.Destinations.Where(d => resolve(d) is null).Select(d => d.Name).ToList();
        string? conflict = null;

        NewRequestTypeImportRowResult Result(NewRequestTypeImportOutcome outcome, int? requestTypeId = null, bool sla = false) =>
            new(row.RequestCode, outcome, requestTypeId, unresolved, sla, conflict, row.IsConditionalApproval, row.ResolutionSlaDecisionRequired);

        if (resolve(row.OwningDepartment) is not { } departmentId)
        {
            return Result(owningDepartmentNearMatch
                ? NewRequestTypeImportOutcome.SkippedOwningDepartmentNearMatch
                : NewRequestTypeImportOutcome.SkippedOwningDepartmentMissing);
        }

        // Similar-name conflicts the business asked to see (UAT decision 8):
        // reported on the row, the existing request type never touched.
        if (row.SimilarExistingRequestTypeName is { } similar
            && await dbContext.RequestTypes.AnyAsync(r => r.DepartmentId == departmentId && r.Name == similar, cancellationToken))
        {
            conflict = similar;
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
            conflict = existing.Name;
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
