using System.Text.Json;

namespace TigerCS.Web.Services.Api;

/// <summary>TigerCS.Api serializes with ASP.NET Core's web defaults (camelCase). Every client uses this so wire shapes match exactly.</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
