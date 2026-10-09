using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.Otp;

namespace TigerCS.Integrations.Modules.SmsIntegration;

/// <summary>
/// Broadnet web-SMS adapter. Request shape (from the supplied integration snippet; the provider's own
/// contract is still to be confirmed): <c>GET {Endpoint}?user=&amp;pass=&amp;sid=&amp;mno=&amp;type=&amp;text=</c>.
///
/// <para>
/// <b>Never reports success it cannot verify.</b> <c>Accepted</c> only for an HTTP 2xx whose body matches
/// the configured <see cref="BroadnetSmsOptions.SuccessBodyPattern"/> and not the failure pattern.
/// A 2xx that matches neither, a timeout, or a connection that failed after the request may have been
/// sent is <c>Unconfirmed</c>; explicit refusals are <c>Rejected</c>; a request that provably never
/// reached the provider, a 5xx or a 429 is <c>Failed</c>. There is no retry here.
/// </para>
///
/// <para>
/// <b>Secrets and content stay out of logs and errors:</b> every query value is percent-encoded
/// (<see cref="Uri.EscapeDataString(string)"/>), the URL, the message text, the credentials and the response
/// body are never logged, and the HTTP client is registered without request logging (which would print the
/// full URL, password included). Only the outcome, the HTTP status and a masked destination are logged.
/// </para>
/// </summary>
public sealed class BroadnetSmsSender(HttpClient httpClient, IOptions<SmsOptions> options, ILogger<BroadnetSmsSender> logger) : ISmsSender
{
    private const int MaxBodyChars = 4096;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private BroadnetSmsOptions Settings => options.Value.Broadnet;

    public bool IsConfigured => ValidationProblem() is null;

    /// <summary>Names the first missing/invalid setting (never its value), or null when complete. Used by the startup log and tests.</summary>
    public string? ValidationProblem()
    {
        var s = Settings;
        if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.Query))
        {
            return "Sms:Broadnet:Endpoint (absolute https URL without a query string)";
        }

        if (string.IsNullOrWhiteSpace(s.User)) return "Sms:Broadnet:User";
        if (string.IsNullOrWhiteSpace(s.Password)) return "Sms:Broadnet:Password";
        if (string.IsNullOrWhiteSpace(s.SenderId)) return "Sms:Broadnet:SenderId";
        if (string.IsNullOrWhiteSpace(s.TypeEnglish)) return "Sms:Broadnet:TypeEnglish";
        if (string.IsNullOrWhiteSpace(s.TypeArabic)) return "Sms:Broadnet:TypeArabic";
        if (s.MobileFormat == MobileNumberFormat.Unset) return "Sms:Broadnet:MobileFormat";
        if (!TryRegex(s.SuccessBodyPattern, required: true, out _)) return "Sms:Broadnet:SuccessBodyPattern (valid regex)";
        if (!TryRegex(s.FailureBodyPattern, required: false, out _)) return "Sms:Broadnet:FailureBodyPattern (valid regex)";
        return null;
    }

    public async Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        if (ValidationProblem() is not null)
        {
            // Defence in depth: the OTP flow checks IsConfigured first.
            return new SmsSendResult(SmsSendOutcome.Failed);
        }

        var s = Settings;
        var masked = Mask(message.Destination);
        var url = BuildUrl(s, message);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 1, 60)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);

            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                var body = await ReadBodyAsync(response, budget.Token);
                return Classify2xx(body, status, masked);
            }

            var outcome = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => SmsSendOutcome.Failed,
                HttpStatusCode.RequestTimeout => SmsSendOutcome.Unconfirmed,
                _ when status >= 500 => SmsSendOutcome.Failed,
                _ when status >= 300 && status < 400 => SmsSendOutcome.Unconfirmed, // redirects are never followed
                _ => SmsSendOutcome.Rejected
            };
            logger.LogWarning("SMS provider answered HTTP {Status} for {Destination}: {Outcome}.", status, masked, outcome);
            return new SmsSendResult(outcome);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our budget expired: the provider may have sent it. Ambiguous — never retried here.
            logger.LogWarning("SMS provider timed out for {Destination}; delivery is unconfirmed.", masked);
            return new SmsSendResult(SmsSendOutcome.Unconfirmed);
        }
        catch (HttpRequestException ex)
        {
            // Only errors that happen before anything is sent prove non-delivery.
            var neverSent = ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;
            var outcome = neverSent ? SmsSendOutcome.Failed : SmsSendOutcome.Unconfirmed;
            logger.LogWarning("SMS provider call failed for {Destination} ({Error}): {Outcome}.", masked, ex.HttpRequestError, outcome);
            return new SmsSendResult(outcome);
        }
    }

    private SmsSendResult Classify2xx(string body, int status, string masked)
    {
        TryRegex(Settings.FailureBodyPattern, required: false, out var failure);
        TryRegex(Settings.SuccessBodyPattern, required: true, out var success);

        try
        {
            if (failure is not null && failure.IsMatch(body))
            {
                logger.LogWarning("SMS provider reported failure (HTTP {Status}) for {Destination}.", status, masked);
                return new SmsSendResult(SmsSendOutcome.Rejected);
            }

            var match = success!.Match(body);
            if (match.Success)
            {
                var reference = match.Groups["ref"] is { Success: true } g ? g.Value : null;
                logger.LogInformation("SMS provider accepted the message for {Destination}.", masked);
                return new SmsSendResult(SmsSendOutcome.Accepted, reference);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // fall through to unconfirmed
        }

        logger.LogWarning("SMS provider answered HTTP {Status} with an unrecognised body for {Destination}; delivery is unconfirmed.", status, masked);
        return new SmsSendResult(SmsSendOutcome.Unconfirmed);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxBodyChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, read);
    }

    /// <summary>Every value is percent-encoded, so Arabic text, spaces, '&amp;' and '#' cannot break or inject parameters.</summary>
    internal static string BuildUrl(BroadnetSmsOptions s, SmsMessage message)
    {
        var mobile = s.MobileFormat == MobileNumberFormat.PlusInternational ? "+" + message.Destination : message.Destination;
        var type = string.Equals(message.Language, "ar", StringComparison.OrdinalIgnoreCase) ? s.TypeArabic : s.TypeEnglish;
        return s.Endpoint
            + "?user=" + Uri.EscapeDataString(s.User)
            + "&pass=" + Uri.EscapeDataString(s.Password)
            + "&sid=" + Uri.EscapeDataString(s.SenderId)
            + "&mno=" + Uri.EscapeDataString(mobile)
            + "&type=" + Uri.EscapeDataString(type)
            + "&text=" + Uri.EscapeDataString(message.Text);
    }

    private static bool TryRegex(string pattern, bool required, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return !required;
        }

        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Mask(string digits) => digits.Length <= 6 ? "***" : $"+{digits[..3]}***{digits[^3..]}";
}
