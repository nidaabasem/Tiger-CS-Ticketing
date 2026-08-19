using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public interface IDepartmentRepository
{
    Task<Department?> GetByIdAsync(int departmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Department>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);
}
