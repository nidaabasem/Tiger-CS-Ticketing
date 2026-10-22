using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// Routing as production wires it: <c>CustomerNotifications</c> configuration → policy → delivery guard → provider. Only recording
/// senders sit behind the guards, so no real message can leave the test, whatever the mode.
/// </summary>
public class CustomerRoutingFromConfigurationTests
{
    private const string Customer = "customer@real-customer.example";
    private const string CustomerMobile = "971501112222";
    private const string TestInbox = "uat-inbox@example.com";
    private const string TestMobile = "971509998888";

    private static ServiceProvider Build(string? isTestMode, string testEmail = TestInbox, string testPhone = "+971 50 999 8888")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Sms:Provider"] = "Fake",
            ["CustomerNotifications:TestEmail"] = testEmail,
            ["CustomerNotifications:TestPhone"] = testPhone
        };
        if (isTestMode is not null)
        {
            settings["CustomerNotifications:IsTestMode"] = isTestMode;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTigerCsSms(configuration);
        return services.BuildServiceProvider();
    }

    private static FakeSmsSender Recorder(IServiceProvider sp) => sp.GetRequiredService<FakeSmsSender>();

    [Fact]
    public async Task TestMode_OtpSms_GoesToTestPhoneOnly()
    {
        using var provider = Build("true");
        var sms = provider.CreateScope().ServiceProvider.GetRequiredService<ISmsSender>();

        var result = await sms.SendAsync(new SmsMessage(CustomerMobile, "Your Tiger verification code is 123456", "en"));

        Assert.Equal(SmsSendOutcome.Accepted, result.Outcome);
        var sent = Assert.Single(Recorder(provider).Sent);
        Assert.Equal(TestMobile, sent.Destination);
        Assert.DoesNotContain(Recorder(provider).Sent, m => m.Destination == CustomerMobile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public async Task MissingOrUnrecognisedSwitch_IsTestMode_SmsStillGoesToTestPhone(string? value)
    {
        using var provider = Build(value);
        var sms = provider.CreateScope().ServiceProvider.GetRequiredService<ISmsSender>();

        await sms.SendAsync(new SmsMessage(CustomerMobile, "code 123456", "en"));

        Assert.Equal(TestMobile, Assert.Single(Recorder(provider).Sent).Destination);
    }

    [Fact]
    public async Task TestMode_MissingTestPhone_SendsNothing_NeverFallsBackToTheCustomer()
    {
        using var provider = Build("true", testPhone: "");
        var sms = provider.CreateScope().ServiceProvider.GetRequiredService<ISmsSender>();

        var result = await sms.SendAsync(new SmsMessage(CustomerMobile, "code 123456", "en"));

        Assert.Equal(SmsSendOutcome.Suppressed, result.Outcome);
        Assert.Empty(Recorder(provider).Sent);
    }

    [Fact]
    public async Task Production_IsTestModeFalse_OtpSms_GoesToTheCustomersOwnNumber_ThroughTheRecordingSender()
    {
        using var provider = Build("false");
        var sms = provider.CreateScope().ServiceProvider.GetRequiredService<ISmsSender>();

        await sms.SendAsync(new SmsMessage(CustomerMobile, "code 123456", "en"));

        Assert.Equal(CustomerMobile, Assert.Single(Recorder(provider).Sent).Destination);
    }

    [Theory]
    [InlineData("true", TestInbox, true)]
    [InlineData("false", Customer, false)]
    public async Task Email_RoutingFollowsTheSwitch_WithARecordingSender(string isTestMode, string expectedRecipient, bool expectTestSubject)
    {
        using var provider = Build(isTestMode);
        var recorder = new FakeEmailSender();
        var guard = new CustomerDeliveryGuardEmailSender(
            recorder, provider.GetRequiredService<CustomerNotificationModePolicy>(), NullLogger<CustomerDeliveryGuardEmailSender>.Instance);

        await guard.SendAsync(new EmailMessage(Customer, "Your Ticket #TG-1 Has Been Closed", "text", Guid.NewGuid(), "<p>html</p>"));
        await guard.SendAsync(new EmailMessage("staff@tiger.example", "Escalation", "text", Guid.NewGuid(), null, Audience: EmailAudience.Internal));

        Assert.Equal(2, recorder.Sent.Count);
        Assert.Equal(expectedRecipient, recorder.Sent[0].ToAddress);
        Assert.Equal(expectTestSubject, recorder.Sent[0].Subject.StartsWith("[TEST] ", StringComparison.Ordinal));
        Assert.Equal("staff@tiger.example", recorder.Sent[1].ToAddress);
    }
}
