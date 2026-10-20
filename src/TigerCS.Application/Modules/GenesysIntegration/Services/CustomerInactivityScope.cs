using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>Who the Website/Chatbot "customer inactive" rule applies to: never a Call Center ticket (by the department it originated in or sits in now).</summary>
public static class CustomerInactivityScope
{
    public static async Task<bool> IsCallCenterTicketAsync(Ticket ticket, IDepartmentRepository departmentRepository, CancellationToken cancellationToken)
    {
        foreach (var departmentId in new[] { ticket.OriginatingDepartmentId, ticket.CurrentDepartmentId }.Distinct())
        {
            var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
            if (department is not null
                && string.Equals(department.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
