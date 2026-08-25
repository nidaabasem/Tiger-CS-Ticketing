namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The Department → customer-lookup-source mapping: which of CRM/PACT/
/// Tasleeh <c>CustomerLookupAppService</c> searches for an IntakeRecord
/// raised against a given Department. One row per configured source, so a
/// Department maps to zero, one, or several sources (e.g. "Department D →
/// CRM + Tasleeh" is two rows) — never a hard-coded per-department branch.
/// An IntakeRecord with no Department searches every source instead; see
/// <c>CustomerLookupAppService.SearchAsync</c>.
/// </summary>
public class DepartmentCustomerLookupSource
{
    public int DepartmentCustomerLookupSourceId { get; private set; }
    public int DepartmentId { get; private set; }
    public CustomerLookupSource Source { get; private set; }

    private DepartmentCustomerLookupSource() { }

    public DepartmentCustomerLookupSource(int departmentId, CustomerLookupSource source)
    {
        DepartmentId = departmentId;
        Source = source;
    }
}
