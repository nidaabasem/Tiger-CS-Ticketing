using System.Text.RegularExpressions;

namespace TigerCS.Application.Modules.CrmDocuments;

/// <summary>
/// <c>VerificationTesting</c> — the Local/UAT test mode for the Genesys OTP and document-copy flows. Real CRM customers are
/// still looked up and verified (identity, unit ownership, the challenge and the session are all untouched); only the
/// <b>outbound message</b> is redirected to the two test inboxes below, so no real customer receives a code or a document.
/// Off by default; never honoured in Production (see <see cref="VerificationTestingPolicy"/>).
/// </summary>
public sealed class VerificationTestingOptions
{
    public const string SectionName = "VerificationTesting";

    public bool Enabled { get; set; }

    /// <summary>International digits (no "+") every OTP SMS is sent to while test mode is on.</summary>
    public string? SmsOverride { get; set; }

    /// <summary>Mailbox every OTP email and every document email is sent to while test mode is on.</summary>
    public string? EmailOverride { get; set; }

    /// <summary>Must be true while test mode is on: a test mode that may still reach real customers is refused.</summary>
    public bool BlockRealCustomerDelivery { get; set; }
}

/// <summary>The only environments where customer-data test redirection may be switched on.</summary>
public static class VerificationTestingSafety
{
    public static readonly IReadOnlyList<string> AllowedEnvironments = ["Local", "Development", "UAT", "Testing"];
}

/// <summary>The resolved destination of one outbound message under test mode.</summary>
/// <param name="Allowed">False when test mode is on but misconfigured: nothing may be sent.</param>
/// <param name="Destination">Where the message must actually go (the override when <paramref name="Redirected"/>).</param>
/// <param name="Redirected">True when <paramref name="Destination"/> replaced the customer's real contact detail.</param>
public readonly record struct TestDeliveryRoute(bool Allowed, string Destination, bool Redirected);

/// <summary>
/// Decides, at the outbound delivery layer, where an OTP or document message really goes.
///
/// <para>
/// <b>Test mode off</b> (the default, and the only state Production can be in): every route is the customer's own contact
/// detail, unchanged. <b>Test mode on</b>: every route is the configured override — and if the configuration is invalid,
/// incomplete or enabled in a non-allowed environment, <see cref="IsBlocked"/> is true and <i>nothing</i> is routable, so a
/// real customer can never be messaged by a half-configured test mode. There is no fallback to the real address.
/// </para>
/// </summary>
public sealed partial class VerificationTestingPolicy
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[1-9]\d{7,14}$")]
    private static partial Regex SmsPattern();

    private readonly string _smsOverride;
    private readonly string _emailOverride;

    public VerificationTestingPolicy(VerificationTestingOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        Enabled = options.Enabled;
        _smsOverride = options.SmsOverride?.Trim() ?? string.Empty;
        _emailOverride = options.EmailOverride?.Trim() ?? string.Empty;

        if (!Enabled)
        {
            return;
        }

        if (!VerificationTestingSafety.AllowedEnvironments.Contains(environmentName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            ConfigurationError =
                $"VerificationTesting:Enabled is true in environment '{environmentName}'. Customer-data test redirection may only run in "
                + $"{string.Join("/", VerificationTestingSafety.AllowedEnvironments)}.";
        }
        else if (!options.BlockRealCustomerDelivery)
        {
            ConfigurationError = "VerificationTesting:BlockRealCustomerDelivery must be true while VerificationTesting:Enabled is true.";
        }
        else if (!SmsPattern().IsMatch(_smsOverride))
        {
            ConfigurationError = "VerificationTesting:SmsOverride must be an international number in digits only (8–15 digits, no '+').";
        }
        else if (!EmailPattern().IsMatch(_emailOverride) || _emailOverride.Length > 254)
        {
            ConfigurationError = "VerificationTesting:EmailOverride must be a valid email address.";
        }
    }

    public bool Enabled { get; }

    /// <summary>Why test mode cannot be used; null when it is off or valid.</summary>
    public string? ConfigurationError { get; }

    /// <summary>Test mode is on but unusable — every delivery must be refused.</summary>
    public bool IsBlocked => Enabled && ConfigurationError is not null;

    /// <summary>Where an OTP SMS for <paramref name="customerMobile"/> must really go.</summary>
    public TestDeliveryRoute RouteSms(string customerMobile) => Route(customerMobile, _smsOverride);

    /// <summary>Where an email (OTP or document) for <paramref name="customerEmail"/> must really go.</summary>
    public TestDeliveryRoute RouteEmail(string customerEmail) => Route(customerEmail, _emailOverride);

    private TestDeliveryRoute Route(string original, string overrideValue)
    {
        if (!Enabled)
        {
            return new TestDeliveryRoute(true, original, false);
        }

        return IsBlocked
            ? new TestDeliveryRoute(false, string.Empty, false)
            : new TestDeliveryRoute(true, overrideValue, true);
    }
}
