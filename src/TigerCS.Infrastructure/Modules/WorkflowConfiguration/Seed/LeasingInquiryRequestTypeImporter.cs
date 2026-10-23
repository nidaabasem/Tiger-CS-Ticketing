using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>What <see cref="LeasingInquiryRequestTypeImporter"/> did.</summary>
public enum LeasingInquiryImportOutcome : byte
{
    /// <summary>Created active, with its workflow version 1 Published and Normal (Medium) default priority.</summary>
    Created = 1,

    /// <summary>This import already created it (its workflow carries the code). Left exactly as it is.</summary>
    AlreadyImported = 2,

    /// <summary>A request type of that name already exists in LCS (e.g. created from Administration). Reused as-is, never modified.</summary>
    ExistingRequestTypeReused = 3,

    /// <summary>A workflow or workflow version already uses the code but is not this import's request type. Never modified.</summary>
    SkippedCodeInUse = 4,

    /// <summary>The LCS department does not exist. It is never created here (the baseline import owns that); nothing was written.</summary>
    DepartmentMissing = 5
}

/// <param name="Outcome">What happened.</param>
/// <param name="RequestTypeId">The request type created, previously imported or reused, when there is one.</param>
public sealed record LeasingInquiryImportResult(LeasingInquiryImportOutcome Outcome, int? RequestTypeId);

/// <summary>
/// Adds the dedicated Genesys <c>Leasing Inquiry</c> request type to Leasing Customer Services (LCS), additively and idempotently, using
/// the same construction as <see cref="NewRequestTypesImporter"/> (workflow, Published version 1 through the validated publish path,
/// active request type, routing category). It is separate from that importer because the 35-row workbook catalogue must stay identical
/// to its source workbook.
///
/// <list type="bullet">
///   <item><description><b>No duplicates.</b> An LCS request type of the same name is reused as-is; a second run creates nothing.</description></item>
///   <item><description><b>Additive.</b> Never updates or deletes a row, never creates the department.</description></item>
///   <item><description><b>No assignment rule, no SLA row.</b> Employees are never guessed: the ticket waits in the LCS queue until an administrator configures a rule for verified LCS members.</description></item>
/// </list>
/// </summary>
public static class LeasingInquiryRequestTypeImporter
{
    public const string Name = "Leasing Inquiry";
    public const string WorkflowCode = "LCS-INQ-001";
    public const string RequiredFieldsJson = "[\"Customer/Tenant\",\"Property/Unit\"]";

    public static async Task<LeasingInquiryImportResult> ImportAsync(
        TigerCsDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var lcs = NewRequestTypesBusinessReview.LeasingCustomerServices;
        var department = await dbContext.Departments.AsNoTracking()
            .Where(d => d.Code == lcs.Code || d.Name == lcs.Name)
            .OrderBy(d => d.Code == lcs.Code ? 0 : 1)
            .FirstOrDefaultAsync(cancellationToken);
        if (department is null)
        {
            return new LeasingInquiryImportResult(LeasingInquiryImportOutcome.DepartmentMissing, null);
        }

        var departmentId = department.DepartmentId;

        var existing = await dbContext.RequestTypes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DepartmentId == departmentId && r.Name == Name, cancellationToken);
        var workflow = await dbContext.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Code == WorkflowCode, cancellationToken);
        if (workflow is not null)
        {
            var imported = await dbContext.RequestTypes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowId == workflow.WorkflowId && r.DepartmentId == departmentId, cancellationToken);
            return imported is null
                ? new LeasingInquiryImportResult(LeasingInquiryImportOutcome.SkippedCodeInUse, null)
                : new LeasingInquiryImportResult(LeasingInquiryImportOutcome.AlreadyImported, imported.RequestTypeId);
        }

        if (existing is not null)
        {
            return new LeasingInquiryImportResult(LeasingInquiryImportOutcome.ExistingRequestTypeReused, existing.RequestTypeId);
        }

        if (await dbContext.WorkflowTemplates.AnyAsync(t => t.Code == WorkflowCode, cancellationToken))
        {
            return new LeasingInquiryImportResult(LeasingInquiryImportOutcome.SkippedCodeInUse, null);
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var workflowName = $"{WorkflowCode} {Name}";
        const string description = "Genesys Leasing inquiry. Leasing CS Queue → Agent → Review → Resolve → Close. Created for Genesys requestType \"Leasing Inquiry\".";
        workflow = new Workflow(WorkflowCode, workflowName, description, utcNow);
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var version = new WorkflowTemplate(
            workflow.WorkflowId, versionNumber: 1, WorkflowCode, workflowName, description,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false,
            utcNow, createdByEmployeeId: null);
        byte sequence = 1;
        version.AddStep(sequence++, "Ticket Created", WorkflowStepKind.Created, false, null);
        version.AddStep(sequence++, "Leasing CS Queue", WorkflowStepKind.Assigned, false, null);
        version.AddStep(sequence++, "Agent", WorkflowStepKind.Assigned, false, null);
        version.AddStep(sequence++, "Review", WorkflowStepKind.InProgress, false, null);
        version.AddStep(sequence++, "Resolve", WorkflowStepKind.Resolved, false, null);
        version.AddStep(sequence, "Close", WorkflowStepKind.Closed, false, null);
        version.Publish(utcNow, publishedByEmployeeId: null);
        dbContext.WorkflowTemplates.Add(version);

        var requestType = new RequestType(
            departmentId, Name, workflow.WorkflowId, (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: false, allowPendingCustomer: false, allowPendingInternal: false, allowReopen: true,
            requiredFieldsJson: RequiredFieldsJson, isActive: true);
        dbContext.RequestTypes.Add(requestType);

        if (!await dbContext.Categories.AnyAsync(c => c.DepartmentId == departmentId && c.Name == Name, cancellationToken))
        {
            dbContext.Categories.Add(new Category(Name, departmentId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new LeasingInquiryImportResult(LeasingInquiryImportOutcome.Created, requestType.RequestTypeId);
    }
}
