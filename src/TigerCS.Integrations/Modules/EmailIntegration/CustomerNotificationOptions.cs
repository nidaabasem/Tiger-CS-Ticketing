using TigerCS.Application.Modules.Notifications;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The <c>CustomerNotifications</c> section: where customer messages go. Environment-specific — UAT runs <c>Test</c> with a
/// <c>TestEmail</c>; Production sets <c>Mode = Production</c> explicitly. The shipped default is <c>Test</c> with no
/// <c>TestEmail</c>, which delivers nothing, so a deployment that forgets to configure this never contacts a customer.
/// </summary>
public sealed class CustomerNotificationOptions
{
    public const string SectionName = "CustomerNotifications";

    /// <summary><c>Test</c> or <c>Production</c>. Missing or anything else suppresses every customer message.</summary>
    public string? Mode { get; set; }

    /// <summary>Test mode only: the single mailbox Resolved and Closed emails are redirected to. Never falls back to the customer.</summary>
    public string? TestEmail { get; set; }

    /// <summary>
    /// Public base URL of the Ticketing web site (e.g. <c>https://tickets.tigergroup.ae</c>), used for the review links and the
    /// logo. Without it the emails are still sent but carry no review section.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long a review link stays valid.</summary>
    public int ReviewLinkLifetimeDays { get; set; } = 14;

    public BrandingOptions Branding { get; set; } = new();

    public sealed class BrandingOptions
    {
        /// <summary>Absolute logo URL. Blank uses <c>{PublicBaseUrl}/img/tiger-logo.png</c>, which the Ticketing web site serves.</summary>
        public string? LogoUrl { get; set; }

        public string? WebsiteUrl { get; set; } = "https://tigergroup.ae";

        /// <summary>Customer-service mailbox shown in the footer; blank omits the line.</summary>
        public string? ContactEmail { get; set; }

        /// <summary>Customer-service phone number shown in the footer; blank omits the line.</summary>
        public string? ContactPhone { get; set; }
    }

    public CustomerNotificationModePolicy ToPolicy() => new(
        Mode,
        TestEmail,
        PublicBaseUrl,
        TimeSpan.FromDays(Math.Clamp(ReviewLinkLifetimeDays, 1, 365)),
        new CustomerEmailBranding(Branding.LogoUrl, Branding.WebsiteUrl ?? string.Empty, Branding.ContactEmail, Branding.ContactPhone));
}
