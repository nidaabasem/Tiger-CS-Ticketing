namespace TigerCS.Web.Services.Api;

/// <summary>Where TigerCS.Api lives. Bound from the "TigerCsApi" configuration section.</summary>
public sealed class TigerCsApiOptions
{
    public const string SectionName = "TigerCsApi";

    public string BaseUrl { get; set; } = string.Empty;
}
