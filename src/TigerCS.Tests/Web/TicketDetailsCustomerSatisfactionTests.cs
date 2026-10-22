extern alias TigerCsWeb;

using System.Runtime.CompilerServices;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>Ticket Details' Customer Satisfaction section: what it shows, what it never shows, and how it loads.</summary>
public sealed class TicketDetailsCustomerSatisfactionTests
{
    private static string Src(string relative, [CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), relative));

    private static string View() => Src(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml"));

    [Fact]
    public void View_ShowsRatingStars_NumericRating_Comment_UaeDate_Status_AndCycle()
    {
        var html = View();

        Assert.Contains("Customer Satisfaction", html);
        Assert.Contains("TicketDisplay.StarsFor(rating)", html);
        Assert.Contains("@rating/5", html);
        Assert.Contains("review.Comment", html);
        Assert.Contains("TicketDisplay.UaeTime(submittedAt)", html);
        Assert.Contains("review.Status", html);
        Assert.Contains("TicketDisplay.ReviewCycleLabel(review.ReopenCycle)", html);
    }

    [Fact]
    public void View_ShowsRatingOnlyWhenOneWasSubmitted_AndFlagsTestReviews()
    {
        var html = View();

        // The rating block is guarded by a non-null Rating, so a Pending cycle can never display one.
        Assert.Contains("@if (review.Rating is int rating)", html);
        Assert.Contains("review.IsTest", html);
        Assert.Contains("Test review", html);
        Assert.Contains("No review has been requested for this ticket yet.", html);
    }

    [Fact]
    public void View_NeverRendersATokenOrReviewLink_AndUsesNoInlineStyle()
    {
        var section = View();
        section = section[section.IndexOf("id=\"customer-satisfaction\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("Verification &amp; Unit", StringComparison.Ordinal)];

        Assert.DoesNotContain("Token", section, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Review?", section, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_LoadsReviewsFromTheApiOnEveryLoad_SoANewReviewAppearsOnRefresh()
    {
        var source = Src(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs"));

        Assert.Contains("ticketsApiClient.GetReviewsAsync(TicketId", source);
        Assert.Contains("ReviewsUnavailable", source);
    }

    [Fact]
    public void Display_UaeTime_IsUtcPlusFour_AndStarsMatchTheRating()
    {
        Assert.Equal("22 Oct 2026 21:40 (UAE)", TicketDisplay.UaeTime(new DateTime(2026, 10, 22, 17, 40, 0, DateTimeKind.Utc)));
        Assert.Equal("23 Oct 2026 00:30 (UAE)", TicketDisplay.UaeTime(new DateTime(2026, 10, 22, 20, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("★★★★☆", TicketDisplay.StarsFor(4));
        Assert.Equal("★★★★★", TicketDisplay.StarsFor(9));
        Assert.Equal("Original resolution", TicketDisplay.ReviewCycleLabel(0));
        Assert.Equal("After reopen 2", TicketDisplay.ReviewCycleLabel(2));
    }
}
