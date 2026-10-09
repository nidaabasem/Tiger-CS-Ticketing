using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Integrations.Modules.CollectionsIntegration;

public interface IGenesysTokenProvider
{
    /// <summary>A valid bearer token, cached until shortly before its stated expiry.</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached token if it is still <paramref name="rejectedToken"/> (Genesys answered 401).</summary>
    void Invalidate(string rejectedToken);
}

public sealed class GenesysAuthenticationException(string message) : Exception(message);

/// <summary>
/// OAuth client-credentials token for Genesys Cloud, requested server-side and cached by its <c>expires_in</c>.
/// The client id, secret and token are never logged or included in exception messages.
/// </summary>
public sealed class GenesysTokenProvider(HttpClient http, GenesysOutboundOptions options, TimeProvider time,
    ILogger<GenesysTokenProvider> logger) : IGenesysTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryCached(out var cached)) return cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryCached(out cached)) return cached;
            if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new GenesysAuthenticationException("The Genesys client credentials are not configured.");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.LoginBaseUrl.TrimEnd('/') + "/"), "oauth/token"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}")));
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, cancellationToken); }
            catch (HttpRequestException) { throw new GenesysAuthenticationException("The Genesys login service could not be reached."); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Genesys token request was refused with HTTP {Status}.", (int)response.StatusCode);
                    throw new GenesysAuthenticationException($"The Genesys login service refused the credentials (HTTP {(int)response.StatusCode}).");
                }
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = json.RootElement;
                var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 0;
                if (string.IsNullOrEmpty(token) || seconds <= 0)
                    throw new GenesysAuthenticationException("The Genesys login service returned an unusable token response.");
                _token = token;
                // Refresh a little early; never cache longer than the stated lifetime.
                _expiresAt = time.GetUtcNow().AddSeconds(Math.Max(1, seconds - Math.Max(0, options.TokenExpirySkewSeconds)));
                return token;
            }
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(string rejectedToken)
    {
        if (string.Equals(_token, rejectedToken, StringComparison.Ordinal)) { _token = null; _expiresAt = default; }
    }

    private bool TryCached(out string token)
    {
        var current = _token;
        if (current is not null && time.GetUtcNow() < _expiresAt) { token = current; return true; }
        token = "";
        return false;
    }
}

/// <summary>
/// Posts contacts to one Genesys outbound contact list. Every answer is classified as accepted (ids returned), rejected
/// (Genesys certainly did not create the contacts, so a retry is safe) or unknown (the request may have been processed:
/// timeouts, dropped connections, 5xx). Unknown outcomes must never be resent blindly.
/// </summary>
public sealed class GenesysOutboundHttpClient(HttpClient http, IGenesysTokenProvider tokens, GenesysOutboundOptions options,
    ILogger<GenesysOutboundHttpClient> logger) : IGenesysOutboundClient
{
    public async Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct)
    {
        if (contacts.Count is 0 or > GenesysOutboundOptions.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(contacts), "A request must contain 1-1,000 contacts.");
        if (contacts.Any(c => !string.Equals(c.ContactListId, contactListId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Every contact must carry the same contactListId as the request URL.");

        var body = JsonSerializer.Serialize(contacts.Select(c => new Dictionary<string, object?>
        {
            ["contactListId"] = c.ContactListId,
            ["data"] = new Dictionary<string, string>
            {
                ["Phone"] = c.Phone, ["CustomerName"] = c.CustomerName, ["Email Address"] = c.EmailAddress,
                ["ReminderType"] = c.ReminderType, ["AmountDue"] = c.AmountDue, ["DueDate"] = c.DueDate
            },
            ["callable"] = c.Callable
        }));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            string token;
            try { token = await tokens.GetTokenAsync(ct); }
            catch (GenesysAuthenticationException ex) { return new(GenesysUploadOutcome.Rejected, null, [], ex.Message); }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(options.ApiBaseUrl.TrimEnd('/') + "/"), $"api/v2/outbound/contactlists/{contactListId}/contacts"))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
            { return new(GenesysUploadOutcome.Rejected, null, [], "Genesys could not be reached; nothing was sent."); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("Genesys upload ended without a response ({ExceptionType}); the outcome is unknown.", ex.GetType().Name);
                return new(GenesysUploadOutcome.Unknown, null, [], "The request timed out or the connection dropped before Genesys answered.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { tokens.Invalidate(token); continue; }
                if (response.IsSuccessStatusCode) return new(GenesysUploadOutcome.Accepted, status, await ReadIdsAsync(response, ct), null);
                if (status >= 500) return new(GenesysUploadOutcome.Unknown, status, [], $"Genesys answered HTTP {status}; the contacts may or may not have been created.");
                var detail = status == 429 ? "Genesys rate limit reached; nothing was created." : $"Genesys rejected the request (HTTP {status}); nothing was created.";
                return new(GenesysUploadOutcome.Rejected, status, [], detail);
            }
        }
        return new(GenesysUploadOutcome.Rejected, 401, [], "Genesys rejected the access token twice; nothing was created.");
    }

    private static async Task<IReadOnlyList<string>> ReadIdsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
            return json.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "").ToList();
        }
        catch (JsonException) { return []; }
    }
}
