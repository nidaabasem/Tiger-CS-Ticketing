using TigerCS.Application.Modules.Notifications.Abstractions;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>
/// Scriptable <see cref="IEmailSender"/>. Every outcome the boundary defines
/// can be produced deterministically, including "throws instead of
/// classifying" — the case a real adapter is not supposed to produce but
/// might.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly Queue<Func<EmailMessage, EmailSendResult>> _scripted = new();

    public List<EmailMessage> Sent { get; } = [];

    /// <summary>Outcome once the scripted queue is exhausted.</summary>
    public EmailSendResult DefaultResult { get; set; } = EmailSendResult.Sent();

    /// <summary>When set, the next send throws this instead of returning — an adapter that fails to classify.</summary>
    public Exception? ThrowOnNextSend { get; set; }

    public int SendCount => Sent.Count;

    public FakeEmailSender ThenTransient(string error = "Simulated transient provider failure.")
    {
        _scripted.Enqueue(_ => EmailSendResult.Transient(error));
        return this;
    }

    public FakeEmailSender ThenPermanent(string error = "Simulated permanent provider rejection.")
    {
        _scripted.Enqueue(_ => EmailSendResult.Permanent(error));
        return this;
    }

    public FakeEmailSender ThenSent()
    {
        _scripted.Enqueue(_ => EmailSendResult.Sent());
        return this;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextSend is { } toThrow)
        {
            ThrowOnNextSend = null;

            // Recorded before throwing: the point of this case is that the
            // provider may well have accepted the message before failing, so
            // a test asserting "not sent twice" must count this attempt.
            Sent.Add(message);
            throw toThrow;
        }

        Sent.Add(message);

        var result = _scripted.Count > 0 ? _scripted.Dequeue()(message) : DefaultResult;
        return Task.FromResult(result);
    }
}
