using TigerCS.Application.Modules.CustomerVerification.Otp;

namespace TigerCS.Integrations.Modules.SmsIntegration;

/// <summary>Default sender: nothing is configured, nothing is sent, and the OTP flow says so before creating a challenge.</summary>
public sealed class DisabledSmsSender : ISmsSender
{
    public bool IsConfigured => false;

    public Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SmsSendResult(SmsSendOutcome.Failed));
}
