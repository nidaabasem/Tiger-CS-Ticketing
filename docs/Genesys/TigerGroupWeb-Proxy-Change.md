# TigerGroupWeb — proxy changes for the Genesys routes

**Status: PENDING — NOT IMPLEMENTED IN THIS REPOSITORY.** TigerGroupWeb (`GenesysController`, `TicketingGenesysService`) is a
separate repository that this session could not read or push to (the session is scoped to `nidaabasem/Tiger-CS-Ticketing`;
`list_repos` shows no TigerGroupWeb). Everything below is the exact change to make there. **Nothing here was changed, built or
tested in TigerGroupWeb.** Access needed to complete it: read + push on the TigerGroupWeb repository, and a UAT deployment of it.

> **Implementation:** the complete forwarding patch (all 19 routes, including `verification/*`, `agent-context` and `screen-pop`), the
> route table that the build checks against the controllers, and the acceptance script are in
> [TigerGroupWeb-Forwarding-Implementation.md](TigerGroupWeb-Forwarding-Implementation.md). The rules below remain the specification.

## 1. Route inventory (Genesys-facing path = TigerCS path under `https://tigergroup.ae`)

| # | Method + route | TigerCS state | TigerGroupWeb state (per `docs/Collections/Genesys-Collections-API.md` §9 / this task) | Change needed |
|---|---|---|---|---|
| 1 | `GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary` | implemented; **now carries `nextPayment`** | "implemented and tested, **not deployed**" (in that repo, not visible here) | Deploy; **verify** the proxy is a pass-through so `nextPayment` is not stripped |
| 2 | `GET /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions` | implemented | same | Deploy |
| 3 | `GET /api/genesys/collections/customers/{crmCustomerId}/outstanding` | implemented; answers `503 FinanceUnavailable` (no financial source) | **not forwarded** | Add forwarding (see §4 note) |
| 4 | `GET /api/genesys/collections/customers/{crmCustomerId}/payments` | implemented; `503` as above | **not forwarded** | Add forwarding |
| 5 | `GET /api/genesys/collections/reminders/candidates` | implemented; `503` (no source) | **not forwarded** | Add forwarding |
| 6 | `POST /api/genesys/collections/reminders` (`Idempotency-Key`) | implemented; sending disabled | **not forwarded** | Add forwarding (see §4 note) |
| 7 | `POST /api/genesys/collections/reminders/{reminderId}/outcomes` (`Idempotency-Key`) | implemented | **not forwarded** | Add forwarding |
| 8 | `GET /api/genesys/collections/customers/{crmCustomerId}/reminders` | implemented | **not forwarded** | Add forwarding |
| 9 | `POST /api/genesys/customers/unit-details` | implemented (this branch) | not forwarded | **Add** |
| 10 | `POST /api/genesys/documents/send-copy` (`Idempotency-Key`) | implemented (merged branch); **blocked for real UAT** (no CRM document source) | not forwarded | **Add** (forwarding alone does not unblock the feature) |
| 12 | `POST /api/genesys/verification/buyer-lookup`, `…/otp/send`, `…/otp/resend`, `…/otp/verify` (data actions 13–16) | implemented (review branch); needs `CrmDocuments:Enabled`, SMTP, `OtpCodePepper` | not forwarded (unverified) | **Add**; pass `Retry-After` and `application/problem+json` through unchanged (rules 4 and 6) |
| 13 | `POST /api/genesys/agent-context`, `POST /api/genesys/screen-pop` (agent/staff only — **never** customer auth) | implemented | unknown | Verify; keep separate from customer verification |
| 11 | `PATCH /api/genesys/tickets/{ticketId}` (body `awaitingCustomerReply`) | implemented (merged branch) | forwards the existing PATCH | **Verify** the body is forwarded as-is (§2 rule 4) |

## 2. Rules — identical to the already-implemented `by-key` forwarding (§9 of the Collections API doc)

1. **Genesys authentication stays at TigerGroupWeb.** Validate the Genesys OAuth bearer (`scope ticketing.genesys`,
   `POST /api/genesys/oauth/token`); reject without it (`401`). **Never forward the Genesys token** and never expose TigerCS credentials.
2. **Authorization towards TigerCS** is TigerGroupWeb's own TigerCS service-account JWT (`POST /api/auth/login`), cached until
   near expiry, **renewed and retried once on a downstream `401`** (the existing `by-key` behaviour). The service account is the CS
   Agent account: it satisfies the `CustomerVerification` policy (unit-details, send-copy, the existing Genesys routes) and, for
   Collections, must be listed in `Collections:Authorization:IntegrationEmployeeIds` (ships empty) for outcomes/VoiceBot queueing.
3. **`Idempotency-Key`** — forward verbatim on routes 6, 7 and 10; never generate, drop or regenerate it. TigerCS dedupes on
   `(caller account, key)`. Do not auto-retry a POST on a timeout unless the key is present.
4. **Request body** — forward the bytes unchanged with `Content-Type: application/json`. Do **not** bind to a typed DTO that drops
   unknown members: route 11 would lose `awaitingCustomerReply`; route 9 would lose `unitId`/`customerReference`; route 10 would lose
   `recordId`/`deliveryChannel`.
5. **Path and query** — forward byte-for-byte. For routes 1–2 the `customerKey` segment is taken from the raw request target
   (`ext%3APact%3A3001` ≡ `ext:Pact:3001`); a key that would change the downstream path (`%2F`, `%5C`, `%3F`, `.`, `..`) is refused with
   `400` and TigerCS is not called (existing behaviour; apply the same to any new path parameter).
6. **Response** — return TigerCS's **status code, body and `Content-Type` unchanged** (including `application/problem+json`,
   whose `code` member the Architect flow branches on): `400/401/403/404/409/422/501/502/503`. Never turn a downstream error into
   `200` or a generic 500. Only a TigerGroupWeb-side failure is its own `502/504`.
7. **Time budget (Collections reads, routes 1–5, 8)** — `Ticketing:CollectionsTimeoutSeconds` (default 27 s) or the flow's
   `X-Genesys-Flow-Timeout-Seconds` − 2 s if shorter; each call to TigerCS carries `X-Collections-Deadline-Seconds` = what is left − 3 s.
   When spent, answer `504`; a Genesys disconnect cancels the downstream call. **Next payment adds EDSM calls (up to 8 windows per
   company)**, so measure this budget in UAT (`Next-Payment.md` §6 #11).
8. **Log** route, status and TigerCS `traceId` only — never bodies (phone numbers, customer references, amounts) and never tokens or key values.

## 3. Reference sketch (C#; illustrative — adapt to `TicketingGenesysService`; not compiled)

```csharp
// New routes, same ForwardAsync used by the existing by-key reads.
app.MapPost("/api/genesys/customers/unit-details", (HttpContext c, TicketingGenesysService s) =>
    s.ForwardAsync(c, "api/genesys/customers/unit-details"));
app.MapPost("/api/genesys/documents/send-copy", (HttpContext c, TicketingGenesysService s) =>
    s.ForwardAsync(c, "api/genesys/documents/send-copy", forwardHeaders: ["Idempotency-Key"]));

// ForwardAsync: validate Genesys token -> GetServiceAccountTokenAsync() (renew + retry once on 401)
// var req = new HttpRequestMessage(c.Request.Method, tigerCsBase + rawPathAndQuery) { Content = new StreamContent(c.Request.Body) };
// req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");   // POST/PATCH only
// req.Headers.Authorization = new("Bearer", serviceToken);
// foreach (var h in forwardHeaders) if (c.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, v.ToArray());
// using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c.RequestAborted);
// c.Response.StatusCode = (int)res.StatusCode; c.Response.ContentType = res.Content.Headers.ContentType?.ToString();
// await res.Content.CopyToAsync(c.Response.Body);
```

## 4. Notes on the not-yet-forwarded Collections routes

The Collections API document records a **deliberate** decision not to forward routes 3–8: `outstanding`/`payments` have no financial
source (they answer `503`), and reminder sending stays disabled. This task asks for all existing Collections routes to be forwarded,
so the change is specified above; **before enabling routes 6–7 in UAT, confirm with the business that reminder queueing and outcome
recording are meant to be reachable from Genesys** (they create records and, when sending is enabled, customer messages). Forwarding
`outstanding`/`payments` only relays a `503` until a financial source exists. Next payment does **not** depend on them: it is
carried by route 1.

## 5. Acceptance — to run once deployed (none has been run)

* No Genesys token → `401` on every route; with it, the call reaches TigerCS as the service account.
* Route 9: a `unitId` of another customer → `403` `UNIT_NOT_ELIGIBLE` arrives unchanged.
* Route 10: same `Idempotency-Key` twice → TigerCS's `duplicate:true`, one email. Route 6/7 likewise.
* Route 11: `{ "conversationId": "…", "awaitingCustomerReply": true }` returns TigerCS's `inactivityDeadlineUtc` unchanged.
* Route 1: the body contains `nextPayment` exactly as TigerCS sent it (compare against a direct TigerCS call).
* A `customerKey` containing `%2F` → `400`, TigerCS not called.
