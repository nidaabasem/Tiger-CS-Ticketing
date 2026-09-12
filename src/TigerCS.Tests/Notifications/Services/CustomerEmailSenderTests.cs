using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// The <see cref="CustomerEmailSender"/> façade: the shared safety rules
/// every customer notification passes through, tested at the boundary
/// with the provider replaced by <see cref="FakeEmailSender"/>.
/// </summary>
public class CustomerEmailSenderTests
{
    private const string SecretPassword = "Sup3r-Secret-Smtp-P@ssw0rd";

    private static CustomerEmailRequest Request(string? recipient = "customer@example.com") => new(
        NotificationType.Acknowledgement,
        TicketId: 42,
        TicketNumber: "TG-CS-20260822-0042",
        RecipientAddress: recipient,
        new CustomerEmailContent("Subject line", "Plain body", "<p>Html body</p>"),
        Guid.NewGuid());

    private static (CustomerEmailSender Sender, FakeEmailSender Provider, CapturingLogger<CustomerEmailSender> Log) Create(
        CustomerNotificationPolicy? policy = null)
    {
        var provider = new FakeEmailSender();
        var log = new CapturingLogger<CustomerEmailSender>();
        return (new CustomerEmailSender(provider, policy ?? CustomerNotificationPolicy.EnabledDefault, log), provider, log);
    }

    [Fact]
    public async Task EnabledAndConfigured_SendsThroughTheProviderWithBothBodies()
    {
        var (sender, provider, log) = Create();
        var request = Request();

        var result = await sender.SendAsync(request);

        Assert.Equal(CustomerEmailSendOutcome.Sent, result.Outcome);
        var sent = Assert.Single(provider.Sent);
        Assert.Equal("customer@example.com", sent.ToAddress);
        Assert.Equal("Subject line", sent.Subject);
        Assert.Equal("Plain body", sent.Body);
        Assert.Equal("<p>Html body</p>", sent.HtmlBody);
        Assert.Equal(request.CorrelationId, sent.CorrelationId);

        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("TG-CS-20260822-0042", line.Message, StringComparison.Ordinal);
        Assert.Contains("Acknowledgement", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_SkipsWithoutTouchingTheProvider()
    {
        var (sender, provider, log) = Create(CustomerNotificationPolicy.Disabled);

        var result = await sender.SendAsync(Request());

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Equal(CustomerEmailSkipReasons.NotificationsDisabled, result.Reason);
        Assert.Empty(provider.Sent);
        Assert.Contains(log.Messages, m => m.Contains(CustomerEmailSkipReasons.NotificationsDisabled, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingRecipient_SkipsSafely(string? recipient)
    {
        var (sender, provider, _) = Create();

        var result = await sender.SendAsync(Request(recipient));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Equal(CustomerEmailSkipReasons.NoCustomerEmail, result.Reason);
        Assert.Empty(provider.Sent);
    }

    [Theory]
    [InlineData("+971501234567")]
    [InlineData("Ahmed Al-Farsi")]
    [InlineData("ahmed@localhost")]
    [InlineData("ahmed@@example.com")]
    [InlineData("Ahmed <ahmed@example.com>")]
    [InlineData("ahmed@example.com, other@example.com")]
    public async Task InvalidRecipient_SkipsSafelyAndNeverEchoesTheValue(string recipient)
    {
        var (sender, provider, log) = Create();

        var result = await sender.SendAsync(Request(recipient));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Equal(CustomerEmailSkipReasons.InvalidCustomerEmail, result.Reason);
        Assert.Empty(provider.Sent);
        Assert.DoesNotContain(log.Messages, m => m.Contains(recipient, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderThatThrows_ReturnsATransientResultInsteadOfThrowing()
    {
        var (sender, provider, log) = Create();
        provider.ThrowOnNextSend = new InvalidOperationException($"connection refused; password={SecretPassword}");

        var result = await sender.SendAsync(Request());

        Assert.Equal(CustomerEmailSendOutcome.TransientFailure, result.Outcome);
        Assert.Contains(nameof(InvalidOperationException), result.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPassword, result.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Messages, m => m.Contains(SecretPassword, StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ProviderPermanentFailure_IsReportedAsPermanent()
    {
        var (sender, provider, _) = Create();
        provider.ThenPermanent("SMTP MailboxUnavailable (SmtpFailedRecipientException).");

        var result = await sender.SendAsync(Request());

        Assert.Equal(CustomerEmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Contains("MailboxUnavailable", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderTransientFailure_IsReportedAsTransient()
    {
        var (sender, provider, _) = Create();
        provider.ThenTransient();

        var result = await sender.SendAsync(Request());

        Assert.Equal(CustomerEmailSendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task LogLines_MaskTheRecipientAndNeverCarryTheBody()
    {
        var (sender, _, log) = Create();

        await sender.SendAsync(Request("ahmed.alfarsi@example.com"));

        var line = Assert.Single(log.Messages);
        Assert.Contains("a***@example.com", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ahmed.alfarsi@example.com", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain body", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Html body", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject line", line, StringComparison.Ordinal);
    }
}
