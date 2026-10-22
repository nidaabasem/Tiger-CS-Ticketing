# TigerGroupWeb — Genesys ticket forwarding hardening patch

Patch for the **existing** `GenesysController` / `TicketingGenesysService` that you supplied. No controller, route or class is added; the public URLs, verbs,
`[Authorize(Policy = GenesysAuthDefaults.PolicyName)]` and the request/response contracts are unchanged.

> **Verification status — read first.** The full TigerGroupWeb project (Program.cs, the real `TicketingOptions`, `GenesysAuthDefaults`, the OAuth token endpoint, logging
> configuration) and a real Ticketing API were **not available**. The two patched files were compiled (warnings as errors, .NET 10) and the 54 tests in
> [`tests/TicketingGenesysForwardingTests.cs`](tests/TicketingGenesysForwardingTests.cs) pass **only inside an isolated harness** ([`harness/`](harness)) that uses
> *stand-ins* for those two types and a *scripted fake* of Ticketing. **This is not a TigerGroupWeb build and not an integration test.** Build and run it in the real
> project before deploying. Four deliberate mutations of the patch (log the exception message, drop the login lock, clear the token unconditionally, refresh on any 401)
> each made a test fail, so the tests do detect those regressions.

Files: `TicketingGenesysService.cs`, `GenesysController.cs` (complete patched files, UTF-8 BOM + CRLF preserved), `*.diff` (apply with `patch -p1 --binary`; verified to reproduce the patched files exactly from the originals you sent), `tests/`, `harness/`.

## 1. Existing implementation (from the two files)

| Public route (unchanged) | Controller action | Service method | Forwarding pattern before the patch |
|---|---|---|---|
| `POST /api/genesys/tickets` | `CreateTicket` | `CreateTicketAsync` | legacy: default client, no cancellation/budget, retry on **any** 401 |
| `PATCH /api/genesys/tickets/{ticketId:long}` | `UpdateTicket` | `UpdateTicketAsync` | legacy, same |
| `GET customers/lookup`, `POST screen-pop` | `CustomerLookup`, `ScreenPop` | `CustomerLookupAsync`, `ScreenPopAsync` | legacy (untouched) |
| `agent-context`, `customers/unit-details`, `verification/*`, `documents/send-copy`, `collections/reminders*` | `ForwardPost` | `ForwardPostAsync` | **safe pattern**: no-redirect client, 30 s budget, refresh once on a Bearer challenge only, bytes + Retry-After relayed |
| `collections/*` reads | `ForwardCollectionsRead` | `CollectionsGetAsync` | budget + deadline header, retry on any 401 (untouched) |

`requestType` and `transcript` already pass through: both actions bind `JsonElement` and forward `GetRawText()`, which is the body exactly as received (whitespace, `\u` escapes and numbers preserved — test `Update_ForwardsPatchBodyWithRequestTypeAndTranscriptByteForByte_WithTheServiceToken`). Nothing in TigerGroupWeb needs to know those fields exist.

## 2. Findings

### Confirmed issues (fixed by this patch)

| # | Issue (evidence in the supplied code) | Fix |
|---|---|---|
| C1 | Create/Update have **no cancellation token and no time budget** (HttpClient default timeout applies); a Genesys disconnect does not cancel; the controller's blanket `catch (Exception)` reports a timeout as **502** and logs a client abort as an error. | Both now go through `ForwardJsonAsync`: one `ForwardBudget` (30 s) covers login + call + response read + the single auth retry; caller disconnect cancels; timeout → **504** `{message}`, other failures → **502** with the **unchanged** message. |
| C2 | Create/Update (and Lookup/ScreenPop/Collections) retry on **every** 401, not only a Bearer authentication challenge. | Create/Update refresh **once, only** when 401 carries `WWW-Authenticate: Bearer`; a business 401 is relayed untouched. |
| C3 | `ClearToken()` removes the cached token unconditionally — a call holding a stale token can discard the fresh one another call just installed. | New `ClearToken(refusedToken)` removes it only if it is still the refused one (already how `ForwardPostAsync` behaved inline). |
| C4 | Token cached a fixed 20 minutes regardless of the JWT's real expiry (login answers `expiresAtUtc`; Ticketing's default is 60 min but `Jwt:ExpirationMinutes` is configurable). If it is set below 20, every call after expiry costs a 401 round trip. | `ComputeTokenLifetime` returns a decision: **cache** until `expiresAtUtc` − 2 min (≤ 20 min as before, ≥ 10 s); **use once, never cache** when it is valid but less than 2 min 10 s remain; **reject** (never cached, never sent; the call answers 502) when it is expired or valid for under 60 s. No expiry in the answer = previous 20 minutes. The timestamp is read as UTC even when it carries no offset; an unreadable value counts as "no expiry". |
| C5 | **No single-flight login**: with a cold or expired cache every concurrent call logs in separately. | Process-wide `SemaphoreSlim` + re-check of the cache. |
| C6 | Login used the **default client, which follows redirects** (a 307/308 would re-send the username and password to the redirect target) and had no cancellation/timeout of its own (callers without a token — Lookup, ScreenPop — could hang). | Login uses the existing `TigerCS-NoRedirect` client, honours the caller's token and has a 15 s cap. Login failure text carries the status only. |
| C7 | Responses are read with `ReadAsStringAsync` into a `ContentResult`: **`Retry-After` is dropped**, bytes are re-encoded, no `Cache-Control`. | Create/Update responses go through the existing `RelayAsync` (bytes, Content-Type incl. `application/problem+json`, `Retry-After`) and carry `Cache-Control: no-store`. |
| C8 | `_logger.LogError(ex, ...)` writes the full exception (message and stack — messages can contain URLs or credentials). | Logs operation, ticket id, status code, elapsed time and the **exception type only**. No body, phone, transcript, token or credential is ever formatted into a log line (test `Logs_Never...` feeds secrets through success, network failure and timeout). |

### Not found / confirmed correct
* Routes, verbs, policy: unchanged (reflection test, plus a no-duplicate-route check).
* The Genesys bearer is never forwarded; Ticketing always receives the service-account token (test).
* No automatic retry after a timeout or network failure anywhere in the new path (tests); the only replay is the single post-challenge refresh, with the identical body.
* **Request body size:** nothing in these two files sets a limit, so the Kestrel default (30,000,000 bytes) applies unless `Program.cs` / a `[RequestSizeLimit]` says otherwise. The "256 KiB" figure came from the *specification* in the Ticketing repo, not from this code — **please check Program.cs**. `[FromBody] JsonElement` buffers the whole body.

### Optional improvements (not in the patch — left alone to protect Screen Pop, Lookup and Collections behaviour)
* O1. Move `CustomerLookupAsync`/`ScreenPopAsync` (and their controller actions) to the same pattern; today they still retry on any 401, ignore cancellation, drop `Retry-After` and log the whole exception. `ScreenPop` returns a one-time `launchUrl` yet sets **no `Cache-Control: no-store`**.
* O2. `CollectionsGetAsync` still clears the token unconditionally and retries on any 401 (swap in `ClearToken(token)` and the challenge test).
* O3. A second 401 after the refresh is relayed to Genesys as 401 (it is Ticketing's, meaning TigerGroupWeb's service account is rejected). Mapping it to 502 would stop Genesys re-authenticating for nothing, but changes a status for OTP/documents too — your call.
* O4. Set an explicit `[RequestSizeLimit]` on Create/Update once Program.cs is checked.
* O5. `IHttpClientFactory` logs the **full request URI at Information** under `System.Net.Http.HttpClient.*` — for Lookup that includes the phone number, for Collections the customer key. Verify your logging configuration and set those categories to `Warning` (e.g. `"Logging:LogLevel:System.Net.Http.HttpClient": "Warning"`). Not visible from the supplied files.
* O6. Keep `ForwardBudget` below the timeout configured on the Genesys data actions (30 s default); set it from options if Genesys' timeout is shorter.
* O7. Drop the `token`/`jwtToken` fallbacks in `GetAccessTokenAsync` (Ticketing returns `accessToken`).

## 3. Behaviour changes to expect (all deliberate)

| Change | Who sees it |
|---|---|
| Create/Update timeout is now **504** (was 502/hang until the default HttpClient timeout) | Genesys data action treats both as failures; retry rules in the handoff document |
| `Retry-After` and exact bytes of upstream answers are relayed on Create/Update | none for normal answers |
| `Cache-Control: no-store` on Create/Update responses | none |
| 401 without a Bearer challenge is relayed instead of triggering a re-login | none expected: Ticketing's JwtBearer always sends the challenge |
| Token cached until the JWT's real expiry (≤ 20 min as before) | fewer stale-token 401s |
| One login at a time per process; login no longer follows redirects | none |
| New public `ForwardBudget` property on the service (default 30 s) and optional `CancellationToken` parameters on `CreateTicketAsync`/`UpdateTicketAsync` (existing call sites compile unchanged) | developers |

No DI registration, configuration key, route or package changes. The named client `TigerCS-NoRedirect` (already used by `ForwardPostAsync`) is now also used for login.

## 4. Exact modified code

### `TicketingGenesysService` — Create/Update
```csharp
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
```
The same file also changes: the new fields/`ForwardBudget`/`ComputeTokenLifetime`, `GetAccessTokenAsync`, the conditional `ClearToken(string)`, and `ForwardPostAsync` (now a one-line wrapper) / `ForwardJsonAsync`:
```csharp
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
```

### `GenesysController` — Create/Update (nothing else in the controller changes; add `using System.Diagnostics;`)
```csharp
        [HttpPost("tickets")]
        public Task<IActionResult> CreateTicket(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            // GetRawText() is the request body exactly as received, so
            // requestType, transcript and every other field reach TigerCS
            // untouched; the answer (status, bytes, content type,
            // ProblemDetails, Retry-After) comes back as TigerCS gave it.
            var requestJson = body.GetRawText();

            return ForwardTicketJson(
                "ticket creation",
                ticketId: null,
                token => _ticketingService.CreateTicketAsync(requestJson, token),
                cancellationToken);
        }

        // =========================================================
        // 3. UPDATE TICKET / CONVERSATION
        //
        // PATCH:
        // /api/genesys/tickets/{ticketId}
        // =========================================================
        [HttpPatch("tickets/{ticketId:long}")]
        public Task<IActionResult> UpdateTicket(
            long ticketId,
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            var requestJson = body.GetRawText();

            return ForwardTicketJson(
                "ticket update",
                ticketId,
                token => _ticketingService.UpdateTicketAsync(ticketId, requestJson, token),
                cancellationToken);
        }

        // Shared by ticket creation and update. The caller's disconnect and
        // the forwarding budget both cancel the call; a timeout or network
        // failure is NEVER retried here (the outcome is unknown - TigerCS
        // is idempotent per conversationId, so the CALLER may resend the
        // identical request). Nothing from the request or the response body
        // is logged: only the operation, the status and the elapsed time.
        private async Task<IActionResult> ForwardTicketJson(
            string operation,
            long? ticketId,
            Func<CancellationToken, Task<HttpResponseMessage>> forward,
            CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store";

            var clock = Stopwatch.StartNew();

            try
            {
                using var response = await forward(cancellationToken);

                _logger.LogInformation(
                    "Genesys {Operation} (TicketId {TicketId}) answered by TigerCS Ticketing: {StatusCode} in {ElapsedMs} ms.",
                    operation, ticketId, (int)response.StatusCode, clock.ElapsedMilliseconds);

                return await RelayAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Genesys {Operation} (TicketId {TicketId}) timed out waiting for TigerCS Ticketing after {ElapsedMs} ms.",
                    operation, ticketId, clock.ElapsedMilliseconds);

                return StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    new
                    {
                        message =
                            "TigerCS Ticketing did not respond in time. " +
                            "The result is unknown; resend the identical request - it is idempotent."
                    });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Exception type only: messages can carry URLs or credentials.
                _logger.LogError(
                    "Genesys {Operation} (TicketId {TicketId}) failed while communicating with TigerCS Ticketing ({ErrorType}).",
                    operation, ticketId, ex.GetType().Name);

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }
```
`GetAccessTokenAsync` and the field/helper block are in the patched file (see [`TicketingGenesysService.diff`](TicketingGenesysService.diff)).

## 5. Tests
[`tests/TicketingGenesysForwardingTests.cs`](tests/TicketingGenesysForwardingTests.cs) — 54 tests: byte-for-byte pass-through of `requestType`/`transcript`/Create body; Genesys bearer not forwarded; status/Content-Type/ProblemDetails/`Retry-After` preserved for 201/400/404/409/422/503; Bearer-challenge refresh (identical body resent, new token); business 401 not retried; repeated challenge not looped; newer token not discarded; timeout → 504 with exactly one upstream attempt and the call cancelled; slow login inside the same budget; network failure → 502 unchanged message, no retry; redirect refused; rejected service account → 502 without leaking the login answer; caller cancellation; 25 concurrent calls → one login; login on the no-redirect client; token lifetime decisions (cache / use once / reject, expired and near-expiry tokens never cached or sent, UTC timestamp formats, unreadable expiry); OTP/document POST still forwards `Idempotency-Key` and is not replayed after a timeout; ScreenPop/Lookup keep their own retry; Collections path/token unchanged; no valid Genesys bearer → 401 and nothing forwarded; malformed JSON → local 400, nothing forwarded; routes/policy/no duplicate routes; logs contain no customer data, conversation text, ids, tokens or credentials.

To run them in TigerGroupWeb: add `tests/TicketingGenesysForwardingTests.cs` to its test project (needs `Microsoft.AspNetCore.TestHost`; remove the `Host` class's auth stand-in and register your real `GenesysAuthDefaults` policy if you want the authorization test against your OAuth handler). To reproduce the isolated run: `cd harness && dotnet test` after copying the two patched files to `harness/src/`.

## 6. Deployment
1. In TigerGroupWeb, back up/branch, then apply the diffs (`patch -p1 --binary < TicketingGenesysService.diff`, same for the controller) or replace the two files.
2. Add the test file; build with warnings as errors; run the whole TigerGroupWeb test suite (Collections, OTP, documents, screen pop included).
3. Check `Program.cs`: the `TigerCS-NoRedirect` client exists (it already does for OTP); note any request-size limit; check `System.Net.Http.HttpClient` log level (O5).
4. Deploy to the **UAT** TigerGroupWeb first. Smoke test with the payloads in [`../Genesys-UAT-Final-Handoff.md`](../Genesys-UAT-Final-Handoff.md) §4: create (201 then 200), PATCH transcript (count 1; replay → count 1), PATCH `requestType`, PATCH handoff; then force a Ticketing 401 (restart Ticketing with a new JWT key, or clear the token after expiry) and confirm exactly one refresh and a `200`.
5. Check logs for the new lines (`Genesys ticket update ... answered by TigerCS Ticketing: 200 in N ms`) and confirm no phone, body text or token appears.
6. Production: deploy in a quiet window; watch 502/504 counts on the two routes for the first day.
7. **Rollback:** restore the two files; there is no configuration or schema change to undo.

## 7. What is implemented here vs what still needs Genesys Architect
**In TigerGroupWeb (this patch):** reliable, time-bounded, cancellable, log-safe forwarding of `POST /api/genesys/tickets` and `PATCH /api/genesys/tickets/{ticketId}` with faithful status/ProblemDetails/`Retry-After`, and safer service-token handling.
**Still Genesys Architect / Data Action work (unchanged by this patch, see [`../Genesys-UAT-Final-Handoff.md`](../Genesys-UAT-Final-Handoff.md) §5):** the *Update Request Type* and *Send Transcript Message* data actions, sending `trigger` on every handoff, `customerActive` on customer lines, sequential message delivery, retries per the handoff document §7, the intent → `requestTypeId` table, and the source for human-agent messages (blocker B3).
