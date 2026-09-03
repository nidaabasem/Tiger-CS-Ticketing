using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>
/// The Workflow/SLA Configuration phase-1 reference data: the three reusable
/// workflow templates, the departments the Customer Service SLA document
/// names, their request types, and the per-(request type, priority) SLA
/// rows carrying that document's durations — as configuration, never as
/// hard-coded logic.
///
/// <para>
/// Defined once, here, and consumed by both the development seed and the
/// test host (mirroring <c>SlaReferenceData</c>'s "exactly one place"
/// discipline), so tests can never pass against values that differ from
/// what a deployment seeds.
/// </para>
///
/// <para>
/// <b>Faithfulness rules</b> (docs/Workflow-SLA-Configuration-Phase1.md):
/// ranges are stored as ranges (10–12 Days stays 10/12 Days); "URGENT"
/// variants are priority rows (Urgent ↔ High, Normal ↔ Medium — the
/// documented mapping decision), never separate request types; "Immediately"
/// is the <c>IsImmediate</c> flag; SLAs the document starts at an approval
/// (Send Receipts, Handover) or at prerequisite completion (Register Unit)
/// carry that trigger; First Response targets, pause behavior, and clock
/// basis are left null — explicitly pending business decisions, not
/// defaults smuggled in as data. No maintenance-completion SLA exists in the
/// source, so none is seeded.
/// </para>
/// </summary>
public static class WorkflowReferenceData
{
    // Template codes — stable identifiers seed data and tests key on.
    public const string StandardTemplateCode = "STANDARD";
    public const string WithPendingTemplateCode = "PENDING";
    public const string WithApprovalTemplateCode = "APPROVAL";

    // Department codes. "CS" already exists in every environment; the rest
    // are the SLA document's other operational areas, created if absent.
    public const string CustomerServiceCode = "CS";
    public const string CollectionsCode = "COL";
    public const string RegistrationCode = "REG";
    public const string HandoverCode = "HO";
    public const string CallCenterCode = "CC";

    /// <summary>Named by the SLA document only as the approver Collections' Send Receipts depends on — seeded so the phase-3 approval flow has a real department to route to.</summary>
    public const string AccountingCode = "ACC";

    /// <summary>
    /// The documented urgency mapping (provisional until business approval):
    /// the SLA document's "Normal" is the existing Medium tier, its
    /// "URGENT" the existing High tier. Critical stays reserved for genuine
    /// emergencies above "Urgent"; Low keeps its existing meaning. No second
    /// priority model is introduced.
    /// </summary>
    public const PriorityLevel NormalUrgencyPriority = PriorityLevel.Medium;

    /// <summary>See <see cref="NormalUrgencyPriority"/>.</summary>
    public const PriorityLevel UrgentUrgencyPriority = PriorityLevel.High;

    /// <summary>One SLA row of a request type: priority tier + trigger + the source document's duration in its own unit.</summary>
    public sealed record SlaSeed(
        PriorityLevel Priority,
        SlaTriggerType Trigger,
        SlaDurationUnit Unit,
        int? ResolutionTarget,
        int? ResolutionMaximum,
        bool IsImmediate = false);

    /// <summary>One request type: which department it belongs to, which reusable template it uses, and its business flags.</summary>
    public sealed record RequestTypeSeed(
        string DepartmentCode,
        string Name,
        string TemplateCode,
        PriorityLevel DefaultPriority,
        bool AllowAgentPriorityChange,
        bool AllowPendingCustomer,
        bool AllowPendingInternal,
        bool AllowReopen,
        IReadOnlyList<SlaSeed> SlaPolicies);

    /// <summary>Departments the SLA document names, beyond the already-seeded Customer Service. Never removed once referenced — same rule as every Department.</summary>
    public static IReadOnlyList<(string Name, string Code)> AdditionalDepartments() =>
    [
        ("Collections", CollectionsCode),
        ("Registration", RegistrationCode),
        ("Handover", HandoverCode),
        ("Call Center", CallCenterCode),
        ("Accounting", AccountingCode)
    ];

    /// <summary>
    /// The three reusable workflow patterns. Steps are display/configuration
    /// data over the existing lifecycle — see <c>WorkflowStepKind</c>'s
    /// remarks; nothing here adds a <c>TicketStatus</c>.
    /// </summary>
    public static IReadOnlyList<WorkflowTemplate> Templates()
    {
        var standard = new WorkflowTemplate(
            StandardTemplateCode, "Standard Request",
            "Straightforward requests with no pending wait and no approval step.",
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false);
        standard.AddStep(1, "Ticket Created", WorkflowStepKind.Created);
        standard.AddStep(2, "Assigned", WorkflowStepKind.Assigned);
        standard.AddStep(3, "In Progress", WorkflowStepKind.InProgress);
        standard.AddStep(4, "Resolved", WorkflowStepKind.Resolved);
        standard.AddStep(5, "Closed", WorkflowStepKind.Closed);

        var withPending = new WorkflowTemplate(
            WithPendingTemplateCode, "Request With Pending",
            "Requests that may wait on the customer (payment, documents, response) or on an internal department / external party.",
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: false);
        withPending.AddStep(1, "Ticket Created", WorkflowStepKind.Created);
        withPending.AddStep(2, "Assigned", WorkflowStepKind.Assigned);
        withPending.AddStep(3, "In Progress", WorkflowStepKind.InProgress);
        withPending.AddStep(4, "Pending Customer", WorkflowStepKind.PendingCustomer, isOptional: true);
        withPending.AddStep(5, "Pending Internal / Third Party", WorkflowStepKind.PendingInternal, isOptional: true);
        withPending.AddStep(6, "Resolved", WorkflowStepKind.Resolved);
        withPending.AddStep(7, "Closed", WorkflowStepKind.Closed);

        var withApproval = new WorkflowTemplate(
            WithApprovalTemplateCode, "Request With Approval",
            "Requests carrying an approval stage (e.g. Accounting approval for Send Receipts, Customer Service approval for Handover) before work proceeds.",
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: true);
        withApproval.AddStep(1, "Ticket Created", WorkflowStepKind.Created);
        withApproval.AddStep(2, "Assigned", WorkflowStepKind.Assigned);
        withApproval.AddStep(3, "Review", WorkflowStepKind.Review);
        withApproval.AddStep(4, "Waiting for Approval", WorkflowStepKind.WaitingForApproval);
        withApproval.AddStep(5, "In Progress", WorkflowStepKind.InProgress);
        withApproval.AddStep(6, "Pending Internal / Third Party", WorkflowStepKind.PendingInternal, isOptional: true);
        withApproval.AddStep(7, "Resolved", WorkflowStepKind.Resolved);
        withApproval.AddStep(8, "Closed", WorkflowStepKind.Closed);

        return [standard, withPending, withApproval];
    }

    /// <summary>
    /// The request types of the Customer Service SLA document (its §Customer
    /// Service/Collections/Registration/Handover areas), with that document's
    /// durations. Call Center is seeded as a department only: the document
    /// gives it operational rules (IVR, 1-minute hold) but the approved
    /// request-type list names none for it, and none is invented.
    /// </summary>
    public static IReadOnlyList<RequestTypeSeed> RequestTypes()
    {
        const PriorityLevel normal = NormalUrgencyPriority;
        const PriorityLevel urgent = UrgentUrgencyPriority;

        return
        [
            // ---- Customer Service ----
            new(CustomerServiceCode, "NOC for Resale", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: true, AllowPendingCustomer: true, AllowPendingInternal: false, AllowReopen: true,
                [
                    new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 10, 12),
                    new(urgent, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 2, 4)
                ]),

            new(CustomerServiceCode, "NOC for Handover", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: true, AllowPendingInternal: false, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2)]),

            new(CustomerServiceCode, "NOC for Mortgage", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: true, AllowPendingCustomer: true, AllowPendingInternal: false, AllowReopen: true,
                [
                    new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 10, 12),
                    new(urgent, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 2, 4)
                ]),

            new(CustomerServiceCode, "NOC for Golden Visa", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: true, AllowPendingInternal: false, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2)]),

            new(CustomerServiceCode, "Complaint Handling", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: true, AllowPendingInternal: true, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 3)]),

            new(CustomerServiceCode, "Ticketing System", StandardTemplateCode, normal,
                AllowAgentPriorityChange: true, AllowPendingCustomer: false, AllowPendingInternal: false, AllowReopen: true,
                [
                    new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, null),
                    new(urgent, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, IsImmediate: true)
                ]),

            new(CustomerServiceCode, "E-mail", StandardTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: false, AllowPendingInternal: false, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2)]),

            // ---- Collections ----
            new(CollectionsCode, "E-mail", StandardTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: false, AllowPendingInternal: false, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2)]),

            new(CollectionsCode, "Ticketing System", StandardTemplateCode, normal,
                AllowAgentPriorityChange: true, AllowPendingCustomer: false, AllowPendingInternal: false, AllowReopen: true,
                [
                    new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, null),
                    new(urgent, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, IsImmediate: true)
                ]),

            // The documented one-day SLA starts AFTER Accounting approval —
            // the trigger carries that; the waiting period is never counted
            // against the post-approval day unless configuration later says
            // so explicitly.
            new(CollectionsCode, "Send Receipts", WithApprovalTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: false, AllowPendingInternal: true, AllowReopen: true,
                [new(normal, SlaTriggerType.ApprovalReceived, SlaDurationUnit.Days, 1, null)]),

            // ---- Registration ----
            new(RegistrationCode, "Send SPA Link", StandardTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: false, AllowPendingInternal: false, AllowReopen: true,
                [new(normal, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, 2)]),

            // "1–3 days when everything is OK" — the clock is conditional on
            // prerequisites; "if something is wrong: duration depends on the
            // issue" is deliberately NOT seeded as an SLA row.
            new(RegistrationCode, "Register Unit", WithPendingTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: true, AllowPendingInternal: true, AllowReopen: true,
                [new(normal, SlaTriggerType.PrerequisitesCompleted, SlaDurationUnit.Days, 1, 3)]),

            // ---- Handover ----
            // The 1–4 day approval duration begins after Customer Service
            // approval, never at creation; the optional maintenance
            // dependency is Pending Internal, and no maintenance-completion
            // SLA exists in the source so none is seeded.
            new(HandoverCode, "Handover Request", WithApprovalTemplateCode, normal,
                AllowAgentPriorityChange: false, AllowPendingCustomer: true, AllowPendingInternal: true, AllowReopen: true,
                [new(normal, SlaTriggerType.CustomerServiceApproved, SlaDurationUnit.Days, 1, 4)])
        ];
    }

    /// <summary>
    /// Department codes that get a <see cref="DepartmentWorkflowSettings"/>
    /// row — the SLA document's participating areas. All flags default to
    /// allowed and the head role to Department Head: provisional defaults
    /// awaiting per-department business decisions, recorded as configuration
    /// so changing them is a data edit, not code.
    /// </summary>
    public static IReadOnlyList<string> ParticipatingDepartmentCodes() =>
        [CustomerServiceCode, CollectionsCode, RegistrationCode, HandoverCode, CallCenterCode, AccountingCode];

    /// <summary>
    /// Seeds templates, departments, request types, SLA rows, and department
    /// workflow settings. Idempotent per table/row and safe on every startup;
    /// existing rows are never modified (configuration changes are
    /// deliberate data edits, not re-seeds).
    /// </summary>
    public static async Task SeedAsync(TigerCsDbContext dbContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Departments first — request types and settings reference them.
        foreach (var (name, code) in AdditionalDepartments())
        {
            if (!await dbContext.Departments.AnyAsync(d => d.Code == code, cancellationToken))
            {
                dbContext.Departments.Add(new Department(name, code));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!await dbContext.WorkflowTemplates.AnyAsync(cancellationToken))
        {
            dbContext.WorkflowTemplates.AddRange(Templates());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var departmentIdsByCode = await dbContext.Departments
            .ToDictionaryAsync(d => d.Code, d => d.DepartmentId, cancellationToken);
        var templateIdsByCode = await dbContext.WorkflowTemplates
            .ToDictionaryAsync(t => t.Code, t => t.WorkflowTemplateId, cancellationToken);

        if (!await dbContext.RequestTypes.AnyAsync(cancellationToken))
        {
            foreach (var seed in RequestTypes())
            {
                var requestType = new RequestType(
                    departmentIdsByCode[seed.DepartmentCode],
                    seed.Name,
                    templateIdsByCode[seed.TemplateCode],
                    (byte)seed.DefaultPriority,
                    seed.AllowAgentPriorityChange,
                    seed.AllowPendingCustomer,
                    seed.AllowPendingInternal,
                    seed.AllowReopen);
                dbContext.RequestTypes.Add(requestType);

                // Saved per request type so the generated key exists for its
                // SLA rows — same pattern as the business-calendar seed.
                await dbContext.SaveChangesAsync(cancellationToken);

                foreach (var sla in seed.SlaPolicies)
                {
                    dbContext.RequestTypeSlaPolicies.Add(new RequestTypeSlaPolicy(
                        requestType.RequestTypeId,
                        (byte)sla.Priority,
                        sla.Trigger,
                        sla.Unit,
                        firstResponseTargetValue: null,
                        firstResponseMaximumValue: null,
                        sla.ResolutionTarget,
                        sla.ResolutionMaximum,
                        sla.IsImmediate));
                }
            }
        }

        if (!await dbContext.DepartmentWorkflowSettings.AnyAsync(cancellationToken))
        {
            foreach (var code in ParticipatingDepartmentCodes())
            {
                dbContext.DepartmentWorkflowSettings.Add(new DepartmentWorkflowSettings(
                    departmentIdsByCode[code],
                    allowAssignment: true,
                    allowInternalReassignment: true,
                    allowTransferToOtherDepartments: true));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
