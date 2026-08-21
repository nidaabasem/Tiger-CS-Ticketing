namespace TigerCS.Api.OpenApi;

/// <summary>
/// The canonical Swagger/OpenAPI tag names for this API, plus the
/// description shown next to each group in Swagger UI.
///
/// <para>
/// Tags are a <b>documentation grouping only</b>: they change nothing about
/// routing, authorization, or behaviour. They are declared here (rather than
/// as loose strings on each action) so that a tag is spelled exactly one way
/// across every controller, and so <see cref="Ordered"/> can fix the order
/// the groups appear in — Swagger UI otherwise sorts them alphabetically,
/// which would interleave the intake/ticket flow with unrelated groups.
/// </para>
///
/// <para>
/// Descriptions restate what the endpoints in the group already do; they
/// deliberately do not introduce business rules that are not already
/// implemented in the code they describe.
/// </para>
/// </summary>
public static class OpenApiTags
{
    public const string Health = "Health";
    public const string Authentication = "Authentication";
    public const string Users = "Users";
    public const string Roles = "Roles";
    public const string Departments = "Departments";
    public const string CrmLookup = "CRM Lookup";
    public const string CustomerVerification = "Customer Verification";
    public const string Intake = "Intake";
    public const string Tickets = "Tickets";
    public const string Assignment = "Assignment";
    public const string Transfer = "Transfer";
    public const string TicketLifecycle = "Ticket Lifecycle";
    public const string Notes = "Notes";
    public const string CrmReconciliation = "CRM Reconciliation";

    /// <summary>
    /// Every tag, in the order Swagger UI should render them — roughly the
    /// order an agent moves through the system: sign in, look the caller up,
    /// verify them, record the intake, create the ticket, then work it.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Ordered =
    [
        (Health, "Unauthenticated liveness probe."),
        (Authentication, "Sign in and obtain a JWT access token, and sign out."),
        (Users, "The signed-in user's own profile, and administrator-only user activation."),
        (Roles, "The fixed role catalogue used across the system."),
        (Departments, "Department membership listings."),
        (CrmLookup, "Read-only lookups against Tiger CRM: units and the contacts linked to a unit."),
        (CustomerVerification, "Verification sessions — recording that a requester was confirmed against a CRM unit and contact."),
        (Intake, "Intake records — every customer interaction is captured before verification is attempted."),
        (Tickets, "Ticket creation and ticket queries (queue and detail)."),
        (Assignment, "Assigning a ticket to an employee."),
        (Transfer, "Transferring a ticket to another department."),
        (TicketLifecycle, "Status changes, resolution, and closing."),
        (Notes, "Ticket notes."),
        (CrmReconciliation, "Reconciling a provisional ticket against a confirmed verification session.")
    ];
}
