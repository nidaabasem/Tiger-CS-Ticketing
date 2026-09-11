using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Resolves a Genesys agent to the Ticketing user it corresponds to — the
/// one place the "Genesys User ID → <c>AspNetUsers.GenesysUserId</c> →
/// Ticketing user → id / roles / departments" chain is walked.
///
/// <para>
/// <b>The immutable Genesys User ID is the only key.</b> Display name, agent
/// name and email are never consulted: names are not unique and change, and
/// an email match alone must never let a request act as a Ticketing user.
/// <c>GenesysEmail</c> on the user is informational, and that is all.
/// </para>
///
/// <para>
/// <b>Nothing is provisioned.</b> An unknown Genesys agent answers
/// <see cref="GenesysAgentResolutionOutcome.NotMapped"/> — a controlled,
/// distinct outcome, not "user not found" and not a silently created
/// account. Mapping is an administrative act performed on the Ticketing
/// user beforehand.
/// </para>
///
/// <para>
/// <b>Active under the existing rule.</b> A mapped user must still be a
/// valid, active employee — the same <c>ActiveEmployeeRequirement</c> every
/// authenticated request already passes — so a deactivated agent cannot be
/// recorded as handling anything (<see cref="GenesysAgentResolutionOutcome.Inactive"/>).
/// </para>
/// </summary>
public sealed class GenesysAgentResolutionAppService(
    IGenesysAgentMappingRepository mappingRepository,
    IUserRoleReader userRoleReader,
    IUserDepartmentAssignmentRepository departmentAssignmentRepository)
{
    public const int GenesysUserIdMaxLength = 64;

    public async Task<GenesysAgentResolutionResult> ResolveByGenesysUserIdAsync(
        string? genesysUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(genesysUserId))
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Invalid, "genesysUserId is required.");
        }

        var value = genesysUserId.Trim();
        if (value.Length > GenesysUserIdMaxLength)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Invalid, $"genesysUserId must be at most {GenesysUserIdMaxLength} characters.");
        }

        var mapped = await mappingRepository.FindByGenesysUserIdAsync(value, cancellationToken);
        if (mapped is null)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.NotMapped, "Genesys agent is not mapped to a Ticketing user.");
        }

        if (!mapped.IsActive)
        {
            return GenesysAgentResolutionResult.Failure(
                GenesysAgentResolutionOutcome.Inactive, "The Ticketing user mapped to this Genesys agent is deactivated.");
        }

        var roles = await userRoleReader.GetRolesAsync(mapped.UserId, cancellationToken);
        var departments = await departmentAssignmentRepository.GetByEmployeeIdAsync(mapped.UserId, cancellationToken);

        return new GenesysAgentResolutionResult(
            GenesysAgentResolutionOutcome.Success,
            mapped,
            roles,
            departments.Select(a => a.DepartmentId).Distinct().ToList());
    }
}
