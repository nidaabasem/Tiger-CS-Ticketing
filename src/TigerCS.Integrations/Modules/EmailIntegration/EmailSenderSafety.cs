namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The startup guard's decision logic for the email adapter, extracted so it
/// is unit-testable without booting a host. Deliberately identical in shape to
/// <c>CrmGatewaySafety</c> — one pattern for "a test double must never run
/// where it could be mistaken for the real thing".
///
/// <para>
/// The risk this guards is specific: <see cref="RecordingEmailSender"/>
/// reports every send as successful without contacting any provider. Running
/// it outside Development/Testing would mark tickets acknowledged, write
/// <c>Sent</c> notification rows, and satisfy every dashboard and audit query
/// — while no customer ever received anything. A silent, total failure that
/// looks exactly like success is worse than a loud one, so it is refused at
/// startup instead.
/// </para>
/// </summary>
public static class EmailSenderSafety
{
    /// <summary>
    /// An allow-list, not a deny-list, so a future environment name ("UAT",
    /// "Staging", a real "Production") is refused by default rather than
    /// permitted because nobody remembered to add it. "Testing" is
    /// <c>TigerCsApiFactory</c>'s WebApplicationFactory environment name.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RecordingAllowedEnvironments = ["Development", "Testing"];

    /// <summary>
    /// True only when <paramref name="provider"/> resolves to the recording
    /// adapter (case-insensitive "Recording") and
    /// <paramref name="environmentName"/> is outside
    /// <see cref="RecordingAllowedEnvironments"/>. A future real adapter is
    /// never flagged unsafe by this method — an unknown provider value is
    /// service registration's problem, not this guard's.
    /// </summary>
    public static bool IsUnsafe(string? provider, string environmentName) =>
        string.Equals(provider, "Recording", StringComparison.OrdinalIgnoreCase)
        && !RecordingAllowedEnvironments.Contains(environmentName);
}
