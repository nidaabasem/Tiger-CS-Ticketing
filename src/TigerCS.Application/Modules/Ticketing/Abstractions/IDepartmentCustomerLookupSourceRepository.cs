using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Reads the Department → customer-lookup-source configuration (<see cref="DepartmentCustomerLookupSource"/>). Read-only — no admin UI ships in this increment (out of scope).</summary>
public interface IDepartmentCustomerLookupSourceRepository
{
    /// <summary>The source(s) configured for a Department, in no particular order. Empty when the Department has none configured — CustomerLookupAppService then searches nothing, never falling back to "all sources" on its own.</summary>
    Task<IReadOnlyCollection<CustomerLookupSource>> GetSourcesForDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default);
}
