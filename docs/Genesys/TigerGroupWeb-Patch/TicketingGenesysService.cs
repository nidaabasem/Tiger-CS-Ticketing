using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tiger.TigerGroupWeb.Genesys
{
    public class TicketingGenesysService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly TicketingOptions _options;

        private const string TokenCacheKey = "TigerCS_Genesys_AccessToken";

        // One login at a time per process: the cache is per process too, so a
        // burst of calls with an empty cache makes ONE login, not one each.
        private static readonly SemaphoreSlim TokenLock = new(1, 1);

        // The login call is always bounded, even for callers that pass no token.
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(15);

        private static readonly TimeSpan TokenRenewMargin = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TokenMaximumLifetime = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan TokenMinimumLifetime = TimeSpan.FromSeconds(10);

        // A token must still be accepted for at least this long when it is issued, so that
        // it covers the forwarded call (ForwardBudget) and Ticketing's clock skew.
        private static readonly TimeSpan TokenMinimumUsableValidity = TimeSpan.FromSeconds(60);

        /// <summary>What to do with a freshly issued service token.</summary>
        public enum TokenUse
        {
            /// <summary>Use it and keep it in the cache for <see cref="TokenLifetime.CacheFor"/>.</summary>
            Cache,

            /// <summary>Valid for the call at hand but too close to expiry to be worth keeping: use it once, never cache it.</summary>
            UseOnce,

            /// <summary>Already expired, or valid for less than the minimum usable time: it is neither cached nor used.</summary>
            Reject
        }

        /// <summary>The decision <see cref="ComputeTokenLifetime"/> makes about a token.</summary>
        public readonly record struct TokenLifetime(TokenUse Use, TimeSpan CacheFor);

        /// <summary>
        /// One time budget for a forwarded Create/Update (and the OTP/document POSTs): TigerCS login, the call, buffering
        /// the response and the single authentication retry. When it runs out the call is cancelled (the controller answers 504).
        /// Keep it below the timeout configured on the Genesys data action.
        /// </summary>
        public TimeSpan ForwardBudget { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Decides what happens to a token whose login answer said it stops being accepted at <paramref name="expiresAtUtc"/>:
        /// <list type="bullet">
        /// <item><description>No expiry in the answer: cached for the previous fixed 20 minutes (the 401 refresh covers a wrong guess).</description></item>
        /// <item><description>Expired, or valid for less than 60 seconds: <see cref="TokenUse.Reject"/> - never cached, never used.</description></item>
        /// <item><description>Valid, but less than 2 minutes + 10 seconds remain: <see cref="TokenUse.UseOnce"/> - used for this call only, never cached.</description></item>
        /// <item><description>Otherwise: cached until 2 minutes before it expires, at most 20 minutes.</description></item>
        /// </list>
        /// </summary>
        public static TokenLifetime ComputeTokenLifetime(DateTimeOffset? expiresAtUtc, DateTimeOffset now)
        {
            if (expiresAtUtc is null)
            {
                return new TokenLifetime(TokenUse.Cache, TokenMaximumLifetime);
            }

            var remaining = expiresAtUtc.Value - now;

            if (remaining < TokenMinimumUsableValidity)
            {
                return new TokenLifetime(TokenUse.Reject, TimeSpan.Zero);
            }

            var cacheFor = remaining - TokenRenewMargin;

            if (cacheFor < TokenMinimumLifetime)
            {
                return new TokenLifetime(TokenUse.UseOnce, TimeSpan.Zero);
            }

            return new TokenLifetime(
                TokenUse.Cache,
                cacheFor > TokenMaximumLifetime ? TokenMaximumLifetime : cacheFor);
        }


        public TicketingGenesysService(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IOptions<TicketingOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _options = options.Value;
        }

        // =====================================================
        // GET TIGERCS ACCESS TOKEN
        // =====================================================
        private async Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(
                    TokenCacheKey,
                    out string? cachedToken) &&
                !string.IsNullOrWhiteSpace(cachedToken))
            {
                return cachedToken;
            }

            await TokenLock.WaitAsync(cancellationToken);

            try
            {
                // Another call may have logged in while this one waited.
                if (_cache.TryGetValue(
                        TokenCacheKey,
                        out cachedToken) &&
                    !string.IsNullOrWhiteSpace(cachedToken))
                {
                    return cachedToken;
                }

                // Credentials go to the no-redirect client: a 307/308 must never
                // re-send the username and password somewhere else. The login is
                // bounded by its own timeout and by the caller's token.
                var client = _httpClientFactory.CreateClient(NoRedirectClientName);
                client.Timeout = Timeout.InfiniteTimeSpan;

                using var loginBudget =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                loginBudget.CancelAfter(LoginTimeout);

                var loginUrl =
                    $"{_options.BaseUrl.TrimEnd('/')}/api/auth/login";

                var loginRequest = new
                {
                    username = _options.Username,
                    password = _options.Password
                };

                var json = JsonSerializer.Serialize(loginRequest);

                using var content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

                using var response = await client.PostAsync(
                    loginUrl,
                    content,
                    loginBudget.Token);

                var responseBody =
                    await response.Content.ReadAsStringAsync(loginBudget.Token);

                if (!response.IsSuccessStatusCode)
                {
                    // Status only: never the body, the username or the password.
                    throw new InvalidOperationException(
                        $"TigerCS authentication failed. " +
                        $"StatusCode: {(int)response.StatusCode}");
                }

                using var document =
                    JsonDocument.Parse(responseBody);

                var root = document.RootElement;

                string? token = null;

                // TigerCS answers { "accessToken": ..., "expiresAtUtc": ... };
                // the older property names stay accepted.
                if (root.TryGetProperty("token", out var tokenElement))
                {
                    token = tokenElement.GetString();
                }
                else if (root.TryGetProperty(
                             "accessToken",
                             out var accessTokenElement))
                {
                    token = accessTokenElement.GetString();
                }
                else if (root.TryGetProperty(
                             "jwtToken",
                             out var jwtTokenElement))
                {
                    token = jwtTokenElement.GetString();
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException(
                        "TigerCS login succeeded but access token was not found.");
                }

                // The field is UTC by name: a timestamp without an offset is read as UTC,
                // never as the server's local time. A missing or unreadable value means
                // "expiry unknown" (the previous 20-minute cache applies).
                DateTimeOffset? expiresAtUtc = null;
                if (root.TryGetProperty("expiresAtUtc", out var expiresElement) &&
                    expiresElement.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(
                        expiresElement.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedExpiry))
                {
                    expiresAtUtc = parsedExpiry;
                }

                var lifetime = ComputeTokenLifetime(expiresAtUtc, DateTimeOffset.UtcNow);

                if (lifetime.Use == TokenUse.Reject)
                {
                    // An expired (or nearly expired) token is neither cached nor sent.
                    throw new InvalidOperationException(
                        "TigerCS issued an access token that is already expired or too close to expiry to use.");
                }

                if (lifetime.Use == TokenUse.Cache)
                {
                    _cache.Set(TokenCacheKey, token, lifetime.CacheFor);
                }

                return token;
            }
            finally
            {
                TokenLock.Release();
            }
        }


        // =====================================================
        // CREATE AUTHENTICATED HTTP CLIENT
        // =====================================================
        private async Task<HttpClient> GetAuthenticatedClientAsync(
            CancellationToken cancellationToken = default)
        {
            var token =
                await GetAccessTokenAsync(cancellationToken);

            var client =
                _httpClientFactory.CreateClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    token);

            return client;
        }


        private void ClearToken()
        {
            _cache.Remove(TokenCacheKey);
        }

        // Drops the cached token only if it is still the one that was refused,
        // so a newer token another call just installed is never thrown away.
        private void ClearToken(string refusedToken)
        {
            if (_cache.TryGetValue(TokenCacheKey, out string? currentToken) &&
                string.Equals(currentToken, refusedToken, StringComparison.Ordinal))
            {
                ClearToken();
            }
        }


        // =====================================================
        // 1. CUSTOMER LOOKUP
        // =====================================================
        public async Task<HttpResponseMessage> CustomerLookupAsync(
            string phoneNumber)
        {
            var encodedPhone =
                Uri.EscapeDataString(phoneNumber);

            var url =
                $"{_options.BaseUrl.TrimEnd('/')}" +
                "/api/genesys/customers/lookup" +
                $"?phoneNumber={encodedPhone}";

            var client =
                await GetAuthenticatedClientAsync();

            var response =
                await client.GetAsync(url);

            // Token expired → renew and retry once
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();

                ClearToken();

                client =
                    await GetAuthenticatedClientAsync();

                response =
                    await client.GetAsync(url);
            }

            return response;
        }


        // =====================================================
        // 2. CREATE / REUSE TICKET
        //
        // The body is forwarded exactly as received. Idempotent in
        // TigerCS (one ticket per conversationId), but never replayed
        // after a timeout or a network failure: the caller decides.
        // =====================================================
        public Task<HttpResponseMessage> CreateTicketAsync(
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            return ForwardJsonAsync(
                HttpMethod.Post,
                "/api/genesys/tickets",
                requestJson,
                idempotencyKey: null,
                cancellationToken);
        }


        // =====================================================
        // 3. UPDATE TICKET
        //
        // PATCH carries requestType, transcript, handoff, routing,
        // ended ... unchanged; TigerCS validates and applies them.
        // =====================================================
        public Task<HttpResponseMessage> UpdateTicketAsync(
            long ticketId,
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            return ForwardJsonAsync(
                HttpMethod.Patch,
                $"/api/genesys/tickets/{ticketId.ToString(CultureInfo.InvariantCulture)}",
                requestJson,
                idempotencyKey: null,
                cancellationToken);
        }


        // =====================================================
        // 4. SECURE SCREEN POP
        // =====================================================
        public async Task<HttpResponseMessage> ScreenPopAsync(
            string requestJson)
        {
            var url =
                $"{_options.BaseUrl.TrimEnd('/')}" +
                "/api/genesys/screen-pop";

            var client =
                await GetAuthenticatedClientAsync();

            using var content =
                new StringContent(
                    requestJson,
                    Encoding.UTF8,
                    "application/json");

            var response =
                await client.PostAsync(
                    url,
                    content);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();

                ClearToken();

                client =
                    await GetAuthenticatedClientAsync();

                using var retryContent =
                    new StringContent(
                        requestJson,
                        Encoding.UTF8,
                        "application/json");

                response =
                    await client.PostAsync(
                        url,
                        retryContent);
            }

            return response;
        }


        // =====================================================
        // 5. COLLECTIONS: PAYMENT SUMMARY
        // =====================================================
        public Task<HttpResponseMessage> CollectionsPaymentSummaryAsync(
            string encodedCustomerKey,
            string queryString,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            return CollectionsGetAsync(
                $"/api/genesys/collections/customers/by-key/{encodedCustomerKey}/payment-summary{queryString}",
                budget,
                cancellationToken);
        }


        // =====================================================
        // 6. COLLECTIONS: PAYMENT TRANSACTIONS
        // =====================================================
        public Task<HttpResponseMessage> CollectionsPaymentTransactionsAsync(
            string encodedCustomerKey,
            string queryString,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            return CollectionsGetAsync(
                $"/api/genesys/collections/customers/by-key/{encodedCustomerKey}/payment-transactions{queryString}",
                budget,
                cancellationToken);
        }


        // Unit-based read: the downstream route belongs to /api/collections.
        // Uses the same service-account token, deadline and one 401 retry.
        public Task<HttpResponseMessage> CollectionsUnitPaymentSummaryAsync(
            string unitCode,
            int? companyId,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            var query = "?unitCode=" + Uri.EscapeDataString(unitCode);
            if (companyId.HasValue)
            {
                query += "&companyId=" + companyId.Value.ToString(CultureInfo.InvariantCulture);
            }

            return CollectionsGetAsync(
                "/api/collections/units/payment-summary" + query,
                budget,
                cancellationToken);
        }


        // The path and query arrive already encoded and are
        // forwarded as-is (Uri keeps reserved escapes like %3A).
        //
        // One budget covers the TigerCS login, the call and the 401
        // retry. When it runs out the call is cancelled (the controller
        // answers 504); a Genesys disconnect cancels it the same way.
        private async Task<HttpResponseMessage> CollectionsGetAsync(
            string pathAndQuery,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            var url = new Uri(
                $"{_options.BaseUrl.TrimEnd('/')}{pathAndQuery}");

            using var budgetSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            budgetSource.CancelAfter(budget);

            var clock = Stopwatch.StartNew();

            var response =
                await SendCollectionsGetAsync(url, budget, clock, budgetSource.Token);

            // Token expired → renew and retry once, within the same budget
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();

                ClearToken();

                response =
                    await SendCollectionsGetAsync(url, budget, clock, budgetSource.Token);
            }

            return response;
        }


        private async Task<HttpResponseMessage> SendCollectionsGetAsync(
            Uri url,
            TimeSpan budget,
            Stopwatch clock,
            CancellationToken cancellationToken)
        {
            var client =
                await GetAuthenticatedClientAsync(cancellationToken);

            // The budget's token bounds the call, not HttpClient.Timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;

            using var request =
                new HttpRequestMessage(HttpMethod.Get, url);

            // TigerCS must answer before this budget runs out, so it is
            // given what is left (after the login, to the nearest second)
            // less a margin: at least 2.5 s stays for the response.
            var tigerCsSeconds = Math.Max(
                1,
                (int)Math.Round((budget - clock.Elapsed).TotalSeconds, MidpointRounding.AwayFromZero)
                    - TicketingOptions.TigerCsMarginSeconds);

            request.Headers.TryAddWithoutValidation(
                TicketingOptions.TigerCsDeadlineHeader,
                tigerCsSeconds.ToString(CultureInfo.InvariantCulture));

            return await client.SendAsync(request, cancellationToken);
        }


        // =====================================================
        // 7. COLLECTIONS: OTHER READS
        //
        // The path after /api/genesys/collections/ arrives already
        // encoded (and validated by the controller); the query is
        // forwarded as-is. Same budget and 401 handling as above.
        // =====================================================
        public Task<HttpResponseMessage> CollectionsReadAsync(
            string encodedPathAfterCollections,
            string queryString,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            return CollectionsGetAsync(
                $"/api/genesys/collections/{encodedPathAfterCollections}{queryString}",
                budget,
                cancellationToken);
        }


        // =====================================================
        // 8. AGENT CONTEXT AND UNIT DETAILS
        // =====================================================
        public Task<HttpResponseMessage> AgentContextAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/agent-context",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> UnitDetailsAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/customers/unit-details",
                requestJson, idempotencyKey, cancellationToken);
        }


        // =====================================================
        // 9. CRM BUYER VERIFICATION AND DOCUMENT COPIES
        //
        // Uses the SAME TicketingOptions, service account and
        // GetAccessTokenAsync cache as the existing operations.
        // =====================================================
        public Task<HttpResponseMessage> BuyerLookupAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/verification/buyer-lookup",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> SendOtpAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/verification/otp/send",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> ResendOtpAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/verification/otp/resend",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> VerifyOtpAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/verification/otp/verify",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> SendDocumentCopyAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/documents/send-copy",
                requestJson, idempotencyKey, cancellationToken);
        }


        // =====================================================
        // 10. COLLECTIONS: REMINDERS (WRITES)
        //
        // TigerCS gates these on Collections:Enabled and on the
        // service account's Collections grants; forwarding them
        // switches nothing on.
        // =====================================================
        public Task<HttpResponseMessage> QueueCollectionsReminderAsync(
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                "/api/genesys/collections/reminders",
                requestJson, idempotencyKey, cancellationToken);
        }

        public Task<HttpResponseMessage> RecordCollectionsReminderOutcomeAsync(
            string encodedReminderId,
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardPostAsync(
                $"/api/genesys/collections/reminders/{encodedReminderId}/outcomes",
                requestJson, idempotencyKey, cancellationToken);
        }


        // Requests that carry OTPs, verification-session ids or document
        // choices: no redirects and no cookies (the named client is set up
        // in Program.cs); authentication uses the existing helper.
        public const string NoRedirectClientName = "TigerCS-NoRedirect";

        private Task<HttpResponseMessage> ForwardPostAsync(
            string path,
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            return ForwardJsonAsync(
                HttpMethod.Post, path, requestJson, idempotencyKey, cancellationToken);
        }

        // The one safe forwarding pattern for JSON writes (POST and PATCH).
        private async Task<HttpResponseMessage> ForwardJsonAsync(
            HttpMethod method,
            string path,
            string requestJson,
            string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            var url = new Uri($"{_options.BaseUrl.TrimEnd('/')}{path}");

            var client = _httpClientFactory.CreateClient(NoRedirectClientName);

            // The budget below bounds the call, not HttpClient.Timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;

            // One budget (ForwardBudget, 30 s by default) covers login, request,
            // response buffering and the single authentication retry.
            using var budgetSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetSource.CancelAfter(ForwardBudget);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await GetAccessTokenAsync(budgetSource.Token);

                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(
                    requestJson, Encoding.UTF8, "application/json");

                if (idempotencyKey is not null)
                {
                    request.Headers.TryAddWithoutValidation(
                        "Idempotency-Key", idempotencyKey);
                }

                // A new request/content is constructed on each attempt.
                // ResponseContentRead keeps body download inside the budget.
                var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, budgetSource.Token);

                var authenticationFailed =
                    response.StatusCode == HttpStatusCode.Unauthorized &&
                    response.Headers.WwwAuthenticate.Any(challenge =>
                        string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));

                // Refresh only for an authentication challenge, once.
                // Never retry a business error, a timeout or a network failure.
                if (attempt == 0 && authenticationFailed)
                {
                    response.Dispose();

                    // Keep a newer cached token installed by another call.
                    ClearToken(token);

                    continue;
                }

                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    response.Dispose();
                    throw new HttpRequestException("Unexpected redirect from TigerCS Ticketing.");
                }

                return response;
            }

            throw new InvalidOperationException("TigerCS authentication retry did not complete.");
        }
    }
}
