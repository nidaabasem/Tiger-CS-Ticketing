using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public interface IDepartmentRepository
{
    Task<Department?> GetByIdAsync(int departmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Department>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);

    Task AddAsync(Department department, CancellationToken cancellationToken = default);

    Task<bool> NameExistsAsync(string name, int? excludeDepartmentId, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, int? excludeDepartmentId, CancellationToken cancellationToken = default);

    /// <summary>How many tickets reference the department as current or originating department — the reference count that makes it undeletable.</summary>
    Task<int> CountTicketReferencesAsync(int departmentId, CancellationToken cancellationToken = default);
}
