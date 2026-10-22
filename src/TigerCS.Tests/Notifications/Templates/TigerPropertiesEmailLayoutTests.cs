using System.Text.RegularExpressions;
using TigerCS.Application.Modules.CustomerReviews.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Templates;

namespace TigerCS.Tests.Notifications.Templates;

/// <summary>The Tiger Properties layout: branding, Outlook-safe structure, English and Arabic (RTL), review section, safety.</summary>
public class TigerPropertiesEmailLayoutTests
{
    private static readonly DateTime When = new(2026, 8, 22, 9, 30, 0, DateTimeKind.Utc);
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";

    private static readonly CustomerEmailBranding Branding = new(
        "https://tickets.example.com/img/tiger-logo.png", "https://tigergroup.ae", "care@example.com", "+971 4 000 0000");

    private static ReviewInvitationLinks Links() => new(
        $"https://tickets.example.com/Review?t={Token}",
        Enumerable.Range(1, 5).Select(r => $"https://tickets.example.com/Review?t={Token}&r={r}").ToList(),
        When.AddDays(14));

    private static CustomerEmailTicketModel Model(string language = "en", bool review = true, bool isTest = false, string? name = "Ahmed Al-Farsi") => new(
        "TG-CS-20260822-0042", name, When, ResolutionStatus: "Resolved", ResolutionMessage: "Thermostat replaced.",
        TicketSubject: "Maintenance Request", Language: language, Branding: Branding, Review: review ? Links() : null, IsTest: isTest);

    // ------------------------------------------------------------- English

    [Fact]
    public void Resolved_English_HasEverythingTheBriefAsks()
    {
        var content = CustomerEmailTemplates.TicketResolved(Model());

        Assert.Equal("Your Ticket #TG-CS-20260822-0042 Has Been Resolved", content.Subject);
        var html = content.HtmlBody;
        Assert.Contains("<img src=\"https://tickets.example.com/img/tiger-logo.png\" alt=\"Tiger Properties\"", html, StringComparison.Ordinal);
        Assert.Contains("Dear Ahmed Al-Farsi,", html, StringComparison.Ordinal);
        Assert.Contains("TG-CS-20260822-0042", html, StringComparison.Ordinal);
        Assert.Contains("Maintenance Request", html, StringComparison.Ordinal);
        Assert.Contains(">Status<", html, StringComparison.Ordinal);
        Assert.Contains(">Resolved<", html, StringComparison.Ordinal);
        Assert.Contains("Thermostat replaced.", html, StringComparison.Ordinal);
        Assert.Contains("Thank you for your patience", html, StringComparison.Ordinal);
        Assert.Contains("How was your experience?", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_English_HasEverythingTheBriefAsks()
    {
        var content = CustomerEmailTemplates.TicketClosed(Model());

        Assert.Equal("Your Ticket #TG-CS-20260822-0042 Has Been Closed", content.Subject);
        Assert.Contains(">Closed<", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Maintenance Request", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Thermostat replaced.", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("How was your experience?", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Branding_GoldBorder_WhiteContent_Footer_Website_ContactEmail()
    {
        var html = CustomerEmailTemplates.TicketResolved(Model()).HtmlBody;

        Assert.Contains("border:1px solid #C1A562", html, StringComparison.Ordinal);
        Assert.Contains("background-color:#ffffff", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://tigergroup.ae\"", html, StringComparison.Ordinal);
        Assert.Contains("mailto:care@example.com", html, StringComparison.Ordinal);
        Assert.Contains("+971 4 000 0000", html, StringComparison.Ordinal);
        Assert.Contains("Please do not reply", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApprovedDesign_WhiteThroughout_ThinGoldBorder_GreyHeaderDivider_NoDecoration(bool closed)
    {
        var html = (closed ? CustomerEmailTemplates.TicketClosed(Model()) : CustomerEmailTemplates.TicketResolved(Model())).HtmlBody;

        // Every background colour is white; nothing tinted, gradient, image or shadow.
        var backgrounds = Regex.Matches(html, @"background-color:\s*(#[0-9a-fA-F]{3,6})").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
        Assert.Equal(["#ffffff"], backgrounds);
        Assert.DoesNotContain("gradient", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("box-shadow", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text-shadow", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background-image", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("border-radius", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("border:2px", html, StringComparison.Ordinal);
        Assert.DoesNotContain("border:3px", html, StringComparison.Ordinal);
        Assert.DoesNotContain("border-bottom:3px", html, StringComparison.Ordinal);

        // Thin gold outer border; thin light-grey divider directly under the header (the logo cell).
        Assert.Matches(@"max-width:650px;background-color:#ffffff;border:1px solid #C1A562", html);
        Assert.Matches(@"<td align=""center"" style=""padding:[^""]*border-bottom:1px solid #e5e5e5;[^""]*""[^>]*><img ", html);
    }

    [Fact]
    public void Footer_DefaultBranding_HasTeamPhoneWebsiteAndContactEmail()
    {
        // No branding in the model: the shared defaults carry the approved footer.
        var model = Model() with { Branding = null };
        foreach (var content in new[] { CustomerEmailTemplates.TicketResolved(model), CustomerEmailTemplates.TicketClosed(model) })
        {
            var html = content.HtmlBody;
            var footer = html[html.LastIndexOf("border-top:1px solid #e5e5e5;padding:20px", StringComparison.Ordinal)..];
            Assert.Contains("Tiger Customer Service Team", footer, StringComparison.Ordinal);
            Assert.Contains("800 300 300", footer, StringComparison.Ordinal);
            Assert.Contains("href=\"https://www.tigergroup.ae\"", footer, StringComparison.Ordinal);
            Assert.Contains(">www.tigergroup.ae<", footer, StringComparison.Ordinal);
            Assert.Contains("href=\"mailto:contact@tigergroup.com\"", footer, StringComparison.Ordinal);
            Assert.Contains("Tiger Customer Service Team", content.TextBody, StringComparison.Ordinal);
            Assert.Contains("800 300 300", content.TextBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConfiguredPolicy_BlankBranding_FallsBackToTheApprovedFooterDetails()
    {
        var policy = new CustomerNotificationModePolicy(
            "true", "t@example.com", "971500000000", "https://tickets.example.com", null, new CustomerEmailBranding(null, "", "", ""));

        Assert.Equal("https://www.tigergroup.ae", policy.Branding.WebsiteUrl);
        Assert.Equal("contact@tigergroup.com", policy.Branding.ContactEmail);
        Assert.Equal("800 300 300", policy.Branding.ContactPhone);
        Assert.Equal("https://tickets.example.com/img/tiger-logo.png", policy.Branding.LogoUrl);
    }

    [Theory]
    [InlineData("en", "Your Support Ticket #TG-CS-20260822-0042 Has Been Created", "Dear Ahmed Al-Farsi,", "Created on", "22 August 2026, 13:30 UAE time", ">Open<")]
    [InlineData("ar", "تم إنشاء تذكرة الدعم رقم #TG-CS-20260822-0042", "عزيزي/عزيزتي أحمد الفارسي،", "تاريخ الإنشاء", "22 أغسطس 2026، 13:30 بتوقيت الإمارات", ">مفتوحة<")]
    public void Created_UsesTheApprovedDesign_WithNoReview_InBothLanguages(
        string language, string subject, string greeting, string createdLabel, string createdValue, string status)
    {
        var name = language == "ar" ? "أحمد الفارسي" : "Ahmed Al-Farsi";
        var content = CustomerEmailTemplates.TicketCreated(Model(language, name: name) with { RequestTypeName = "Maintenance Request" });
        var html = content.HtmlBody;

        Assert.Equal(subject, content.Subject);
        Assert.Contains(greeting, html, StringComparison.Ordinal);
        Assert.Contains("TG-CS-20260822-0042", html, StringComparison.Ordinal);
        Assert.Contains("Maintenance Request", html, StringComparison.Ordinal);
        Assert.Contains(createdLabel, html, StringComparison.Ordinal);
        Assert.Contains(createdValue, html, StringComparison.Ordinal);
        Assert.Contains(status, html, StringComparison.Ordinal);
        Assert.Contains(language == "ar" ? "dir=\"rtl\"" : "dir=\"ltr\"", html, StringComparison.Ordinal);

        // Same shell as Resolved/Closed; the satisfaction review is never part of it, even when links are offered.
        Assert.DoesNotContain("&#9733;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/Review", html + content.TextBody, StringComparison.Ordinal);
        Assert.Contains("border:1px solid #C1A562", html, StringComparison.Ordinal);
        Assert.Contains("border-bottom:1px solid #e5e5e5", html, StringComparison.Ordinal);
        Assert.Contains("<!--[if mso]>", html, StringComparison.Ordinal);
        Assert.Equal(["#ffffff"], Regex.Matches(html, @"background-color:\s*(#[0-9a-fA-F]{3,6})").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList());
        Assert.Contains(language == "ar" ? "فريق خدمة عملاء Tiger" : "Tiger Customer Service Team", content.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Created_Footer_DefaultBranding_HasPhoneWebsiteAndContactEmail()
    {
        var html = CustomerEmailTemplates.TicketCreated(Model() with { Branding = null }).HtmlBody;

        Assert.Contains("800 300 300", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://www.tigergroup.ae\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:contact@tigergroup.com\"", html, StringComparison.Ordinal);
        Assert.Contains("Tiger Customer Service Team", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "22 August 2026, 13:30 UAE time")]
    [InlineData("ar", "22 أغسطس 2026، 13:30 بتوقيت الإمارات")]
    public void EveryCustomerEmail_ShowsItsDateInUaeTime_NeverUtc(string language, string expected)
    {
        var model = Model(language);
        foreach (var content in new[]
        {
            CustomerEmailTemplates.TicketCreated(model), CustomerEmailTemplates.TicketResolved(model),
            CustomerEmailTemplates.TicketClosed(model), CustomerEmailTemplates.TicketReopened(model)
        })
        {
            // 09:30 UTC is 13:30 in the UAE.
            Assert.Contains(expected, content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains(expected, content.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", content.HtmlBody + content.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("09:30", content.HtmlBody + content.TextBody, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("2026-12-31T20:30:00Z", "01 January 2027, 00:30 UAE time", "1 يناير 2027، 00:30 بتوقيت الإمارات")]
    [InlineData("2026-03-01T19:59:00Z", "01 March 2026, 23:59 UAE time", "1 مارس 2026، 23:59 بتوقيت الإمارات")]
    [InlineData("2026-07-15T12:00:00Z", "15 July 2026, 16:00 UAE time", "15 يوليو 2026، 16:00 بتوقيت الإمارات")]
    public void UaeTime_CrossesMidnightAndMonthBoundaries_AndIgnoresTheMachineTimeZone(string utc, string english, string arabic)
    {
        var instant = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        Assert.Contains(english, CustomerEmailTemplates.TicketReopened(Model("en") with { OccurredAtUtc = instant }).TextBody, StringComparison.Ordinal);
        Assert.Contains(arabic, CustomerEmailTemplates.TicketReopened(Model("ar") with { OccurredAtUtc = instant }).TextBody, StringComparison.Ordinal);
        // An unspecified-kind value is treated as UTC, as every stored timestamp is.
        var unspecified = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified);
        Assert.Contains(english, CustomerEmailTemplates.TicketClosed(Model("en") with { OccurredAtUtc = unspecified }).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_IsOutlookSafe_TableBased_InlineCss_650pxMax_NoScriptOrForms()
    {
        var html = CustomerEmailTemplates.TicketResolved(Model()).HtmlBody;

        Assert.Contains("max-width:650px", html, StringComparison.Ordinal);
        Assert.Contains("<!--[if mso]>", html, StringComparison.Ordinal);
        Assert.Contains("width=\"650\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"presentation\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"viewport\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        // Layout is tables, not divs-for-columns/flex/grid.
        Assert.DoesNotContain("display:flex", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("display:grid", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewSection_FiveClickableGoldStars_EachLinkingToItsRating()
    {
        var html = CustomerEmailTemplates.TicketResolved(Model()).HtmlBody;

        var stars = Regex.Matches(html, @"<a href=""(?<url>[^""]+)""[^>]*color:#C1A562[^>]*>&#9733;</a>");
        Assert.Equal(5, stars.Count);
        for (var rating = 1; rating <= 5; rating++)
        {
            Assert.Equal($"https://tickets.example.com/Review?t={Token}&amp;r={rating}", stars[rating - 1].Groups["url"].Value);
        }

        Assert.Contains("title=\"1 star\"", html, StringComparison.Ordinal);
        Assert.Contains("title=\"5 stars\"", html, StringComparison.Ordinal);
        // A plain link to the page for clients that block the glyph, and the links in the text part.
        Assert.Contains($"href=\"https://tickets.example.com/Review?t={Token}\"", html, StringComparison.Ordinal);
        var text = CustomerEmailTemplates.TicketResolved(Model()).TextBody;
        Assert.Contains($"(5/5): https://tickets.example.com/Review?t={Token}&r=5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutReviewLinks_NoReviewSectionIsRendered()
    {
        var content = CustomerEmailTemplates.TicketClosed(Model(review: false));

        Assert.DoesNotContain("How was your experience?", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&#9733;", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/Review", content.HtmlBody + content.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewLinksThatAreNotHttp_AreNeverRendered()
    {
        var hostile = new ReviewInvitationLinks(
            "javascript:alert(1)", Enumerable.Range(1, 5).Select(_ => "javascript:alert(1)").ToList(), When);

        var content = CustomerEmailTemplates.TicketResolved(Model() with { Review = hostile });

        Assert.DoesNotContain("javascript:", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("How was your experience?", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLogoUrl_FallsBackToTheBrandNameInGold_AndOptionalContactLinesAreOmitted()
    {
        var bare = Model() with { Branding = new CustomerEmailBranding(null, "https://tigergroup.ae", null, null) };

        var html = CustomerEmailTemplates.TicketResolved(bare).HtmlBody;

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("color:#C1A562;\">Tiger Properties</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mailto:", html, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- Arabic

    [Fact]
    public void Resolved_Arabic_RendersRightToLeft_WithArabicText()
    {
        var content = CustomerEmailTemplates.TicketResolved(Model(language: "ar", name: "أحمد الفارسي"));

        Assert.Equal("تم حل تذكرتك رقم #TG-CS-20260822-0042", content.Subject);
        var html = content.HtmlBody;
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("text-align:right", html, StringComparison.Ordinal);
        Assert.Contains("عزيزي/عزيزتي أحمد الفارسي،", html, StringComparison.Ordinal);
        Assert.Contains("رقم التذكرة", html, StringComparison.Ordinal);
        Assert.Contains("تم الحل", html, StringComparison.Ordinal);
        Assert.Contains("كيف كانت تجربتك؟", html, StringComparison.Ordinal);
        Assert.Contains("charset=\"utf-8\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("How was your experience?", html, StringComparison.Ordinal);
        // The ticket number and the stars stay left-to-right and intact.
        Assert.Contains("TG-CS-20260822-0042", html, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(html, "&#9733;").Count);
        Assert.Contains("dir=\"ltr\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_Arabic_RendersRightToLeft_WithArabicText()
    {
        var content = CustomerEmailTemplates.TicketClosed(Model(language: "ar"));

        Assert.Equal("تم إغلاق تذكرتك رقم #TG-CS-20260822-0042", content.Subject);
        Assert.Contains("lang=\"ar\" dir=\"rtl\"", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("مغلقة", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("تم الآن إغلاق طلبك", content.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("EN", false)]
    [InlineData("fr", false)]
    [InlineData("", false)]
    [InlineData("ar", true)]
    [InlineData("AR", true)]
    public void Language_DefaultsToEnglish_AndArabicIsRtl(string language, bool rtl)
    {
        var html = CustomerEmailTemplates.TicketResolved(Model(language: language)).HtmlBody;

        Assert.Equal(rtl, html.Contains("dir=\"rtl\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("أحمد الفارسي", "ar")]
    [InlineData("Ahmed Al-Farsi", "en")]
    [InlineData("Al أحمد الفارسي", "ar")]
    [InlineData("Ahmed أحمد", "en")]
    [InlineData("Mohammed Ali Al-Mansoori أحمد", "en")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void TheEmailLanguage_FollowsTheScriptOfTheCustomersName(string? name, string expected) =>
        Assert.Equal(expected, TigerCS.Application.Modules.Notifications.Services.CustomerContactResolver.LanguageFor(name));

    [Fact]
    public void EveryTemplate_RendersInBothLanguages_WithoutThrowing_AndWithTheTicketNumber()
    {
        foreach (var language in new[] { "en", "ar" })
        {
            var model = Model(language);
            foreach (var content in new[]
            {
                CustomerEmailTemplates.TicketCreated(model), CustomerEmailTemplates.TicketResolved(model),
                CustomerEmailTemplates.TicketClosed(model), CustomerEmailTemplates.TicketReopened(model)
            })
            {
                Assert.Contains("TG-CS-20260822-0042", content.Subject, StringComparison.Ordinal);
                Assert.Contains("TG-CS-20260822-0042", content.HtmlBody, StringComparison.Ordinal);
                Assert.Contains("TG-CS-20260822-0042", content.TextBody, StringComparison.Ordinal);
                Assert.Contains("C1A562", content.HtmlBody, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------ safety/test

    [Fact]
    public void DynamicValues_AreEncoded_InBothLanguages_IncludingTheSubjectAndLinks()
    {
        foreach (var language in new[] { "en", "ar" })
        {
            var model = Model(language, name: "<script>alert(1)</script>") with
            {
                TicketSubject = "<img src=x onerror=alert(1)>",
                ResolutionMessage = "\"><b>bold</b>"
            };

            var html = CustomerEmailTemplates.TicketResolved(model).HtmlBody;

            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>bold</b>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TestNotification_IsMarkedInTheSubjectAndWithABanner()
    {
        var content = CustomerEmailTemplates.TicketResolved(Model(isTest: true));

        Assert.Equal("[TEST] Your Ticket #TG-CS-20260822-0042 Has Been Resolved", content.Subject);
        Assert.Contains("TEST NOTIFICATION", content.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("TEST NOTIFICATION", content.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST NOTIFICATION", CustomerEmailTemplates.TicketResolved(Model()).HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Greeting_FallsBackWhenTheNameIsUnknown()
    {
        Assert.Contains("Dear Customer,", CustomerEmailTemplates.TicketResolved(Model(name: null)).TextBody, StringComparison.Ordinal);
        Assert.Contains("عزيزي العميل،", CustomerEmailTemplates.TicketResolved(Model("ar", name: null)).TextBody, StringComparison.Ordinal);
    }
}
