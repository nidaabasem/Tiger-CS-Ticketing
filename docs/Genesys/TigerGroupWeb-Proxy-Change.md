# TigerGroupWeb — proxy change required for the new Genesys routes

**Status: NOT IMPLEMENTED.** The TigerGroupWeb source (`TicketingGenesysService`) was not accessible to the implementer,
so this is the exact change to make there, not a change that was made or tested. Needed repository access:
the TigerGroupWeb repository (read + push).

## Routes to forward (Genesys → TigerGroupWeb → TigerCS)

| Genesys-facing route (`https://tigergroup.ae`) | Method | TigerCS route |
|---|---|---|
| `/api/genesys/customers/unit-details` | POST | `/api/genesys/customers/unit-details` |
| `/api/genesys/documents/send-copy` | POST | `/api/genesys/documents/send-copy` |
| `/api/genesys/tickets/{ticketId}` | PATCH | existing — **verify** it forwards the body as-is (see below) |

## Rules (same as the existing three routes, plus the two additions the new routes need)

1. **Genesys authentication stays at TigerGroupWeb.** Validate the Genesys OAuth bearer (`scope ticketing.genesys`)
   exactly as for `customers/lookup`; reject without it (`401`). Genesys never sees TigerCS credentials.
2. **Authorization towards TigerCS** is TigerGroupWeb's own TigerCS service-account JWT (`POST /api/auth/login`, cached
   until near expiry, one re-login and one retry on a downstream `401`). Do **not** forward the Genesys token to TigerCS.
   The service account must hold the CS Agent/Supervisor role (`CustomerVerification` policy). For `send-copy` the same
   account must be the one that created the verification session (TigerCS enforces it).
3. **`Idempotency-Key`** — forward the request header **verbatim** on `send-copy` (TigerCS keys duplicates on
   `(caller account, key)`; a proxy that drops or regenerates it turns retries into duplicate emails). Never generate one.
   Do not auto-retry a POST to TigerCS on a timeout unless the key is present.
4. **Request body** — forward the bytes unchanged, `Content-Type: application/json`. Do **not** bind to a typed DTO that
   drops unknown members: for the PATCH route that would silently lose `awaitingCustomerReply`; for `unit-details`
   it would lose `unitId`/`customerReference`.
5. **Response** — return TigerCS's **status code, body and `Content-Type` unchanged**, including `400/403/404/409/422/501/502/503`
   ProblemDetails (their `code` member is what the Architect flow branches on). Do not turn a downstream error into `200`
   or a generic 500. Only a TigerGroupWeb-side failure (cannot reach TigerCS, timeout) is its own `502/504`.
6. **Log** route, status, TigerCS `traceId`; never the body (phone numbers, customer reference) and never tokens or the key value.

## Reference sketch (C#; illustrative — adapt to `TicketingGenesysService`, not compiled here)

```csharp
app.MapPost("/api/genesys/customers/unit-details", (HttpContext c, TicketingGenesysService s) => s.ForwardAsync(c, "api/genesys/customers/unit-details"));
app.MapPost("/api/genesys/documents/send-copy",    (HttpContext c, TicketingGenesysService s) => s.ForwardAsync(c, "api/genesys/documents/send-copy", forwardHeaders: ["Idempotency-Key"]));

// ForwardAsync: validate Genesys token -> GetServiceAccountTokenAsync()
// var req = new HttpRequestMessage(c.Request.Method, tigerCsBase + path) { Content = new StreamContent(c.Request.Body) };
// req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
// req.Headers.Authorization = new("Bearer", serviceToken);
// foreach (var h in forwardHeaders) if (c.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, v.ToArray());
// using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted);
// c.Response.StatusCode = (int)res.StatusCode; c.Response.ContentType = res.Content.Headers.ContentType?.ToString();
// await res.Content.CopyToAsync(c.Response.Body);
```

## Acceptance (to run once deployed; none of this has been run)

* `unit-details` without a Genesys token → 401; with it, a non-owned `unitId` → 403 `UNIT_NOT_ELIGIBLE` passes through unchanged.
* `send-copy` twice with the same `Idempotency-Key` → second answer is TigerCS's `duplicate:true`, one email.
* PATCH `{ "conversationId": "...", "awaitingCustomerReply": true }` returns TigerCS's `inactivityDeadlineUtc` unchanged.
