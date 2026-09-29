using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>
/// The 35 request types from the Customer Service workbook
/// (docs/business-review/TigerCS_New_Request_Types_Business_Review.xlsx) —
/// the first approved UAT baseline — translated onto the existing
/// configuration model. See docs/New-Request-Types-UAT-Import.md.
///
/// <para>
/// <b>The workbook is the source of truth.</b> Its rows are read verbatim
/// from the embedded TSV export of that workbook, never restated here; this
/// class adds only what the workbook cannot say in TigerCS terms — the
/// structured workflow steps each "Proposed Workflow" text translates to, and
/// the normalization of its free-text values onto the fixed priority and SLA
/// model. Every normalization throws on a value it does not know, so a
/// re-exported workbook with new wording fails loudly instead of being
/// guessed at.
/// </para>
///
/// <para>
/// <b>Deliberately basic.</b> Non-specific rules ("Conditional" approval,
/// "Based on severity" SLA) are left to the standard behaviour rather than
/// invented, so every row is usable in UAT and refined later from the
/// Administration screens.
/// </para>
/// </summary>
public static class NewRequestTypesBusinessReview
{
    /// <summary>The embedded TSV export of the workbook (header row + one line per request type).</summary>
    public const string SourceResourceName = "TigerCS.NewRequestTypesBusinessReview.tsv";

    public const int ExpectedRowCount = 35;

    /// <summary>
    /// An owning department, resolved against existing Department rows by
    /// <see cref="Code"/> first, then by exact <see cref="Name"/>.
    /// </summary>
    /// <param name="Name">The department's name as the workbook (or the existing seed) spells it.</param>
    /// <param name="Code">The stable code it is resolved by — the existing seed's code where one exists; for a department the importer may create, the code it is created with.</param>
    public sealed record DepartmentRef(string Name, string Code);

    // Owning departments. Codes are the ones the existing seeds already use.
    public static readonly DepartmentRef CustomerService = new("Customer Service", WorkflowReferenceData.CustomerServiceCode);
    public static readonly DepartmentRef Registration = new("Registration", WorkflowReferenceData.RegistrationCode);
    public static readonly DepartmentRef Collections = new("Collections", WorkflowReferenceData.CollectionsCode);
    public static readonly DepartmentRef Handover = new("Handover", WorkflowReferenceData.HandoverCode);
    // Facilities Management and Leasing Customer Services are business
    // departments the workbook owns request types in. "FM" is the code the
    // development seed already uses; no seed defines Leasing Customer
    // Services, so "LCS" (the workbook's own request-code prefix) is the code
    // it is created with when missing.
    public static readonly DepartmentRef FacilitiesManagement = new("Facilities Management", "FM");
    public static readonly DepartmentRef LeasingCustomerServices = new("Leasing Customer Services", "LCS");

    /// <summary>The owning departments the importer creates when missing.</summary>
    public static IReadOnlyList<DepartmentRef> CreatableOwningDepartments { get; } = [FacilitiesManagement, LeasingCustomerServices];

    /// <summary>
    /// Existing request types a proposed row resembles without matching
    /// exactly: reported so the business can decide whether to merge them
    /// later. Neither is ever merged, replaced or modified.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SimilarExistingRequestTypeNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CS-CMP-001"] = "Complaint Handling"
        };

    /// <summary>
    /// How a Proposed Workflow step is classified before it is mapped onto a
    /// <see cref="WorkflowStepKind"/>. <see cref="Start"/> is not in the
    /// workbook: every TigerCS workflow must open with the ticket-created
    /// step, so it is added to each translation.
    /// </summary>
    public enum StepRole : byte
    {
        Start = 0,
        QueueOwnership = 1,
        Assignment = 2,
        TransferHandoff = 3,
        Approval = 4,
        Operational = 5,
        Resolve = 6,
        Close = 7
    }

    /// <summary>One structured workflow step.</summary>
    /// <param name="Name">The step's display name — the workbook's own wording, typos corrected ("Acounting", "CS Agentt").</param>
    /// <param name="Kind">The supported step kind it is represented by.</param>
    /// <param name="Role">Its classification (see <see cref="StepRole"/>).</param>
    /// <param name="IsOptional">True for the workbook's "if needed" steps.</param>
    public sealed record Step(
        string Name,
        WorkflowStepKind Kind,
        StepRole Role,
        bool IsOptional = false);

    /// <summary>One workbook row, verbatim, plus its structured workflow translation.</summary>
    public sealed record Row(
        string RequestCode,
        string Department,
        string RequestGroup,
        string Name,
        string BusinessDescription,
        string ProposedWorkflow,
        string NeedsApproval,
        string ApprovalRole,
        string DefaultPriority,
        string FirstResponseSla,
        string ResolutionSla,
        string RequiredFields,
        string RequiredDocuments,
        string AllowTransfer,
        string AllowReopen,
        string BusinessDecision,
        string BusinessComments,
        IReadOnlyList<Step> Steps)
    {
        public DepartmentRef OwningDepartment => MapOwningDepartment(Department);

        public PriorityLevel Priority => MapPriority(DefaultPriority);

        /// <summary>Business hours to first response — parsed for reconciliation; see <see cref="NewRequestTypesImporter"/> for why it is not stored.</summary>
        public int FirstResponseBusinessHours => ParseFirstResponseHours(FirstResponseSla);

        /// <summary>Business days to resolution ("Same business day" is 1), or null for "Based on severity" — the standard per-priority SLA then applies.</summary>
        public int? ResolutionBusinessDays => ParseResolutionDays(ResolutionSla);

        public bool AllowTransferFlag => ParseYesNo(AllowTransfer, nameof(AllowTransfer));

        public bool AllowReopenFlag => ParseYesNo(AllowReopen, nameof(AllowReopen));

        public bool IsConditionalApproval => NeedsApproval switch
        {
            "Conditional" => true,
            "No" => false,
            _ => throw new FormatException($"{RequestCode}: unknown 'Needs Approval?' value '{NeedsApproval}'.")
        };

        /// <summary>The workbook's Required Fields as a JSON array of its own labels (the provisional <c>RequestType.RequiredFieldsJson</c> representation).</summary>
        public string RequiredFieldsJson => JsonSerializer.Serialize(
            RequiredFields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        /// <summary>An existing request type in the owning department this row may duplicate (see <see cref="SimilarExistingRequestTypeNames"/>), or null.</summary>
        public string? SimilarExistingRequestTypeName => SimilarExistingRequestTypeNames.GetValueOrDefault(RequestCode);

        /// <summary>The logical workflow's code — the Request Code, so the business code stays the stable key that identifies what this import created.</summary>
        public string WorkflowCode => RequestCode;

        public string WorkflowName => $"{RequestCode} {Name}";

        /// <summary>
        /// Carries what the request type itself has no column for — the
        /// Request Group, Business Description, the verbatim Proposed Workflow
        /// text and Required Documents — so none of it is lost in UAT.
        /// </summary>
        public string WorkflowDescription =>
            $"Customer Service UAT baseline. Request Group: {RequestGroup}. {BusinessDescription} "
            + $"Proposed workflow (source text): {ProposedWorkflow} Required documents: {RequiredDocuments}.";
    }

    public static IReadOnlyList<Row> Rows() => LazyRows.Value;

    private static readonly Lazy<IReadOnlyList<Row>> LazyRows = new(Load);

    private static IReadOnlyList<Row> Load()
    {
        using var stream = typeof(NewRequestTypesBusinessReview).Assembly.GetManifestResourceStream(SourceResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{SourceResourceName}' is missing.");
        using var reader = new StreamReader(stream);

        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();
        var header = lines[0].Split('\t');
        if (header.Length != 17 || header[0] != "Request Code" || header[14] != "Allow Reopen?")
        {
            throw new FormatException("The embedded workbook export does not have the expected 17 columns.");
        }

        var steps = StepsByCode();
        var rows = new List<Row>();
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split('\t');
            if (c.Length != 17)
            {
                throw new FormatException($"Workbook row '{c[0]}' has {c.Length} columns, expected 17.");
            }

            if (!steps.TryGetValue(c[0], out var flow))
            {
                throw new InvalidOperationException($"No structured workflow translation exists for request code '{c[0]}'.");
            }

            rows.Add(new Row(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13], c[14], c[15], c[16], flow));
        }

        var extra = steps.Keys.Except(rows.Select(r => r.RequestCode)).ToList();
        if (extra.Count > 0)
        {
            throw new InvalidOperationException($"Workflow translations exist for codes not in the workbook: {string.Join(", ", extra)}.");
        }

        return rows;
    }

    public static DepartmentRef MapOwningDepartment(string department) => department switch
    {
        "Customer Service" => CustomerService,
        "Registration" => Registration,
        "Collections" => Collections,
        "Handover" => Handover,
        "Facilities Management" => FacilitiesManagement,
        "Leasing Customer Services" => LeasingCustomerServices,
        _ => throw new FormatException($"Unknown owning department '{department}'.")
    };

    /// <summary>"Normal" is the existing Medium tier — the documented Normal ↔ Medium mapping (<see cref="WorkflowReferenceData.NormalUrgencyPriority"/>); High and Low are the tiers of the same name.</summary>
    public static PriorityLevel MapPriority(string priority) => priority switch
    {
        "Normal" => WorkflowReferenceData.NormalUrgencyPriority,
        "High" => PriorityLevel.High,
        "Low" => PriorityLevel.Low,
        _ => throw new FormatException($"Unknown default priority '{priority}'.")
    };

    public static int ParseFirstResponseHours(string value)
    {
        var parts = value.Split(' ');
        if (parts.Length == 3 && parts[1] == "business" && parts[2] == "hours"
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours > 0)
        {
            return hours;
        }

        throw new FormatException($"Unknown First Response SLA '{value}'.");
    }

    public static int? ParseResolutionDays(string value)
    {
        switch (value)
        {
            case "Same business day":
                return 1;
            case "Based on severity":
            case "Based on issue severity":
                return null;
        }

        var parts = value.Split(' ');
        if (parts.Length == 3 && parts[1] == "business" && parts[2] is "day" or "days"
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            return days;
        }

        throw new FormatException($"Unknown Resolution SLA '{value}'.");
    }

    private static bool ParseYesNo(string value, string column) => value switch
    {
        "Yes" => true,
        "No" => false,
        _ => throw new FormatException($"Unknown '{column}' value '{value}'.")
    };

    // ---- Structured workflow translations -------------------------------
    //
    // Mapping rules (docs/New-Request-Types-UAT-Import.md §4):
    //   * "<X> Queue"                                   → Assigned  (QueueOwnership)
    //   * "Agent", "CS Agent", "Agent/Technician"       → Assigned  (Assignment)
    //   * a hand-off to another TigerCS department       → Assigned  (TransferHandoff; the move itself is the
    //     agent's existing Transfer action — the engine performs no transfer)
    //   * a hand-off to Admin Sales / Sales / Legal / HR /
    //     Marketing / Responsible Finance                → InProgress (TransferHandoff, manual: the ticket stays
    //     with its owning department)
    //   * "Reception / CS", "CS Intake"                  → Assigned  (QueueOwnership: Customer Service intake —
    //     Reception is not a TigerCS department)
    //   * "Facilities Management if needed" (Handover)  → MaintenanceDependency (TransferHandoff, optional)
    //   * review / coordinate / follow-up / confirm ... → InProgress (Operational)
    //   * "Escalate if needed"                          → InProgress (Operational, optional) — the existing
    //     manual escalation, NOT an approval: no supported ApprovalType fits any workbook approval role.
    //   * Resolve / Close                               → Resolved / Closed
    // No step is invented beyond the mandatory Start step.

    private static Step Start() => new("Ticket Created", WorkflowStepKind.Created, StepRole.Start);

    private static Step Queue(string name) => new(name, WorkflowStepKind.Assigned, StepRole.QueueOwnership);

    private static Step Agent(string name = "Agent") => new(name, WorkflowStepKind.Assigned, StepRole.Assignment);

    private static Step Handoff(string name) => new(name, WorkflowStepKind.Assigned, StepRole.TransferHandoff);

    /// <summary>
    /// A manual hand-off to a team with no TigerCS department. Represented
    /// as owning-department work (<see cref="WorkflowStepKind.InProgress"/>),
    /// never as a queue / assignment step: the ticket stays with its owning
    /// department, and nothing in the workflow engine moves it anywhere.
    /// </summary>
    private static Step ManualHandoff(string destination, bool optional = false) =>
        new($"Manual handoff to {destination}", WorkflowStepKind.InProgress, StepRole.TransferHandoff, optional);

    /// <summary>Reception is not a TigerCS department: "Reception / CS" is Customer Service intake.</summary>
    private static Step CustomerServiceIntake() => Queue("Customer Service intake (Reception / CS)");

    private static Step Work(string name, bool optional = false) =>
        new(name, WorkflowStepKind.InProgress, StepRole.Operational, optional);

    private static Step Resolve(string name = "Resolve") => new(name, WorkflowStepKind.Resolved, StepRole.Resolve);

    private static Step Close() => new("Close", WorkflowStepKind.Closed, StepRole.Close);

    private static IReadOnlyList<Step> Flow(params Step[] middle) => [Start(), .. middle, Close()];

    private static Dictionary<string, IReadOnlyList<Step>> StepsByCode()
    {
        IReadOnlyList<Step> accountingNoc = Flow(
            Queue("CS Queue"), Agent("CS Agent"),
            Handoff("Transfer to Accounting"), Handoff("Return to CS Agent"),
            Resolve());

        return new Dictionary<string, IReadOnlyList<Step>>(StringComparer.Ordinal)
        {
            // CS Queue → Agent → Resolve → Close
            ["CS-GEN-001"] = Flow(Queue("CS Queue"), Agent(), Resolve()),
            ["CS-GEN-002"] = Flow(Queue("CS Queue"), Agent(), Resolve()),
            // CS Queue → Agent → Obtain update if needed → Resolve → Close
            ["CS-GEN-003"] = Flow(Queue("CS Queue"), Agent(), Work("Obtain update if needed", optional: true), Resolve()),
            // CS Queue → Agent → Escalate if needed → Resolve → Close
            ["CS-CMP-001"] = Flow(Queue("CS Queue"), Agent(), Work("Escalate if needed", optional: true), Resolve()),
            // CS Queue → Agent → Record/route → Resolve → Close
            ["CS-CMP-002"] = Flow(Queue("CS Queue"), Agent(), Work("Record / route"), Resolve()),

            // CS Queue → CS Agent → Accounting → CS Agent → Resolve → Close
            ["REG-NOC-001"] = accountingNoc,
            ["REG-NOC-002"] = accountingNoc,
            ["REG-NOC-003"] = accountingNoc,

            // Registration Queue → Agent → Review [DLD/registration status] → Resolve → Close
            ["REG-CON-001"] = Flow(Queue("Registration Queue"), Agent(), Work("Review"), Resolve()),
            ["REG-DLD-001"] = Flow(Queue("Registration Queue"), Agent(), Work("Review DLD / registration status"), Resolve()),

            // Collections Queue → Agent → ... → Resolve → Close
            ["COL-PAY-001"] = Flow(Queue("Collections Queue"), Agent(), Work("Review account"), Resolve()),
            ["COL-PAY-002"] = Flow(Queue("Collections Queue"), Agent(), Work("Follow-up"), Work("Escalate if needed", optional: true), Resolve()),
            ["COL-PAY-003"] = Flow(Queue("Collections Queue"), Agent(), Work("Confirm cheque / collection"), Resolve()),
            ["COL-PAY-004"] = Flow(Queue("Collections Queue"), Agent(), Work("Review"), Resolve()),

            // CS Queue → Agent → Accounting → CS Agent → Handover Agent → Resolve → Close
            ["HO-NOC-001"] = Flow(
                Queue("CS Queue"), Agent(),
                Handoff("Transfer to Accounting"), Handoff("Return to CS Agent"),
                Handoff("Transfer to Handover Agent"),
                Resolve()),

            // Handover Queue → Agent → ... → Resolve → Close
            ["HO-HND-001"] = Flow(Queue("Handover Queue"), Agent(), Work("Coordinate"), Resolve()),
            ["HO-HND-002"] = Flow(Queue("Handover Queue"), Agent(), Work("Confirm available slot"), Resolve()),
            ["HO-HND-003"] = Flow(Queue("Handover Queue"), Agent(), Work("Coordinate requirements"), Resolve()),
            // Handover Queue → Facilities Management if needed → Follow-up → Resolve → Close
            ["HO-HND-004"] = Flow(
                Queue("Handover Queue"),
                new Step("Facilities Management if needed", WorkflowStepKind.MaintenanceDependency, StepRole.TransferHandoff,
                    IsOptional: true),
                Work("Follow-up"), Resolve()),

            // FM Queue → Assign Agent/Technician → In Progress → Resolve → Close
            ["FM-MNT-001"] = Flow(Queue("FM Queue"), Agent("Assign Agent / Technician"), Work("In Progress"), Resolve()),
            ["FM-UTL-001"] = Flow(Queue("FM Queue"), Agent(), Work("Review / coordinate"), Resolve()),
            ["FM-COM-001"] = Flow(Queue("FM Queue"), Agent("Agent / Technician"), Work("In Progress"), Resolve()),
            // FM/Responsible Finance Queue → Agent → Review → Resolve → Close
            // Owning department stays Facilities Management; "Responsible
            // Finance" is a manual hand-off, optional because the workbook's
            // "FM/..." makes it an alternative.
            ["FM-SVC-001"] = Flow(Queue("FM Queue"), ManualHandoff("Responsible Finance", optional: true), Agent(), Work("Review"), Resolve()),

            // Leasing CS Queue → Agent → ... → Resolve → Close
            ["LCS-TEN-001"] = Flow(Queue("Leasing CS Queue"), Agent(), Work("Process / review"), Resolve()),
            ["LCS-EJR-001"] = Flow(Queue("Leasing CS Queue"), Agent(), Work("Process / review"), Resolve()),
            ["LCS-BKG-001"] = Flow(Queue("Leasing CS Queue"), Agent(), Work("Confirm booking / details"), Resolve()),
            ["LCS-MOV-001"] = Flow(Queue("Leasing CS Queue"), Agent(), Work("Coordinate"), Resolve()),
            ["LCS-CHK-001"] = Flow(Queue("Leasing CS Queue"), Agent(), Work("Review"), Resolve()),

            // CS Queue → CS Agent → Admin Sales → Review / Confirm collection → Resolve → Close
            ["BRK-COM-001"] = Flow(Queue("CS Queue"), Agent("CS Agent"), ManualHandoff("Admin Sales"), Work("Review"), Resolve()),
            ["BRK-CHK-001"] = Flow(Queue("CS Queue"), Agent("CS Agent"), ManualHandoff("Admin Sales"), Work("Confirm collection"), Resolve()),

            // CS / Call Center → Sales Handoff → Follow-up → Resolve / Close
            ["SAL-INQ-001"] = Flow(Queue("CS / Call Center Queue"), ManualHandoff("Sales"), Work("Follow-up"), Resolve()),
            // CS Intake → Legal Handoff → Legal Review/Response → CS Resolve → Close
            ["LEG-INQ-001"] = Flow(Queue("Customer Service intake"), ManualHandoff("Legal"), Work("Legal Review / Response"), Resolve("CS Resolve")),
            // Reception / CS → HR | Marketing Handoff → Acknowledge/Resolve → Close
            ["REC-HR-001"] = Flow(CustomerServiceIntake(), ManualHandoff("HR"), Resolve("Acknowledge / Resolve")),
            ["REC-MKT-001"] = Flow(CustomerServiceIntake(), ManualHandoff("Marketing"), Resolve("Acknowledge / Resolve")),
            // Reception / CS → Route to Responsible Department → Resolve → Close
            // (the destination is chosen per ticket with the existing Transfer action)
            ["REC-OTH-001"] = Flow(
                CustomerServiceIntake(),
                new Step("Route to Responsible Department", WorkflowStepKind.Assigned, StepRole.TransferHandoff),
                Resolve())
        };
    }
}
