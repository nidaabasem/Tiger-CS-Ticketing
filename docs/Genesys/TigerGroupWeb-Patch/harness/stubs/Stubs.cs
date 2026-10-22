// STAND-INS ONLY. The real TicketingOptions and GenesysAuthDefaults live in TigerGroupWeb and were not available.
// Only the members the two patched files use are declared; their values here are test values.
namespace Tiger.TigerGroupWeb.Genesys
{
    public class TicketingOptions
    {
        public const int TigerCsMarginSeconds = 3;
        public const string TigerCsDeadlineHeader = "X-Collections-Deadline-Seconds";
        public const string FlowTimeoutHeader = "X-Genesys-Flow-Timeout-Seconds";

        public string BaseUrl { get; set; } = "https://ticketing.test";
        public string Username { get; set; } = "svc-genesys";
        public string Password { get; set; } = "PASSWORD-MUST-NEVER-BE-LOGGED";

        public TimeSpan CollectionsBudget(int? flowTimeoutSeconds) => TimeSpan.FromSeconds(flowTimeoutSeconds ?? 20);
    }
}

namespace Tiger.TigerGroupWeb.Genesys.Auth
{
    public static class GenesysAuthDefaults
    {
        public const string PolicyName = "GenesysPolicy";
    }
}
