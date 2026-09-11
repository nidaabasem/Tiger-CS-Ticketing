using System.Net.Mail;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Email;

/// <summary>
/// <see cref="SmtpEmailSender"/> with the transport faked: message
/// construction, failure classification, and what may reach a log line.
/// No test here opens a socket.
/// </summary>
public class SmtpEmailSenderTests
{
    private const string SecretPassword = "Sup3r-Secret-Smtp-P@ssw0rd";

    private sealed class FakeSmtpTransport : ISmtpTransport
    {
        public List<MailMessage> Sent { get; } = [];
        public Exception? ThrowNext { get; set; }

        public Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            if (ThrowNext is { } toThrow)
            {
                ThrowNext = null;
                throw toThrow;
            }

            // Materialise the parts we assert on before the caller disposes the message.
            Sent.Add(new MailMessage(message.From!.Address, message.To[0].Address, message.Subject, message.Body)
            {
                From = message.From
            });
            foreach (var view in message.AlternateViews)
            {
                Sent[^1].AlternateViews.Add(view);
            }

            return Task.CompletedTask;
        }
    }

    private static EmailNotificationOptions Options() => new()
    {
        Enabled = true,
        Provider = "Smtp",
        Username = "no_reply@example.com",
        Password = SecretPassword,
        FromEmail = "no_reply@example.com",
        FromName = "Tiger Properties"
    };

    private static (SmtpEmailSender Sender, FakeSmtpTransport Transport, CapturingLogger<SmtpEmailSender> Log) Create(
        EmailNotificationOptions? options = null)
    {
        var transport = new FakeSmtpTransport();
        var log = new CapturingLogger<SmtpEmailSender>();
        return (new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(options ?? Options()), transport, log), transport, log);
    }

    private static EmailMessage Message(string? html = "<p>Hello</p>") =>
        new("customer@example.com", "Subject", "Hello", Guid.NewGuid(), html);

    [Fact]
    public async Task Send_BuildsAMultipartMessageFromTheConfiguredSender()
    {
        var (sender, transport, _) = Create();

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.Sent, result.Outcome);
        var mail = Assert.Single(transport.Sent);
        Assert.Equal("no_reply@example.com", mail.From!.Address);
        Assert.Equal("Tiger Properties", mail.From.DisplayName);
        Assert.Equal("customer@example.com", mail.To[0].Address);
        Assert.Equal("Subject", mail.Subject);
        Assert.Equal("Hello", mail.Body);
        Assert.Single(mail.AlternateViews);
    }

    [Fact]
    public void BuildMailMessage_IsPlainTextOnlyWhenNoHtmlIsSupplied()
    {
        using var mail = SmtpEmailSender.BuildMailMessage(Message(html: null), "no_reply@example.com", "Tiger Properties");

        Assert.False(mail.IsBodyHtml);
        Assert.Empty(mail.AlternateViews);
        Assert.NotNull(mail.Headers["X-TigerCS-Correlation-Id"]);
    }

    [Theory]
    [InlineData(SmtpStatusCode.MailboxUnavailable)]
    [InlineData(SmtpStatusCode.MailboxNameNotAllowed)]
    [InlineData(SmtpStatusCode.ClientNotPermitted)]
    [InlineData(SmtpStatusCode.MustIssueStartTlsFirst)]
    [InlineData(SmtpStatusCode.TransactionFailed)]
    public async Task PermanentSmtpStatus_IsClassifiedPermanent(SmtpStatusCode status)
    {
        var (sender, transport, _) = Create();
        transport.ThrowNext = new SmtpException(status, $"server said no; password={SecretPassword}");

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Contains(status.ToString(), result.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SmtpStatusCode.ServiceNotAvailable)]
    [InlineData(SmtpStatusCode.MailboxBusy)]
    [InlineData(SmtpStatusCode.InsufficientStorage)]
    [InlineData(SmtpStatusCode.GeneralFailure)]
    public async Task TransientSmtpStatus_IsClassifiedTransient(SmtpStatusCode status)
    {
        var (sender, transport, _) = Create();
        transport.ThrowNext = new SmtpException(status, "try again later");

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task RejectedRecipient_IsPermanent()
    {
        var (sender, transport, _) = Create();
        transport.ThrowNext = new SmtpFailedRecipientException(SmtpStatusCode.MailboxUnavailable, "customer@example.com");

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task SocketOrTlsFailure_IsTransient()
    {
        var (sender, transport, _) = Create();
        transport.ThrowNext = new IOException("connection reset");

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.TransientFailure, result.Outcome);
        Assert.Contains(nameof(IOException), result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorsAndLogs_NeverCarryThePasswordTheBodyOrTheFullRecipient()
    {
        var (sender, transport, log) = Create();
        transport.ThrowNext = new SmtpException(SmtpStatusCode.ClientNotPermitted, $"535 auth failed for password {SecretPassword}");

        var result = await sender.SendAsync(Message());

        Assert.DoesNotContain(SecretPassword, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("auth failed", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", result.Error!, StringComparison.Ordinal);
        foreach (var line in log.Messages)
        {
            Assert.DoesNotContain(SecretPassword, line, StringComparison.Ordinal);
            Assert.DoesNotContain("customer@example.com", line, StringComparison.Ordinal);
            Assert.Contains("c***@example.com", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MissingFromEmail_IsPermanentConfigurationFailure()
    {
        var options = Options();
        options.FromEmail = null;
        var (sender, transport, _) = Create(options);

        var result = await sender.SendAsync(Message());

        Assert.Equal(EmailSendOutcome.PermanentFailure, result.Outcome);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Validate_NamesEveryMissingKeyAndNeverAValue()
    {
        var options = new EmailNotificationOptions
        {
            Enabled = true, Provider = "Smtp", SmtpHost = "", SmtpPort = 0, Username = null, Password = null, FromEmail = "nope"
        };

        var errors = EmailSenderSafety.Validate(options);

        Assert.Contains(errors, e => e.Contains("EmailNotifications:SmtpHost", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("EmailNotifications:SmtpPort", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("EmailNotifications:Username", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("EmailNotifications:Password", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("EmailNotifications:FromEmail", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_IsSilentWhenDisabledOrRecording()
    {
        Assert.Empty(EmailSenderSafety.Validate(new EmailNotificationOptions { Enabled = false }));
        Assert.Empty(EmailSenderSafety.Validate(new EmailNotificationOptions { Enabled = true, Provider = "Recording" }));
        Assert.Empty(EmailSenderSafety.Validate(Options()));
    }

    [Fact]
    public void Validate_RejectsAnUnknownProvider()
    {
        var errors = EmailSenderSafety.Validate(new EmailNotificationOptions { Enabled = true, Provider = "Graph" });

        Assert.Single(errors);
        Assert.Contains("EmailNotifications:Provider", errors[0], StringComparison.Ordinal);
    }
}
