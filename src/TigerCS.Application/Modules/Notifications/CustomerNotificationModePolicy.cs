using System.Globalization;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Application.Modules.Notifications;

public enum CustomerNotificationMode
{
    /// <summary>Missing or unrecognised <c>CustomerNotifications:Mode</c>. Treated as "send nothing to any customer".</summary>
    Invalid = 0,

    /// <summary>UAT / test: only Resolved and Closed are emailed, and only to <c>TestEmail</c>; the real customer is never contacted.</summary>
    Test = 1,

    /// <summary>The existing behaviour: customers receive their lifecycle emails.</summary>
    Production = 2
}

/// <summary>What the delivery boundary must do with one customer notification.</summary>
/// <param name="Deliver">False when nothing may be sent.</param>
/// <param name="Recipient">The mailbox that replaces the customer's (Test mode only); <c>null</c> means "the customer's own address".</param>
/// <param name="IsTest">The message is a test notification (marked in the subject, reviews kept out of production statistics).</param>
/// <param name="SuppressReason">A <see cref="CustomerEmailSkipReasons"/> code when <paramref name="Deliver"/> is false.</param>
public readonly record struct CustomerDeliveryDecision(bool Deliver, string? Recipient, bool IsTest, string? SuppressReason)
{
    public static CustomerDeliveryDecision Suppress(string reason, bool isTest = false) => new(false, null, isTest, reason);
}

/// <summary>The Tiger Properties branding shared by every customer email.</summary>
/// <param name="LogoUrl">Absolute https URL of the logo; <c>null</c> renders the brand name as text instead.</param>
/// <param name="WebsiteUrl">Company website shown in the footer.</param>
/// <param name="ContactEmail">Customer-service mailbox shown in the footer; <c>null</c> omits the line.</param>
/// <param name="ContactPhone">Customer-service phone shown in the footer; <c>null</c> omits the line.</param>
public sealed record CustomerEmailBranding(string? LogoUrl, string WebsiteUrl, string? ContactEmail, string? ContactPhone)
{
    public static CustomerEmailBranding Default { get; } = new(null, "https://tigergroup.ae", null, null);
}

/// <summary>
/// The Application layer's view of the <c>CustomerNotifications</c> configuration section: whether customer messages go to
/// customers (<see cref="CustomerNotificationMode.Production"/>), to a single test mailbox
/// (<see cref="CustomerNotificationMode.Test"/>), or nowhere (<see cref="CustomerNotificationMode.Invalid"/>).
///
/// <para>
/// <b>Fails safe.</b> A missing, blank or unrecognised mode is <see cref="CustomerNotificationMode.Invalid"/> and suppresses
/// every customer message. In Test mode a missing or malformed <c>TestEmail</c> suppresses delivery too — there is no
/// fallback to the customer's own address under any condition.
/// </para>
/// </summary>
public sealed class CustomerNotificationModePolicy
{
    public const string TestSubjectPrefix = "[TEST] ";

    public static readonly TimeSpan DefaultReviewLinkLifetime = TimeSpan.FromDays(14);

    public CustomerNotificationModePolicy(
        string? mode,
        string? testEmail,
        string? publicBaseUrl,
        TimeSpan? reviewLinkLifetime = null,
        CustomerEmailBranding? branding = null)
    {
        Mode = mode?.Trim().ToLowerInvariant() switch
        {
            "test" => CustomerNotificationMode.Test,
            "production" => CustomerNotificationMode.Production,
            _ => CustomerNotificationMode.Invalid
        };

        var candidate = CustomerEmailAddress.Normalize(testEmail);
        TestEmail = CustomerEmailAddress.IsValid(candidate) ? candidate : null;

        PublicBaseUrl = NormalizeBaseUrl(publicBaseUrl);
        ReviewLinkLifetime = reviewLinkLifetime is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : DefaultReviewLinkLifetime;

        var b = branding ?? CustomerEmailBranding.Default;
        Branding = b with
        {
            LogoUrl = CustomerEmailAddress.Normalize(b.LogoUrl) ?? (PublicBaseUrl is null ? null : PublicBaseUrl + "/img/tiger-logo.png"),
            WebsiteUrl = CustomerEmailAddress.Normalize(b.WebsiteUrl) ?? CustomerEmailBranding.Default.WebsiteUrl,
            ContactEmail = CustomerEmailAddress.IsValid(b.ContactEmail) ? CustomerEmailAddress.Normalize(b.ContactEmail) : null,
            ContactPhone = CustomerEmailAddress.Normalize(b.ContactPhone)
        };
    }

    /// <summary>The safe default: no customer message of any kind is delivered.</summary>
    public static CustomerNotificationModePolicy Unconfigured { get; } = new(null, null, null);

    /// <summary>The existing behaviour, for tests and call sites that predate the mode switch.</summary>
    public static CustomerNotificationModePolicy ProductionDefault(string? publicBaseUrl = null) =>
        new("Production", null, publicBaseUrl);

    public CustomerNotificationMode Mode { get; }

    public bool IsTest => Mode == CustomerNotificationMode.Test;

    public bool IsProduction => Mode == CustomerNotificationMode.Production;

    /// <summary>The validated test mailbox; <c>null</c> when missing or malformed.</summary>
    public string? TestEmail { get; }

    /// <summary>Absolute http(s) base URL of the public Ticketing site, without a trailing slash; <c>null</c> when not configured.</summary>
    public string? PublicBaseUrl { get; }

    public TimeSpan ReviewLinkLifetime { get; }

    public CustomerEmailBranding Branding { get; }

    /// <summary>Operator-facing explanation of why customer delivery is blocked or degraded; <c>null</c> when the configuration is sound.</summary>
    public string? ConfigurationWarning => Mode switch
    {
        CustomerNotificationMode.Invalid =>
            "CustomerNotifications:Mode is missing or invalid (expected \"Test\" or \"Production\"); no customer notification will be sent.",
        CustomerNotificationMode.Test when TestEmail is null =>
            "CustomerNotifications:Mode is Test but CustomerNotifications:TestEmail is missing or invalid; no notification will be sent.",
        _ => null
    };

    /// <summary>Test mode delivers only these, and only to the test mailbox.</summary>
    public static bool IsTestModeDelivered(NotificationType type) => type is NotificationType.Resolved or NotificationType.Closed;

    public CustomerDeliveryDecision Evaluate(NotificationType type)
    {
        switch (Mode)
        {
            case CustomerNotificationMode.Production:
                return new CustomerDeliveryDecision(true, null, false, null);

            case CustomerNotificationMode.Test:
                if (!IsTestModeDelivered(type))
                {
                    return CustomerDeliveryDecision.Suppress(CustomerEmailSkipReasons.TestModeSuppressed, isTest: true);
                }

                return TestEmail is null
                    ? CustomerDeliveryDecision.Suppress(CustomerEmailSkipReasons.TestEmailMissing, isTest: true)
                    : new CustomerDeliveryDecision(true, TestEmail, true, null);

            default:
                return CustomerDeliveryDecision.Suppress(CustomerEmailSkipReasons.CustomerNotificationModeInvalid);
        }
    }

    /// <summary>True when <paramref name="address"/> is the configured test mailbox.</summary>
    public bool IsTestMailbox(string? address) =>
        TestEmail is not null && string.Equals(CustomerEmailAddress.Normalize(address), TestEmail, StringComparison.OrdinalIgnoreCase);

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
