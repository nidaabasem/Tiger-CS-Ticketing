namespace TigerCS.Integrations.Modules.SmsIntegration;

/// <summary>Startup guard: the fake sender prints/keeps codes and pretends delivery succeeded, so it may only run in Development/Testing.</summary>
public static class SmsSafety
{
    public static readonly IReadOnlyCollection<string> FakeAllowedEnvironments = ["Development", "Testing"];

    public static bool IsUnsafe(string? provider, string environmentName) =>
        string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase)
        && !FakeAllowedEnvironments.Contains(environmentName);
}
