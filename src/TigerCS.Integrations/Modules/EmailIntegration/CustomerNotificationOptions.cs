using TigerCS.Application.Modules.Notifications;

namespace TigerCS.Integrations.Modules.EmailIntegration;

/// <summary>
/// The <c>CustomerNotifications</c> section: where customer messages go. Environment-specific — UAT runs
/// <c>IsTestMode = true</c> with a <c>TestEmail</c>/<c>TestPhone</c>; Production sets <c>IsTestMode = false</c> explicitly. The
/// shipped default is test mode with no recipients, so a deployment that forgets to configure this never contacts a customer.
/// </summary>
public sealed class CustomerNotificationOptions
{
    public const string SectionName = "CustomerNotifications";

    /// <summary>
    /// <c>true</c> (the default) redirects every customer email to <see cref="TestEmail"/> and every customer SMS to
    /// <see cref="TestPhone"/>. Only an explicit <c>false</c> delivers to real customers; a missing or unrecognised value
    /// is test mode. Held as text so that a malformed value fails safe instead of failing configuration binding.
    /// </summary>
    public string? IsTestMode { get; set; } = "true";

    /// <summary>Test mode: the mailbox that receives every customer email. Missing/invalid ⇒ customer emails are skipped.</summary>
    public string? TestEmail { get; set; }

    /// <summary>Test mode: the mobile number that receives every customer SMS. Missing/invalid ⇒ customer SMS are skipped.</summary>
    public string? TestPhone { get; set; }

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

        public string? WebsiteUrl { get; set; } = "https://www.tigergroup.ae";

        /// <summary>Customer-service mailbox shown in the footer; blank uses the default (contact@tigergroup.com).</summary>
        public string? ContactEmail { get; set; }

        /// <summary>Customer-service phone number shown in the footer; blank uses the default (800 300 300).</summary>
        public string? ContactPhone { get; set; }
    }

    public CustomerNotificationModePolicy ToPolicy() => new(
        IsTestMode,
        TestEmail,
        TestPhone,
        PublicBaseUrl,
        TimeSpan.FromDays(Math.Clamp(ReviewLinkLifetimeDays, 1, 365)),
        new CustomerEmailBranding(Branding.LogoUrl, Branding.WebsiteUrl ?? string.Empty, Branding.ContactEmail, Branding.ContactPhone));
}
