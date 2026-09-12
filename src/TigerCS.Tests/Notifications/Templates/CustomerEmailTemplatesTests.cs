using TigerCS.Application.Modules.Notifications.Templates;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Notifications.Templates;

public class CustomerEmailTemplatesTests
{
    private static readonly DateTime When = new(2026, 8, 22, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TicketCreated_UsesTheAgreedSubjectAndApprovedFields()
    {
        var content = CustomerEmailTemplates.TicketCreated(new CustomerEmailTicketModel(
            "TG-CS-20260822-0042", "Ahmed Al-Farsi", When, RequestTypeName: "Maintenance Request"));

        Assert.Equal("Your request has been received – Ticket TG-CS-20260822-0042", content.Subject);
        Assert.Contains("Dear Ahmed Al-Farsi,", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Ticket number: TG-CS-20260822-0042", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Request type: Maintenance Request", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Received on: 22 August 2026, 09:30 UTC", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("has been received", content.TextBody, StringComparison.Ordinal);
        Assert.Contains(CustomerEmailTemplates.SignatureTeam, content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Tiger Properties", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("TG-CS-20260822-0042", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketCreated_WithoutNameOrRequestType_FallsBackGracefully()
    {
        var content = CustomerEmailTemplates.TicketCreated(new CustomerEmailTicketModel("TG-CS-20260822-0001", null, When));

        Assert.Contains("Dear Customer,", content.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Request type", content.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Request type", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketResolved_IncludesTheCustomerFriendlyStatusAndOptionalMessage()
    {
        var content = CustomerEmailTemplates.TicketResolved(new CustomerEmailTicketModel(
            "TG-CS-20260822-0042", "Ahmed", When,
            ResolutionStatus: CustomerEmailTemplates.DescribeResolution(ResolutionOutcome.Resolved),
            ResolutionMessage: "Thermostat replaced."));

        Assert.Equal("Your request has been resolved – Ticket TG-CS-20260822-0042", content.Subject);
        Assert.Contains("Status: Resolved", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Resolution: Thermostat replaced.", content.TextBody, StringComparison.Ordinal);
        Assert.Contains("Resolved on: 22 August 2026, 09:30 UTC", content.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketResolved_WithoutAMessage_OmitsTheResolutionLine()
    {
        var content = CustomerEmailTemplates.TicketResolved(new CustomerEmailTicketModel(
            "TG-CS-20260822-0042", null, When, ResolutionStatus: "Resolved"));

        Assert.DoesNotContain("Resolution:", content.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResolutionOutcome.Resolved, "Resolved")]
    [InlineData(ResolutionOutcome.Cancelled, "Cancelled")]
    [InlineData(ResolutionOutcome.Rejected, "Reviewed – no further action required")]
    [InlineData(ResolutionOutcome.Duplicate, "Merged with an existing request")]
    [InlineData(null, "Resolved")]
    public void DescribeResolution_UsesCustomerWordingNotAgentVocabulary(ResolutionOutcome? outcome, string expected) =>
        Assert.Equal(expected, CustomerEmailTemplates.DescribeResolution(outcome));

    [Fact]
    public void TicketClosedAndReopened_UseTheAgreedSubjects()
    {
        var closed = CustomerEmailTemplates.TicketClosed(new CustomerEmailTicketModel("TG-CS-20260822-0042", "Ahmed", When));
        var reopened = CustomerEmailTemplates.TicketReopened(new CustomerEmailTicketModel("TG-CS-20260822-0042", "Ahmed", When));

        Assert.Equal("Your request has been closed – Ticket TG-CS-20260822-0042", closed.Subject);
        Assert.Contains("has now been closed", closed.TextBody, StringComparison.Ordinal);
        Assert.Equal("Your request has been reopened – Ticket TG-CS-20260822-0042", reopened.Subject);
        Assert.Contains("working on it again", reopened.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlBody_EncodesEveryDynamicValue()
    {
        const string hostileName = "<script>alert('x')</script> & \"Ahmed\"";
        const string hostileNote = "<img src=x onerror=alert(1)>";

        var content = CustomerEmailTemplates.TicketResolved(new CustomerEmailTicketModel(
            "TG-CS-<b>1</b>", hostileName, When, ResolutionStatus: "Resolved", ResolutionMessage: hostileNote));

        Assert.DoesNotContain("<script>", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>1</b>", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;img", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&amp; &quot;Ahmed&quot;", content.HtmlBody, StringComparison.Ordinal);

        // The plain-text body carries the raw text — there is no markup to
        // inject into — but the subject is never HTML either way.
        Assert.Contains(hostileName, content.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTemplate_SharesTheHeaderFooterAndSignature()
    {
        var model = new CustomerEmailTicketModel("TG-CS-20260822-0042", "Ahmed", When, ResolutionStatus: "Resolved");
        var all = new[]
        {
            CustomerEmailTemplates.TicketCreated(model),
            CustomerEmailTemplates.TicketResolved(model),
            CustomerEmailTemplates.TicketClosed(model),
            CustomerEmailTemplates.TicketReopened(model)
        };

        foreach (var content in all)
        {
            Assert.StartsWith("<!DOCTYPE html>", content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("viewport", content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains(CustomerEmailTemplates.BrandName, content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains(CustomerEmailTemplates.SignatureTeam, content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("Please do not reply", content.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("Kind regards,", content.TextBody, StringComparison.Ordinal);
            Assert.Contains("Please do not reply", content.TextBody, StringComparison.Ordinal);
            Assert.Contains("– Ticket TG-CS-20260822-0042", content.Subject, StringComparison.Ordinal);
        }
    }
}
