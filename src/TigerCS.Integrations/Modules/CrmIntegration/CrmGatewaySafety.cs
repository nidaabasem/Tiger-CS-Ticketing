namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The startup guard's actual decision logic (Program.cs calls
/// <see cref="IsUnsafe"/> rather than inlining this), extracted so it can be
/// unit-tested directly without booting a full <c>WebApplicationFactory</c>
/// host for every scenario.
///
/// <para>
/// <b>The guard is conditional on the selected gateway type, not the
/// environment name.</b> There is no blanket "if Production, refuse to
/// start" anywhere in this application (see Program.cs's own remarks) — no
/// production deployment is authorized at this pilot stage for unrelated
/// release-governance reasons (docs/DEV-SETUP.md, ADR-0022), but that is not
/// this code's decision to enforce. What this guard actually refuses is
/// narrower and specific to one real risk: <see cref="MockCrmGateway"/>
/// silently serving fixture data as if it were real Tiger CRM data outside
/// Development/Testing. A future real <c>InternalCrmGateway</c>
/// (<c>Crm:Provider</c> set to anything other than "Mock") is judged safe by
/// this method in every environment, Production included — see
/// <see cref="IsUnsafe"/>'s own tests for the proof.
/// </para>
/// </summary>
public static class CrmGatewaySafety
{
    /// <summary>
    /// Environments allowed to run MockCrmGateway — deliberately an
    /// allow-list, not a deny-list, so a future environment name (a real
    /// "UAT"/"Staging"/"Production") is refused by default rather than
    /// accidentally permitted because nobody thought to add it to a
    /// deny-list. "Testing" is TigerCsApiFactory's own WebApplicationFactory
    /// environment name.
    /// </summary>
    public static readonly IReadOnlyCollection<string> MockAllowedEnvironments = ["Development", "Testing"];

    /// <summary>
    /// True only when <paramref name="provider"/> resolves to the mock
    /// gateway (case-insensitive "Mock") and <paramref name="environmentName"/>
    /// is outside <see cref="MockAllowedEnvironments"/>. Any other provider
    /// value — including one that doesn't (yet) correspond to a real,
    /// registered <c>ICrmGateway</c> — is never flagged unsafe by this
    /// method; that failure mode belongs to service registration
    /// (<c>IntegrationsServiceCollectionExtensions</c>'s own
    /// <c>NotSupportedException</c>), not to this environment guard.
    /// </summary>
    public static bool IsUnsafe(string? provider, string environmentName) =>
        string.Equals(provider, "Mock", StringComparison.OrdinalIgnoreCase)
        && !MockAllowedEnvironments.Contains(environmentName);
}
