using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.Otp;

namespace TigerCS.Integrations.Modules.SmsIntegration;

/// <summary>
/// <b>Local verification only — no SMS is ever sent.</b> Records messages in memory so tests and
/// developers can read the code, and answers with a scripted outcome (default Accepted).
/// Registered only for <c>Sms:Provider=Fake</c>, which <see cref="SmsSafety"/> refuses outside
/// Development/Testing.
/// </summary>
public sealed class FakeSmsSender(IOptions<SmsOptions> options, ILogger<FakeSmsSender> logger) : ISmsSender
{
    private readonly ConcurrentQueue<SmsSendResult> _script = new();

    public bool IsConfigured => true;

    public ConcurrentQueue<SmsMessage> Sent { get; } = new();

    public SmsSendResult Default { get; set; } = new(SmsSendOutcome.Accepted, "fake-ref");

    public Exception? Throws { get; set; }

    /// <summary>Queue the outcome of the next send(s).</summary>
    public FakeSmsSender Then(SmsSendOutcome outcome)
    {
        _script.Enqueue(new SmsSendResult(outcome));
        return this;
    }

    public SmsMessage? Last => Sent.LastOrDefault();

    /// <summary>The digits of the most recent message's code (the first run of digits).</summary>
    public string? LastCode => Last is null ? null : System.Text.RegularExpressions.Regex.Match(Last.Text, @"\d{4,9}").Value;

    public Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        Sent.Enqueue(message);
        if (options.Value.Fake.LogMessageText)
        {
            logger.LogInformation("FAKE SMS (not sent) to {Destination}: {Text}", Mask(message.Destination), message.Text);
        }
        else
        {
            logger.LogInformation("FAKE SMS (not sent) to {Destination}.", Mask(message.Destination));
        }

        return Task.FromResult(_script.TryDequeue(out var scripted) ? scripted : Default);
    }

    private static string Mask(string digits) => digits.Length <= 6 ? "***" : $"{digits[..3]}***{digits[^3..]}";
}
