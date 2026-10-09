using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Integrations.Modules.SmsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Otp;

/// <summary>
/// <b>Stub-based, not a Broadnet integration test.</b> The endpoint, the English/Arabic type values, the mobile format
/// and the success/failure response patterns below are <b>test inputs</b>, not Broadnet's confirmed contract — which is
/// still to be supplied. These tests prove the adapter's behaviour (encoding, strict response handling, no guessing,
/// redaction); they say nothing about whether real SMS is delivered.
/// </summary>
public sealed class BroadnetSmsSenderTests
{
    private const string Password = "S3cret&pass#word";
    private const string Code = "482913";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private static SmsOptions Configured(Action<BroadnetSmsOptions>? tweak = null)
    {
        var options = new SmsOptions
        {
            Provider = "Broadnet",
            Broadnet = new BroadnetSmsOptions
            {
                Endpoint = "https://sms.example.test/websmpp/websms",
                User = "api-user",
                Password = Password,
                SenderId = "TIGER OTP",
                TypeEnglish = "T-EN",
                TypeArabic = "T-AR",
                MobileFormat = MobileNumberFormat.InternationalDigits,
                SuccessBodyPattern = @"^OK (?<ref>\w+)",
                FailureBodyPattern = @"^ERR",
                TimeoutSeconds = 1
            }
        };
        tweak?.Invoke(options.Broadnet);
        return options;
    }

    private static (BroadnetSmsSender Sender, StubHttpMessageHandler Handler, CapturingLogger<BroadnetSmsSender> Log) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, Action<BroadnetSmsOptions>? tweak = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var log = new CapturingLogger<BroadnetSmsSender>();
        var sender = new BroadnetSmsSender(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(Configured(tweak)), log);
        return (sender, handler, log);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply(HttpStatusCode status, string body = "") =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static SmsMessage Message(string text = "Your code is " + Code, string language = "en") =>
        new("971501234567", text, language);

    // ------------------------------------------------------------ request

    [Fact]
    public async Task EveryValueIsPercentEncoded_ArabicSpacesAmpersandsAndHashesCannotBreakTheQuery()
    {
        var (sender, handler, _) = Create(Reply(HttpStatusCode.OK, "OK abc"));

        await sender.SendAsync(Message("رمز 123 & code #1 =x", "ar"));

        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.StartsWith("https://sms.example.test/websmpp/websms?user=api-user&pass=", url);
        var query = System.Web.HttpUtility.ParseQueryString(handler.LastRequest.RequestUri.Query);
        Assert.Equal(Password, query["pass"]);                       // round-trips: & and # did not split it
        Assert.Equal("TIGER OTP", query["sid"]);
        Assert.Equal("971501234567", query["mno"]);
        Assert.Equal("T-AR", query["type"]);
        Assert.Equal("رمز 123 & code #1 =x", query["text"]);
        Assert.Equal(6, query.AllKeys.Length);                        // nothing was injected
    }

    [Theory]
    [InlineData("en", "T-EN")]
    [InlineData("ar", "T-AR")]
    public async Task LanguageSelectsTheConfiguredProviderType(string language, string expected)
    {
        var (sender, handler, _) = Create(Reply(HttpStatusCode.OK, "OK 1"));

        await sender.SendAsync(Message(language: language));

        Assert.Equal(expected, System.Web.HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query)["type"]);
    }

    [Fact]
    public async Task PlusFormat_PrefixesThePlus()
    {
        var (sender, handler, _) = Create(Reply(HttpStatusCode.OK, "OK 1"), o => o.MobileFormat = MobileNumberFormat.PlusInternational);

        await sender.SendAsync(Message());

        Assert.Equal("+971501234567", System.Web.HttpUtility.ParseQueryString(handler.LastRequest!.RequestUri!.Query)["mno"]);
    }

    // ------------------------------------------------------ configuration

    [Fact]
    public void Configured_ReportsConfigured()
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK));
        Assert.True(sender.IsConfigured);
        Assert.Null(sender.ValidationProblem());
    }

    [Theory]
    [InlineData("http://sms.example.test/websmpp/websms", "Endpoint")]          // plain http refused
    [InlineData("", "Endpoint")]
    [InlineData("https://sms.example.test/send?x=1", "Endpoint")]               // no query string in the endpoint
    [InlineData("not a url", "Endpoint")]
    public void EndpointMustBeAnHttpsUrlWithoutAQuery(string endpoint, string expectedSetting)
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK), o => o.Endpoint = endpoint);

        Assert.False(sender.IsConfigured);
        Assert.Contains(expectedSetting, sender.ValidationProblem());
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Password")]
    [InlineData("SenderId")]
    [InlineData("TypeEnglish")]
    [InlineData("TypeArabic")]
    [InlineData("SuccessBodyPattern")]
    public void EveryProviderValueIsRequired_NothingIsDefaulted(string setting)
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK), o => typeof(BroadnetSmsOptions).GetProperty(setting)!.SetValue(o, ""));

        Assert.False(sender.IsConfigured);
        Assert.Contains(setting, sender.ValidationProblem());
    }

    [Fact]
    public void AnUnsetMobileFormat_OrABrokenPattern_KeepsItUnconfigured()
    {
        Assert.False(Create(Reply(HttpStatusCode.OK), o => o.MobileFormat = MobileNumberFormat.Unset).Sender.IsConfigured);
        Assert.False(Create(Reply(HttpStatusCode.OK), o => o.SuccessBodyPattern = "(unclosed").Sender.IsConfigured);
        Assert.False(Create(Reply(HttpStatusCode.OK), o => o.FailureBodyPattern = "(unclosed").Sender.IsConfigured);
        Assert.False(new BroadnetSmsSender(new HttpClient(), Options.Create(new SmsOptions()), new CapturingLogger<BroadnetSmsSender>()).IsConfigured);
    }

    [Fact]
    public async Task Unconfigured_NeverCallsTheProvider()
    {
        var (sender, handler, _) = Create(Reply(HttpStatusCode.OK, "OK 1"), o => o.TypeArabic = "");

        var result = await sender.SendAsync(Message());

        Assert.Equal(SmsSendOutcome.Failed, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    // ----------------------------------------------------------- outcomes

    [Fact]
    public async Task RecognisedSuccess_IsAccepted_WithTheProviderReference()
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK, "OK msg42"));

        var result = await sender.SendAsync(Message());

        Assert.Equal(SmsSendOutcome.Accepted, result.Outcome);
        Assert.Equal("msg42", result.ProviderReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>maintenance</html>")]
    [InlineData("accepted?")]
    public async Task A2xxThatIsNotARecognisedSuccess_IsNeverReportedAsSent(string body)
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK, body));

        Assert.Equal(SmsSendOutcome.Unconfirmed, (await sender.SendAsync(Message())).Outcome);
    }

    [Fact]
    public async Task AnExplicitFailureBody_BeatsAnHttp200()
    {
        var (sender, _, _) = Create(Reply(HttpStatusCode.OK, "ERR bad sender"));

        Assert.Equal(SmsSendOutcome.Rejected, (await sender.SendAsync(Message())).Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SmsSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, SmsSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, SmsSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, SmsSendOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, SmsSendOutcome.Failed)]
    [InlineData(HttpStatusCode.BadGateway, SmsSendOutcome.Failed)]
    [InlineData(HttpStatusCode.RequestTimeout, SmsSendOutcome.Unconfirmed)]
    [InlineData(HttpStatusCode.Found, SmsSendOutcome.Unconfirmed)]
    public async Task NonSuccessStatuses_MapToExplicitOutcomes_NeverToAccepted(HttpStatusCode status, SmsSendOutcome expected)
    {
        var (sender, _, _) = Create(Reply(status, "OK 1"));   // even a success-looking body is ignored on a non-2xx

        Assert.Equal(expected, (await sender.SendAsync(Message())).Outcome);
    }

    [Fact]
    public async Task ATimeout_IsUnconfirmed_AndNotRetried()
    {
        var (sender, handler, log) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await sender.SendAsync(Message());

        Assert.Equal(SmsSendOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains(log.Lines, l => l.Contains("unconfirmed"));
    }

    [Fact]
    public async Task ACallerCancellation_IsNotSwallowedAsAProviderOutcome()
    {
        var (sender, _, _) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message(), cts.Token));
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, SmsSendOutcome.Failed)]
    [InlineData(HttpRequestError.NameResolutionError, SmsSendOutcome.Failed)]
    [InlineData(HttpRequestError.SecureConnectionError, SmsSendOutcome.Failed)]
    [InlineData(HttpRequestError.ResponseEnded, SmsSendOutcome.Unconfirmed)]
    [InlineData(HttpRequestError.Unknown, SmsSendOutcome.Unconfirmed)]
    public async Task NetworkErrors_AreFailedOnlyWhenTheRequestProvablyNeverLeft(HttpRequestError error, SmsSendOutcome expected)
    {
        var (sender, _, _) = Create((_, _) => throw new HttpRequestException(error, "network"));

        Assert.Equal(expected, (await sender.SendAsync(Message())).Outcome);
    }

    // ---------------------------------------------------------- redaction

    [Fact]
    public async Task Logs_NeverContainTheCredentials_TheCode_TheMessage_TheFullNumber_OrTheResponseBody()
    {
        var log = new CapturingLogger<BroadnetSmsSender>();
        foreach (var scenario in new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
        {
            Reply(HttpStatusCode.OK, "OK msg42 to 971501234567"),
            Reply(HttpStatusCode.OK, "weird 971501234567 body"),
            Reply(HttpStatusCode.Forbidden, "denied for 971501234567"),
            Reply(HttpStatusCode.InternalServerError, "oops"),
            (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused https://sms.example.test/?pass=" + Password),
        })
        {
            var sender = new BroadnetSmsSender(
                new HttpClient(new StubHttpMessageHandler(scenario)), Options.Create(Configured()), log);
            await sender.SendAsync(Message());
        }

        var everything = string.Join("\n", log.Lines);
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(Password, everything);
        Assert.DoesNotContain("S3cret", everything);
        Assert.DoesNotContain("api-user", everything);
        Assert.DoesNotContain(Code, everything);
        Assert.DoesNotContain("Your code", everything);
        Assert.DoesNotContain("971501234567", everything);
        Assert.DoesNotContain("TIGER OTP", everything);
        Assert.DoesNotContain("sms.example.test", everything);
        Assert.DoesNotContain("msg42", everything);
        Assert.Contains("+971***567", everything);                    // only the masked form appears
    }

    [Fact]
    public async Task TheFakeSender_IsClearlyNotRealDelivery_AndLogsNoTextUnlessAskedTo()
    {
        var log = new CapturingLogger<FakeSmsSender>();
        var fake = new FakeSmsSender(Options.Create(new SmsOptions()), log);

        var result = await fake.SendAsync(Message());

        Assert.Equal(SmsSendOutcome.Accepted, result.Outcome);
        Assert.Contains(log.Lines, l => l.Contains("FAKE SMS (not sent)"));
        Assert.DoesNotContain(Code, string.Join("\n", log.Lines));
    }

    [Theory]
    [InlineData("Fake", "Development", false)]
    [InlineData("Fake", "Testing", false)]
    [InlineData("Fake", "Production", true)]
    [InlineData("Fake", "UAT", true)]
    [InlineData("fake", "Staging", true)]
    [InlineData("Broadnet", "Production", false)]
    [InlineData("Disabled", "Production", false)]
    public void TheFakeSender_IsRefusedOutsideDevelopmentAndTesting(string provider, string environment, bool unsafeCombination) =>
        Assert.Equal(unsafeCombination, SmsSafety.IsUnsafe(provider, environment));
}
