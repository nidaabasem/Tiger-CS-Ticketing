using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.Otp;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Otp;

public sealed class SmsRegistrationTests
{
    private sealed class AllLogs : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);
        public void Dispose() { }

        private sealed class Capture(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines) { lines.Add(category + ": " + formatter(state, exception)); }
            }
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, AllLogs? logs = null, StubHttpMessageHandler? handler = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Trace); if (logs is not null) b.AddProvider(logs); });
        services.AddTigerCsSms(configuration);
        if (handler is not null)
        {
            services.AddHttpClient<BroadnetSmsSender>().ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheDefault_IsDisabled_NothingIsConfigured()
    {
        using var provider = Build([]);
        using var scope = provider.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISmsSender>();

        Assert.IsType<DisabledSmsSender>(sender);
        Assert.False(sender.IsConfigured);
    }

    [Theory]
    [InlineData("Fake", typeof(FakeSmsSender))]
    [InlineData("Broadnet", typeof(BroadnetSmsSender))]
    [InlineData("Disabled", typeof(DisabledSmsSender))]
    public void TheProviderSwitchSelectsTheSender(string name, Type expected)
    {
        using var provider = Build(new() { ["Sms:Provider"] = name });
        using var scope = provider.CreateScope();

        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<ISmsSender>());
    }

    [Fact]
    public void BroadnetSelectedButUnconfirmed_StaysInert_NoRealSmsCanGoOut()
    {
        using var provider = Build(new() { ["Sms:Provider"] = "Broadnet" });
        using var scope = provider.CreateScope();

        Assert.False(scope.ServiceProvider.GetRequiredService<ISmsSender>().IsConfigured);
    }

    [Fact]
    public void AnUnknownProvider_FailsLoudly()
    {
        using var provider = Build(new() { ["Sms:Provider"] = "Twilio" });
        using var scope = provider.CreateScope();

        Assert.Throws<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<ISmsSender>());
    }

    [Fact]
    public async Task TheHttpPipeline_NeverLogsTheRequestUrl_ThePasswordOrTheText()
    {
        var logs = new AllLogs();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK 1") }));
        using var provider = Build(new()
        {
            ["Sms:Provider"] = "Broadnet",
            ["Sms:Broadnet:Endpoint"] = "https://sms.example.test/websmpp/websms",
            ["Sms:Broadnet:User"] = "api-user",
            ["Sms:Broadnet:Password"] = "TopSecretPassword",
            ["Sms:Broadnet:SenderId"] = "SID",
            ["Sms:Broadnet:TypeEnglish"] = "1",
            ["Sms:Broadnet:TypeArabic"] = "2",
            ["Sms:Broadnet:MobileFormat"] = "InternationalDigits",
            ["Sms:Broadnet:SuccessBodyPattern"] = "^OK"
        }, logs, handler);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISmsSender>();

        var result = await sender.SendAsync(new SmsMessage("971501234567", "code 482913", "en"));

        Assert.Equal(SmsSendOutcome.Accepted, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        var everything = string.Join("\n", logs.Lines);
        Assert.DoesNotContain("TopSecretPassword", everything);
        Assert.DoesNotContain("pass=", everything);
        Assert.DoesNotContain("482913", everything);
        Assert.DoesNotContain("sms.example.test", everything);
    }
}
