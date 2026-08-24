namespace TigerCS.Web.Services.Auth;

/// <summary>Custom claim types stored in the Web app's own encrypted sign-in cookie (never sent to the browser as JS-readable data).</summary>
public static class TigerCsClaimTypes
{
    /// <summary>The TigerCS.Api access token, carried inside the cookie so it can be replayed as a Bearer credential on outgoing Api calls.</summary>
    public const string AccessToken = "tigercs:access_token";

    public const string PrimaryDepartmentId = "tigercs:primary_department_id";
}
