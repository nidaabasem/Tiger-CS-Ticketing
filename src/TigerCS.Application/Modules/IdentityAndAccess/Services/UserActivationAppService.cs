using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.6 PATCH /api/users/{employeeId}/activation.</summary>
public sealed class UserActivationAppService(
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<ActivationResult> SetActivationAsync(
        Guid employeeId, ActivationRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return ActivationResult.NotFound();
        }

        if (!request.IsActive && employee.IsActive)
        {
            var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
            if (roles.Contains(Roles.SystemAdministrator))
            {
                var activeAdmins = await employeeRepository.CountActiveInRoleAsync(Roles.SystemAdministrator, cancellationToken);
                if (activeAdmins <= 1)
                {
                    return ActivationResult.LastActiveAdministrator();
                }
            }

            employee.Deactivate(timeProvider.GetUtcNow().UtcDateTime);
        }
        else if (request.IsActive && !employee.IsActive)
        {
            employee.Reactivate();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // AffectedOpenTicketCount is always 0 at this increment: the Ticketing
        // module (and its Tickets.CurrentOwnerEmployeeId FK) does not exist yet.
        // MVP-API-Contracts.md §1.6 requires this field on the response; it
        // becomes meaningful once Ticketing ships.
        return ActivationResult.Success(new ActivationResponseDto(
            employee.EmployeeId, employee.DisplayName, employee.IsActive, employee.DeactivatedAtUtc, 0));
    }
}
