using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Authorization;

/// <summary>
/// Builds the current <see cref="UserContext"/> from the authentication
/// cookie's claims.
///
/// <para>
/// Cheap enough to call per request: it reads claims already materialised on
/// the principal and makes no API call. That matters because the shell renders
/// on every page.
/// </para>
/// </summary>
public sealed class UserContextAccessor(AccessTokenAccessor accessTokenAccessor)
{
    public UserContext Current
    {
        get
        {
            var employeeId = accessTokenAccessor.GetEmployeeId();
            if (employeeId is null)
            {
                return UserContext.Anonymous;
            }

            return new UserContext(
                employeeId,
                accessTokenAccessor.GetDisplayName(),
                accessTokenAccessor.GetRoles(),
                accessTokenAccessor.GetDepartments());
        }
    }
}
