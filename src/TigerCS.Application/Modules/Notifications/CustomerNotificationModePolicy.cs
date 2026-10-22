using System.Globalization;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Application.Modules.Notifications;

/// <summary>What the delivery boundary must do with one customer message.</summary>
/// <param name="Deliver">False when nothing may be sent.</param>
/// <param name="Recipient">The test recipient that replaces the customer's contact (test mode only); <c>null</c> means "the customer's own".</param>
/// <param name="IsTest">The message is a test notification.</param>
/// <param name="SuppressReason">A <see cref="CustomerEmailSkipReasons"/> code when <paramref name="Deliver"/> is false.</param>
public readonly record struct CustomerDeliveryDecision(bool Deliver, string? Recipient, bool IsTest, string? SuppressReason)
{
    public static CustomerDeliveryDecision Suppress(string reason) => new(false, null, true, reason);
}

/// <summary>The Tiger Properties branding shared by every customer email.</summary>
/// <param name="LogoUrl">Absolute public https URL of the logo; <c>null</c> renders the brand name as text instead.</param>
/// <param name="WebsiteUrl">Company website shown in the footer.</param>
/// <param name="ContactEmail">Customer-service mailbox shown in the footer; <c>null</c> omits the line (the configured policy falls back to the default).</param>
/// <param name="ContactPhone">Customer-service phone shown in the footer; <c>null</c> omits the line.</param>
public sealed record CustomerEmailBranding(string? LogoUrl, string WebsiteUrl, string? ContactEmail, string? ContactPhone)
{
    /// <summary>The official TIGER logo, hosted publicly so every mail client (Outlook included) can load it.</summary>
    public const string OfficialLogoUrl = "https://www.tigergroup.ae/logos/TigerHead.jpg";

    /// <summary>An email logo must be an absolute https URL on a public host — never localhost, a relative path or plain http.</summary>
    public static bool IsPublicHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !uri.IsLoopback
        && uri.Host.Contains('.')
        && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    public static CustomerEmailBranding Default { get; } = new(OfficialLogoUrl, "https://www.tigergroup.ae", "contact@tigergroup.com", "800 300 300");
}

/// <summary>
/// The Application layer's view of the <c>CustomerNotifications</c> section — one Boolean switch.
///
/// <para>
/// <c>IsTestMode = true</c>: <i>every</i> customer email goes to <c>TestEmail</c> and every customer SMS to <c>TestPhone</c>;
/// the customer's own contact details are never used. <c>IsTestMode = false</c>: messages go to their intended recipients.
/// </para>
///
/// <para>
/// <b>Fails safe.</b> Only an explicit <c>false</c> turns test mode off; a missing, blank or unrecognised value means test
/// mode. In test mode a missing or invalid recipient suppresses that delivery — there is no fallback to the customer.
/// </para>
/// </summary>
public sealed class CustomerNotificationModePolicy
{
    public const string TestSubjectPrefix = "[TEST] ";

    public static readonly TimeSpan DefaultReviewLinkLifetime = TimeSpan.FromDays(14);

    public CustomerNotificationModePolicy(
        string? isTestMode,
        string? testEmail,
        string? testPhone,
        string? publicBaseUrl,
        TimeSpan? reviewLinkLifetime = null,
        CustomerEmailBranding? branding = null)
    {
        IsTest = !string.Equals(isTestMode?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        var candidate = CustomerEmailAddress.Normalize(testEmail);
        TestEmail = CustomerEmailAddress.IsValid(candidate) ? candidate : null;
        TestPhone = NormalizePhone(testPhone);

        PublicBaseUrl = NormalizeBaseUrl(publicBaseUrl);
        ReviewLinkLifetime = reviewLinkLifetime is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : DefaultReviewLinkLifetime;

        var b = branding ?? CustomerEmailBranding.Default;
        Branding = b with
        {
            LogoUrl = CustomerEmailBranding.IsPublicHttpsUrl(CustomerEmailAddress.Normalize(b.LogoUrl))
                ? CustomerEmailAddress.Normalize(b.LogoUrl)
                : CustomerEmailBranding.OfficialLogoUrl,
            WebsiteUrl = CustomerEmailAddress.Normalize(b.WebsiteUrl) ?? CustomerEmailBranding.Default.WebsiteUrl,
            ContactEmail = CustomerEmailAddress.IsValid(b.ContactEmail) ? CustomerEmailAddress.Normalize(b.ContactEmail) : CustomerEmailBranding.Default.ContactEmail,
            ContactPhone = CustomerEmailAddress.Normalize(b.ContactPhone) ?? CustomerEmailBranding.Default.ContactPhone
        };
    }

    /// <summary>The safe default (configuration absent): test mode with no recipients, so nothing is delivered.</summary>
    public static CustomerNotificationModePolicy Unconfigured { get; } = new(null, null, null, null);

    /// <summary><c>IsTestMode = false</c>, for tests and call sites that predate the switch.</summary>
    public static CustomerNotificationModePolicy ProductionDefault(string? publicBaseUrl = null) =>
        new("false", null, null, publicBaseUrl);

    public bool IsTest { get; }

    public bool IsProduction => !IsTest;

    /// <summary>The validated test mailbox; <c>null</c> when missing or malformed.</summary>
    public string? TestEmail { get; }

    /// <summary>The test mobile number as international digits (no "+"); <c>null</c> when missing or malformed.</summary>
    public string? TestPhone { get; }

    /// <summary>Absolute http(s) base URL of the public Ticketing site, without a trailing slash; <c>null</c> when not configured.</summary>
    public string? PublicBaseUrl { get; }

    public TimeSpan ReviewLinkLifetime { get; }

    public CustomerEmailBranding Branding { get; }

    /// <summary>Operator-facing explanation of why test-mode delivery is blocked; <c>null</c> when sound or not in test mode.</summary>
    public string? ConfigurationWarning
    {
        get
        {
            if (!IsTest)
            {
                return null;
            }

            var missing = new List<string>();
            if (TestEmail is null) missing.Add("CustomerNotifications:TestEmail (customer emails are skipped)");
            if (TestPhone is null) missing.Add("CustomerNotifications:TestPhone (customer SMS are skipped)");
            return missing.Count == 0 ? null : "IsTestMode is on but " + string.Join(" and ", missing) + " is missing or invalid.";
        }
    }

    /// <summary>How a customer email is routed: to the test mailbox in test mode (or suppressed if there is none), else as intended.</summary>
    public CustomerDeliveryDecision DecideEmail()
    {
        if (!IsTest)
        {
            return new CustomerDeliveryDecision(true, null, false, null);
        }

        return TestEmail is null
            ? CustomerDeliveryDecision.Suppress(CustomerEmailSkipReasons.TestEmailMissing)
            : new CustomerDeliveryDecision(true, TestEmail, true, null);
    }

    /// <summary>How a customer SMS is routed: to the test number in test mode (or suppressed if there is none), else as intended.</summary>
    public CustomerDeliveryDecision DecideSms()
    {
        if (!IsTest)
        {
            return new CustomerDeliveryDecision(true, null, false, null);
        }

        return TestPhone is null
            ? CustomerDeliveryDecision.Suppress(CustomerEmailSkipReasons.TestPhoneMissing)
            : new CustomerDeliveryDecision(true, TestPhone, true, null);
    }

    private static string? NormalizePhone(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(c => !(char.IsWhiteSpace(c) || c is '+' or '-' or '(' or ')')).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length is >= 8 and <= 15 && digits[0] != '0' && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>
    /// The review page link for a token, optionally with a pre-selected rating. <c>null</c> when no public base URL is
    /// configured. The link carries the opaque token and the rating only — never a ticket id, name or any contact detail.
    /// </summary>
    public string? BuildReviewUrl(string token, int? rating = null)
    {
        if (PublicBaseUrl is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var url = $"{PublicBaseUrl}/Review?t={Uri.EscapeDataString(token)}";
        return rating is >= 1 and <= 5 ? $"{url}&r={rating.Value.ToString(CultureInfo.InvariantCulture)}" : url;
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        var trimmed = CustomerEmailAddress.Normalize(value);
        if (trimmed is null || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
