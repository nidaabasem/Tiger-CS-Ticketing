using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// <c>CustomerNotifications:Mode</c> — Test mode redirects Resolved/Closed to <c>TestEmail</c> and suppresses everything else
/// customer-facing; Production keeps the existing behaviour; a missing or invalid configuration sends nothing. Exercised at the
/// handlers, at the customer-email boundary and at the final provider guard.
/// </summary>
public class CustomerNotificationModeTests
{
    private const string TestInbox = "uat-inbox@example.org";
    private const string CustomerEmail = "ahmed@example.com";

    private static CustomerNotificationModePolicy Test(string? testEmail = TestInbox) =>
        new("Test", testEmail, NotificationServiceFixture.PublicBaseUrl);

    private static NotificationServiceFixture TestModeFixture(string? testEmail = TestInbox) =>
        new() { ModePolicy = Test(testEmail) };

    private static async Task<(NotificationServiceFixture F, Ticket Ticket)> ResolvedTicketAsync(
        NotificationServiceFixture f, string contact = CustomerEmail, string number = "TG-CS-20260822-0300")
    {
        var ticket = await f.SeedVerifiedTicketAsync(contact, number);
        await f.ResolveAsync(ticket);
        return (f, ticket);
    }

    // ---------------------------------------------------------------- Test mode

    [Fact]
    public async Task TestMode_Created_SendsNothingToAnyone()
    {
        var f = TestModeFixture();
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail);
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains(CustomerEmailSkipReasons.TestModeSuppressed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestMode_Reopened_SendsNothingToAnyone()
    {
        var f = TestModeFixture();
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail);
        await f.ResolveAsync(ticket);
        ticket.Close();
        ticket.Reopen(ticket.CurrentDepartmentId);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketReopened, ticket.TicketId, ticket.ReopenCount);

        await f.CreateReopenedHandler().HandleAsync(message);

        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Fact]
    public async Task TestMode_Resolved_GoesToTheTestMailboxOnly_MarkedAsATest_WithTheReview()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        var result = await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(TestInbox, email.ToAddress);
        Assert.Equal(EmailAudience.TestMailbox, email.Audience);
        Assert.Equal("[TEST] Your Ticket #TG-CS-20260822-0300 Has Been Resolved", email.Subject);
        Assert.Contains("TEST NOTIFICATION", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("TEST NOTIFICATION", email.Body, StringComparison.Ordinal);
        Assert.Contains("How was your experience?", email.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerEmail, email.HtmlBody + email.Body + email.Subject, StringComparison.Ordinal);

        var invitation = Assert.Single(f.Reviews.Invitations);
        Assert.True(invitation.IsTest);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(TestInbox, notification.RecipientAddress);
    }

    [Fact]
    public async Task TestMode_Closed_GoesToTheTestMailboxOnly_WithTheReview()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        ticket.Close();
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateClosedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(TestInbox, email.ToAddress);
        Assert.Equal("[TEST] Your Ticket #TG-CS-20260822-0300 Has Been Closed", email.Subject);
        Assert.Contains("How was your experience?", email.HtmlBody, StringComparison.Ordinal);
        Assert.True(Assert.Single(f.Reviews.Invitations).IsTest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("two@example.com;three@example.com")]
    public async Task TestMode_MissingOrInvalidTestEmail_SendsNothing_NeverFallsBackToTheCustomer(string? testEmail)
    {
        var f = TestModeFixture(testEmail);
        var (_, ticket) = await ResolvedTicketAsync(f);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, (await f.CreateResolvedHandler().HandleAsync(resolved)).Outcome);
        Assert.Equal(OutboxHandlingOutcome.Succeeded, (await f.CreateClosedHandler().HandleAsync(closed)).Outcome);

        Assert.Empty(f.Email.Sent);
        Assert.All(f.Notifications.Notifications, n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
        Assert.Contains(f.Audit.Entries, e => e.AfterValue!.Contains(CustomerEmailSkipReasons.TestEmailMissing, StringComparison.Ordinal));
        Assert.Empty(f.Reviews.Invitations);
    }

    [Fact]
    public async Task TestMode_MissingTestEmail_LogsAWarning()
    {
        var provider = new FakeEmailSender();
        var log = new CapturingLogger<CustomerEmailSender>();
        var sender = new CustomerEmailSender(provider, CustomerNotificationPolicy.EnabledDefault, Test(testEmail: null), log);

        var result = await sender.SendAsync(Request(NotificationType.Resolved, CustomerEmail));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Equal(CustomerEmailSkipReasons.TestEmailMissing, result.Reason);
        Assert.Empty(provider.Sent);
        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("TestEmail", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerEmailAddressForLogs, line.Message, StringComparison.Ordinal);
    }

    private const string CustomerEmailAddressForLogs = "ahmed@example.com";

    [Fact]
    public async Task TestMode_StillTestsATicketWhoseCustomerHasNoEmail()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f, contact: "+971500000009");
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        await f.CreateResolvedHandler().HandleAsync(message);

        Assert.Equal(TestInbox, Assert.Single(f.Email.Sent).ToAddress);
    }

    [Fact]
    public async Task TestMode_RedeliveredMessage_SendsTheTestEmailOnce()
    {
        var f = TestModeFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        var handler = f.CreateResolvedHandler();

        await handler.HandleAsync(message);
        await handler.HandleAsync(message);

        Assert.Single(f.Email.Sent);
        Assert.Single(f.Reviews.Invitations);
    }

    [Fact]
    public async Task TestMode_AutomaticClosure_FollowsTheSamePolicy()
    {
        // Auto-closure (customer inactivity, completed document delivery) enqueues the same Resolved and Closed events and
        // runs through the same dispatcher and handlers: both go to the test mailbox, nothing reaches the customer.
        var f = TestModeFixture();
        var ticket = await f.SeedGenesysTicketAsync(CustomerEmail);
        await f.ResolveAsync(ticket);
        f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        var dispatcher = f.CreateDispatcher(
            f.CreateHandler(), f.CreateResolvedHandler(), f.CreateClosedHandler(), f.CreateReopenedHandler());
        var result = await dispatcher.DispatchPendingAsync();

        Assert.Equal(2, result.Processed);
        Assert.All(f.Email.Sent, e => Assert.Equal(TestInbox, e.ToAddress));
        Assert.Equal(2, f.Email.Sent.Count);
        Assert.All(f.Email.Sent, e => Assert.StartsWith("[TEST] ", e.Subject, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Staging")]
    [InlineData("1")]
    public async Task InvalidMode_SendsNoCustomerMessageOfAnyKind(string? mode)
    {
        var f = new NotificationServiceFixture { ModePolicy = new CustomerNotificationModePolicy(mode, TestInbox, NotificationServiceFixture.PublicBaseUrl) };
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail);
        var created = f.EnqueueTicketCreated(ticket.TicketId);
        await f.ResolveAsync(ticket);
        var resolved = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);
        ticket.Close();
        var closed = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketClosed, ticket.TicketId);

        await f.CreateHandler().HandleAsync(created);
        await f.CreateResolvedHandler().HandleAsync(resolved);
        await f.CreateClosedHandler().HandleAsync(closed);

        Assert.Equal(CustomerNotificationMode.Invalid, f.ModePolicy.Mode);
        Assert.NotNull(f.ModePolicy.ConfigurationWarning);
        Assert.Empty(f.Email.Sent);
        Assert.All(f.Notifications.Notifications, n => Assert.Equal(NotificationDeliveryStatus.Skipped, n.DeliveryStatus));
    }

    [Fact]
    public void ModeIsCaseInsensitive_AndTheDefaultUnconfiguredPolicyDeliversNothing()
    {
        Assert.Equal(CustomerNotificationMode.Test, new CustomerNotificationModePolicy(" test ", TestInbox, null).Mode);
        Assert.Equal(CustomerNotificationMode.Production, new CustomerNotificationModePolicy("PRODUCTION", null, null).Mode);

        foreach (var type in Enum.GetValues<NotificationType>())
        {
            Assert.False(CustomerNotificationModePolicy.Unconfigured.Evaluate(type).Deliver);
        }
    }

    // --------------------------------------------------------------- Production

    [Fact]
    public async Task Production_Resolved_GoesToTheActualCustomer_WithTheReview_AndNoTestMarking()
    {
        var f = new NotificationServiceFixture();
        var (_, ticket) = await ResolvedTicketAsync(f);
        var message = f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId);

        await f.CreateResolvedHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal(CustomerEmail, email.ToAddress);
        Assert.Equal(EmailAudience.Customer, email.Audience);
        Assert.Equal("Your Ticket #TG-CS-20260822-0300 Has Been Resolved", email.Subject);
        Assert.DoesNotContain("TEST", email.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("How was your experience?", email.HtmlBody, StringComparison.Ordinal);
        Assert.False(Assert.Single(f.Reviews.Invitations).IsTest);
    }

    [Fact]
    public async Task Production_Created_StillAcknowledgesTheCustomer()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(CustomerEmail);
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        await f.CreateHandler().HandleAsync(message);

        Assert.Equal(CustomerEmail, Assert.Single(f.Email.Sent).ToAddress);
        Assert.NotNull(ticket.AcknowledgementSentAtUtc);
    }

    [Fact]
    public async Task Production_ADisabledEmailSwitch_StillSendsNothing()
    {
        var f = new NotificationServiceFixture { NotificationPolicy = CustomerNotificationPolicy.Disabled };
        var (_, ticket) = await ResolvedTicketAsync(f);

        await f.CreateResolvedHandler().HandleAsync(f.EnqueueLifecycleEvent(OutboxEventTypes.TicketResolved, ticket.TicketId));

        Assert.Empty(f.Email.Sent);
    }

    // ------------------------------------------- final delivery boundary (guard)

    private static EmailMessage Message(EmailAudience audience, string to = CustomerEmail) =>
        new(to, "Subject", "Body", Guid.NewGuid(), "<p>Body</p>", Audience: audience);

    private static CustomerDeliveryGuardEmailSender Guard(
        FakeEmailSender provider, CustomerNotificationModePolicy mode, VerificationTestingPolicy? verification = null) =>
        new(provider, mode, verification, NullLogger<CustomerDeliveryGuardEmailSender>.Instance);

    [Fact]
    public async Task Guard_TestMode_SuppressesEveryOtherCustomerFacingEmail()
    {
        // Document copies, OTP codes, Collections reminders and anything added later build an EmailMessage that defaults to
        // the Customer audience — and cannot reach the provider in Test mode.
        var provider = new FakeEmailSender();
        var guard = Guard(provider, Test());

        var result = await guard.SendAsync(new EmailMessage(CustomerEmail, "Payment reminder", "Body", Guid.NewGuid()));

        Assert.Equal(EmailSendOutcome.Suppressed, result.Outcome);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task Guard_TestMode_PassesInternalEmailUntouched()
    {
        var provider = new FakeEmailSender();

        var result = await Guard(provider, Test()).SendAsync(Message(EmailAudience.Internal, "manager@tigergroup.example"));

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);
        Assert.Equal("manager@tigergroup.example", Assert.Single(provider.Sent).ToAddress);
    }

    [Fact]
    public async Task Guard_TestMode_RefusesATestMailboxMessageAddressedToAnyoneElse()
    {
        var provider = new FakeEmailSender();
        var guard = Guard(provider, Test());

        var toCustomer = await guard.SendAsync(Message(EmailAudience.TestMailbox, CustomerEmail));
        var toTestInbox = await guard.SendAsync(Message(EmailAudience.TestMailbox, TestInbox.ToUpperInvariant()));

        Assert.Equal(EmailSendOutcome.Suppressed, toCustomer.Outcome);
        Assert.Equal(EmailSendOutcome.Sent, toTestInbox.Outcome);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task Guard_TestMode_AllowsTheVerificationTestingInbox_ForOtpAndDocumentMail()
    {
        var provider = new FakeEmailSender();
        var verification = new VerificationTestingPolicy(
            new VerificationTestingOptions { Enabled = true, SmsOverride = "971500000001", EmailOverride = "otp-inbox@example.org", BlockRealCustomerDelivery = true },
            "UAT");
        var guard = Guard(provider, Test(), verification);

        var otp = await guard.SendAsync(Message(EmailAudience.TestMailbox, "otp-inbox@example.org"));
        var unredirected = await guard.SendAsync(Message(EmailAudience.Customer, CustomerEmail));

        Assert.Equal(EmailSendOutcome.Sent, otp.Outcome);
        Assert.Equal(EmailSendOutcome.Suppressed, unredirected.Outcome);
    }

    [Fact]
    public async Task Guard_InvalidMode_SuppressesEveryCustomerAndTestMailboxMessage_ButNotInternal()
    {
        var provider = new FakeEmailSender();
        var guard = Guard(provider, CustomerNotificationModePolicy.Unconfigured);

        Assert.Equal(EmailSendOutcome.Suppressed, (await guard.SendAsync(Message(EmailAudience.Customer))).Outcome);
        Assert.Equal(EmailSendOutcome.Suppressed, (await guard.SendAsync(Message(EmailAudience.TestMailbox, TestInbox))).Outcome);
        Assert.Equal(EmailSendOutcome.Sent, (await guard.SendAsync(Message(EmailAudience.Internal, "ops@tigergroup.example"))).Outcome);
    }

    [Fact]
    public async Task Guard_Production_PassesCustomerEmailThrough()
    {
        var provider = new FakeEmailSender();

        var result = await Guard(provider, CustomerNotificationModePolicy.ProductionDefault()).SendAsync(Message(EmailAudience.Customer));

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);
        Assert.Equal(CustomerEmail, Assert.Single(provider.Sent).ToAddress);
    }

    [Fact]
    public async Task Guard_ASuppressedMessage_IsReportedAsASkip_NotAFailureThatWouldBeRetried()
    {
        var provider = new FakeEmailSender();
        var sender = new CustomerEmailSender(
            Guard(provider, Test()), CustomerNotificationPolicy.EnabledDefault, CustomerNotificationModePolicy.ProductionDefault(),
            NullLogger<CustomerEmailSender>.Instance);

        // The façade was (mis)configured for Production while the guard is in Test mode: the guard still refuses, and the
        // result is a terminal skip rather than a transient failure the dispatcher would retry.
        var result = await sender.SendAsync(Request(NotificationType.Resolved, CustomerEmail));

        Assert.Equal(CustomerEmailSendOutcome.Skipped, result.Outcome);
        Assert.Empty(provider.Sent);
    }

    // ------------------------------------------------------------------- SMS

    [Fact]
    public async Task SmsGuard_TestMode_SuppressesUnredirectedSms_AndAllowsOnlyTheConfiguredTestNumber()
    {
        var inner = new TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender(
            Microsoft.Extensions.Options.Options.Create(new TigerCS.Integrations.Modules.SmsIntegration.SmsOptions()),
            NullLogger<TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender>.Instance);
        var verification = new VerificationTestingPolicy(
            new VerificationTestingOptions { Enabled = true, SmsOverride = "971500000001", EmailOverride = "otp-inbox@example.org", BlockRealCustomerDelivery = true },
            "UAT");
        var guard = new CustomerDeliveryGuardSmsSender(inner, Test(), verification, NullLogger<CustomerDeliveryGuardSmsSender>.Instance);

        var toCustomer = await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en"));
        var claimsRedirectButWrongNumber = await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en", TestRedirected: true));
        var toTestNumber = await guard.SendAsync(new SmsMessage("971500000001", "code 123", "en", TestRedirected: true));

        Assert.Equal(SmsSendOutcome.Suppressed, toCustomer.Outcome);
        Assert.Equal(SmsSendOutcome.Suppressed, claimsRedirectButWrongNumber.Outcome);
        Assert.Equal(SmsSendOutcome.Accepted, toTestNumber.Outcome);
    }

    [Theory]
    [InlineData("Production", SmsSendOutcome.Accepted)]
    [InlineData("Test", SmsSendOutcome.Suppressed)]
    [InlineData(null, SmsSendOutcome.Suppressed)]
    public async Task SmsGuard_ByMode(string? mode, SmsSendOutcome expected)
    {
        var inner = new TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender(
            Microsoft.Extensions.Options.Options.Create(new TigerCS.Integrations.Modules.SmsIntegration.SmsOptions()),
            NullLogger<TigerCS.Integrations.Modules.SmsIntegration.FakeSmsSender>.Instance);
        var guard = new CustomerDeliveryGuardSmsSender(
            inner, new CustomerNotificationModePolicy(mode, TestInbox, null), null, NullLogger<CustomerDeliveryGuardSmsSender>.Instance);

        Assert.Equal(expected, (await guard.SendAsync(new SmsMessage("971555555555", "code 123", "en"))).Outcome);
    }

    private static CustomerEmailRequest Request(NotificationType type, string? recipient) => new(
        type, TicketId: 7, TicketNumber: "TG-CS-20260822-0007", recipient,
        new CustomerEmailContent("Subject", "Body", "<p>Body</p>"), Guid.NewGuid());
}
