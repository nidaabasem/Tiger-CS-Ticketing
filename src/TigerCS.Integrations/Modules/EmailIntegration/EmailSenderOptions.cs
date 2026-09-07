namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// Governs which <see cref="TigerCS.Application.Modules.Notifications.Abstractions.IEmailSender"/>
/// implementation is wired up. Mirrors <c>CrmGatewayOptions</c> exactly, for
/// the same reason: the choice of provider is deployment configuration, never
/// a branch inside business logic.
///
/// <para>
/// <b>Only "Recording" is implemented (OPERATIONAL BLOCKER).</b> No real
/// email provider is confirmed. Module-Design.md names "Office 365 Email" as
/// the Notifications module's external dependency and Solution-Analysis.md's
/// INT-05 marks its authentication <c>[ASSUMPTION] SMTP relay/API key</c> —
/// no tenant, sender identity, relay host, or credential appears in any
/// merged document. Until those are supplied, this system cannot deliver a
/// real acknowledgement to a real customer, and nothing here should be
/// described as production-ready.
/// </para>
///
/// <para>
/// <b>No credential belongs in this class or in any committed settings
/// file.</b> When a real provider is confirmed, its secret must come from
/// user-secrets in development and from the platform's secret store in a
/// deployed environment — never from <c>appsettings.json</c>, which is
/// committed. The options here are deliberately limited to non-secret shape.
/// </para>
/// </summary>
public sealed class EmailSenderOptions
{
    public const string SectionName = "Notifications:Email";

    /// <summary>
    /// Whether email delivery is part of the current deployment phase.
    /// Email delivery is <b>not</b> part of the UAT / management-demo phase,
    /// and only <see cref="RecordingEmailSender"/> exists, so a UAT or
    /// Production host has to be able to start with the recording adapter
    /// without pretending it delivers anything. Setting this to
    /// <c>false</c> tells <see cref="EmailSenderSafety"/> (enforced in
    /// <c>Program.cs</c>) that no delivery is expected, so the "Recording
    /// outside Development/Testing" startup refusal does not apply.
    ///
    /// <para>
    /// This flag changes the startup guard only. It does not select a
    /// different adapter and does not make anything send: the recording
    /// adapter still records in memory and contacts no provider, exactly as
    /// before. Defaults to <c>true</c> in code so an environment whose
    /// configuration omits the key keeps the existing protection — the
    /// committed <c>appsettings.json</c> sets it to <c>false</c> for the
    /// current no-delivery phase, and re-enabling it with the recording
    /// adapter outside Development/Testing is refused at startup again.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The adapter to wire up. "Recording" is the only supported value at this pilot phase.</summary>
    public string Provider { get; set; } = "Recording";

    /// <summary>
    /// The <c>From</c> identity a real adapter would send as. Not a secret,
    /// and deliberately not defaulted to a plausible-looking Tiger Group
    /// address: an unconfigured sender should be visibly unconfigured rather
    /// than quietly wrong. Unused by the recording adapter.
    /// </summary>
    public string? FromAddress { get; set; }

    public string? FromDisplayName { get; set; }
}
