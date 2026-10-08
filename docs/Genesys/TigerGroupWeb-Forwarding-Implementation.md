# TigerGroupWeb forwarding - implementation patch, route table and acceptance script

**Status: PATCH SPECIFIED AND COMPILED IN ISOLATION - NOT APPLIED TO TIGERGROUPWEB.** TigerGroupWeb is a separate repository that was not
accessible when this was written. Nothing here was built, deployed or run inside TigerGroupWeb, and nothing was run against Genesys Cloud.
What *was* done: the C# in section 4 was compiled (warnings as errors, .NET 10 SDK) in a scratch ASP.NET Core project and exercised with
`curl` against an in-process fake TigerCS (section 7 lists exactly what that proved). This document completes
[TigerGroupWeb-Proxy-Change.md](TigerGroupWeb-Proxy-Change.md), which stays the statement of the rules (section 2 there); this file is the
implementation of those rules for **every** route, plus the `verification/*`, `agent-context` and `screen-pop` routes the earlier
document omitted.

Notation: `{TG}` = `https://tigergroup.ae` (public), `{TCS}` = TigerCS.Api base URL (internal).

## 1. Route table (public URL -> TigerCS URL -> data action)

The table between the markers is **checked by the build**: `TigerGroupWebRouteTableTests` (TigerCS.Tests) reflects over the four
Genesys-facing controllers and fails when a route is added, removed or renamed, when a `[FromHeader]`/query parameter changes, when a
declared `[ProducesResponseType]` status is missing here, or when a data-action file points at a route that is not in the table. Update the
table (and section 4's `ForwardRoutes.All`, which must list the same 19 routes) in the same change as the controller.

Columns: *Required headers* are the request headers TigerCS binds besides `Authorization` (forwarded verbatim, never generated).
*Expected statuses* are the controller's declared statuses plus the ones the forwarding layer itself can answer: `400` (path or
query refused), `401` (no valid Genesys bearer), `413` (body too large, POST/PATCH), `502` (TigerCS unreachable or it rejected the
integration account), `504` (budget spent).

<!-- ROUTE-TABLE:BEGIN -->
| # | Method | Public URL | TigerCS URL | Data action | Required headers | Query parameters | Expected statuses |
|---|---|---|---|---|---|---|---|
| 1 | POST | `https://tigergroup.ae/api/genesys/tickets` | `{TCS}/api/genesys/tickets` | 02 | - | - | 200, 201, 400, 401, 413, 422, 502, 503, 504 |
| 2 | PATCH | `https://tigergroup.ae/api/genesys/tickets/{ticketId}` | `{TCS}/api/genesys/tickets/{ticketId}` | 03, 04, 05, 06, 07, 10 | - | - | 200, 400, 401, 404, 409, 413, 422, 502, 503, 504 |
| 3 | GET | `https://tigergroup.ae/api/genesys/customers/lookup` | `{TCS}/api/genesys/customers/lookup` | 01 | - | phoneNumber | 200, 400, 401, 502, 503, 504 |
| 4 | POST | `https://tigergroup.ae/api/genesys/customers/unit-details` | `{TCS}/api/genesys/customers/unit-details` | 12 | - | - | 200, 400, 401, 403, 409, 413, 502, 503, 504 |
| 5 | POST | `https://tigergroup.ae/api/genesys/verification/buyer-lookup` | `{TCS}/api/genesys/verification/buyer-lookup` | 13 | - | - | 200, 400, 401, 404, 409, 413, 502, 503, 504 |
| 6 | POST | `https://tigergroup.ae/api/genesys/verification/otp/send` | `{TCS}/api/genesys/verification/otp/send` | 14 | - | - | 200, 400, 401, 403, 404, 409, 413, 422, 429, 502, 503, 504 |
| 7 | POST | `https://tigergroup.ae/api/genesys/verification/otp/resend` | `{TCS}/api/genesys/verification/otp/resend` | 15 | - | - | 200, 400, 401, 404, 410, 413, 423, 429, 502, 504 |
| 8 | POST | `https://tigergroup.ae/api/genesys/verification/otp/verify` | `{TCS}/api/genesys/verification/otp/verify` | 16 | - | - | 200, 400, 401, 404, 409, 410, 413, 423, 502, 504 |
| 9 | POST | `https://tigergroup.ae/api/genesys/documents/send-copy` | `{TCS}/api/genesys/documents/send-copy` | 11 | Idempotency-Key | - | 200, 202, 400, 401, 403, 404, 409, 413, 422, 501, 502, 503, 504 |
| 10 | GET | `https://tigergroup.ae/api/genesys/collections/customers/by-key/{customerKey}/payment-summary` | `{TCS}/api/genesys/collections/customers/by-key/{customerKey}/payment-summary` | 08 | - | includeTransactions | 200, 400, 401, 403, 404, 502, 503, 504 |
| 11 | GET | `https://tigergroup.ae/api/genesys/collections/customers/by-key/{customerKey}/payment-transactions` | `{TCS}/api/genesys/collections/customers/by-key/{customerKey}/payment-transactions` | 09 | - | companyId, type | 200, 400, 401, 403, 404, 422, 502, 503, 504 |
| 12 | GET | `https://tigergroup.ae/api/genesys/collections/customers/{crmCustomerId}/outstanding` | `{TCS}/api/genesys/collections/customers/{crmCustomerId}/outstanding` | - | - | accountId, cursor, pageSize, unitId | 200, 400, 401, 403, 404, 502, 503, 504 |
| 13 | GET | `https://tigergroup.ae/api/genesys/collections/customers/{crmCustomerId}/payments` | `{TCS}/api/genesys/collections/customers/{crmCustomerId}/payments` | - | - | accountId, cursor, fromDate, pageSize, toDate, unitId, view | 200, 400, 401, 403, 404, 502, 503, 504 |
| 14 | GET | `https://tigergroup.ae/api/genesys/collections/customers/{crmCustomerId}/reminders` | `{TCS}/api/genesys/collections/customers/{crmCustomerId}/reminders` | - | - | accountId, cursor, pageSize | 200, 400, 401, 403, 502, 503, 504 |
| 15 | GET | `https://tigergroup.ae/api/genesys/collections/reminders/candidates` | `{TCS}/api/genesys/collections/reminders/candidates` | - | - | accountId, businessDate, crmCustomerId, cursor, pageSize, reminderType | 200, 400, 401, 403, 502, 503, 504 |
| 16 | POST | `https://tigergroup.ae/api/genesys/collections/reminders` | `{TCS}/api/genesys/collections/reminders` | - | Idempotency-Key | - | 200, 202, 400, 401, 403, 409, 413, 422, 502, 503, 504 |
| 17 | POST | `https://tigergroup.ae/api/genesys/collections/reminders/{reminderId}/outcomes` | `{TCS}/api/genesys/collections/reminders/{reminderId}/outcomes` | - | Idempotency-Key | - | 200, 202, 400, 401, 403, 404, 409, 413, 502, 503, 504 |
| 18 | POST | `https://tigergroup.ae/api/genesys/agent-context` | `{TCS}/api/genesys/agent-context` | - | - | - | 200, 400, 401, 403, 404, 413, 502, 503, 504 |
| 19 | POST | `https://tigergroup.ae/api/genesys/screen-pop` | `{TCS}/api/genesys/screen-pop` | - | - | - | 200, 400, 401, 403, 413, 502, 503, 504 |
<!-- ROUTE-TABLE:END -->

Not in the table on purpose: `POST /api/genesys/oauth/token` (data action `00`) is TigerGroupWeb's own Genesys token endpoint and never reaches
TigerCS; `/api/auth/login` is used only by TigerGroupWeb to obtain the service-account JWT; `/api/verification-sessions` (agent UI) and
`/api/collections/*` (staff UI) are not Genesys-facing and must **not** be forwarded.

## 2. Journeys traced through the forwarding layer

For each journey: the data actions, the public routes in call order, what the forwarding layer must preserve for the journey to work, and
what was found. "Found" items are facts from the TigerCS code; whatever happens inside TigerGroupWeb/Genesys is `[ext]`.

### 2.1 Customer lookup (data action 01)
`GET {TG}/api/genesys/customers/lookup?phoneNumber=tel%3A%2B971501234567`
* **Preserve:** the raw query. Rebuilding it from decoded values turns `+` into a space; `ForwardAsync` sends the raw target unchanged.
* Statuses seen by the flow: 200 (also when nothing is found - `found:false`), 400 (not a phone number), 503. Output feeds data actions 12 (`externalCustomerId` -> `customerReference` when `verificationSource=Crm`) and 08/09 (`externalCustomerId` -> `customerKey` `ext:Pact:{id}` when `verificationSource=Pact` and `matchedCustomerCount=1`).

### 2.2 Verification (OTP) and document delivery (data actions 13, 14, 15, 16, 11)
1. `POST verification/buyer-lookup {phoneNumber}` -> 200 `Found` + `units[]`; 404 `CUSTOMER_NOT_FOUND`; 409 `CUSTOMER_AMBIGUOUS`; 400; 502; 503.
2. `POST verification/otp/send {phoneNumber, crmUnitId}` -> 200 `CodeSent` / `AlreadySent` / `UnitSelectionRequired`; 403 `UNIT_NOT_OWNED`; 422 `NO_EMAIL_ON_RECORD`; **429 + `Retry-After`** (`OTP_RATE_LIMITED`, `OTP_RESEND_LIMIT_REACHED`).
3. `POST verification/otp/resend {challengeId}` -> 200; **429 `OTP_RESEND_TOO_SOON` + `Retry-After`**; 410 `OTP_EXPIRED`; 423 `OTP_LOCKED`; 404.
4. `POST verification/otp/verify {challengeId, code}` -> 200 `Verified` + `session.verificationSessionId`; 400 `OTP_INVALID` + `attemptsRemaining`; 409 `OTP_ALREADY_USED`; 410; 423; 404.
5. `POST documents/send-copy` + **`Idempotency-Key`** -> 200 `Sent` (`duplicate:true` on replay) / `SelectionRequired`; 202 `Queued`; 403 `VERIFICATION_FAILED` / `RECORD_OWNERSHIP_MISMATCH`; 404; 409 `IDEMPOTENCY_KEY_REUSED`; 422; 501; 502; 503.
* **Preserve:** bodies verbatim (a typed DTO would drop `crmLeadId` / `recordId` / `deliveryChannel`); `Retry-After` and the `application/problem+json` body unchanged - the `code`, `attemptsRemaining` and `resendAvailableAtUtc` members are what the flow branches on; the same `Idempotency-Key` on every retry of one request and a *new* key for the follow-up that carries the customer's choice; no caching (`Cache-Control: no-store` is added).
* **Found - ownership is per integration account, not per customer or conversation.** TigerCS binds a challenge and a verification session to the *calling TigerCS account* (`OTP_CHALLENGE_NOT_FOUND` for another caller, `VERIFICATION_FAILED` for a session of another caller). All Genesys traffic reaches TigerCS as the **one** service account, so TigerCS cannot tell two conversations apart: anyone in the flow holding a `challengeId` / `verificationSessionId` (random GUIDs) can use it. The protection is that the ids are unguessable and live only in that conversation's flow variables. Tests prove cross-account separation (`GenesysDocumentsEndpointTests`, `GenesysVerificationNumericEnumAndLifecycleTests`) but **cannot** prove cross-conversation separation; closing that needs a TigerCS change (bind the challenge to `conversationId`) and is not part of this patch.
* **Found - the Genesys failure path.** Non-2xx answers (400/403/404/409/410/422/423/429/5xx) leave the data action through its failure path; the shipped files define success schemas only, so whether the flow can read the problem body's `code` / `attemptsRemaining` depends on Genesys' failure output `[ext]`. The forwarding layer's part is to pass the body through unchanged.

### 2.3 Customer unit details (data action 12)
`POST customers/unit-details {customerReference, phoneNumber, unitId?}` -> 200 `UnitSelectionRequired` / `UnitDetails`; 403 `CUSTOMER_NOT_VERIFIED` / `UNIT_NOT_ELIGIBLE`; 409 `CUSTOMER_AMBIGUOUS`; 502; 503. **Preserve:** the body (an `unitId` dropped by a typed DTO silently turns "details" into "which unit?").

### 2.4 Collections payment summary / transactions (data actions 08, 09)
`GET collections/customers/by-key/{customerKey}/payment-summary?includeTransactions=false`, then `.../payment-transactions?companyId=&type=` for each id in 08's `availableCompanyIds`.
* **Preserve:** the raw `customerKey` segment (`ext%3APact%3A3001` == `ext:Pact:3001`); refuse any key that changes the downstream path (`%2F`, `%5C`, `%3F`, `%23`, double encoding, `.`, `..`) with 400 and do not call TigerCS; the time budget - `min(Ticketing:CollectionsTimeoutSeconds, X-Genesys-Flow-Timeout-Seconds - 2)` - and `X-Collections-Deadline-Seconds` = what is left minus 3 s (TigerCS reads exactly this header; without it the default deadline is 22 s).
* Statuses: 200, 404 `AccountNotFound`, 403, 422 `CustomerNotMapped`, 503 `FinanceUnavailable`, 504 from the proxy. Rendering `nextPayment` fields requires TigerCS' body to pass unchanged.

### 2.5 Tickets (data actions 02-07, 10)
`POST tickets`, then `PATCH tickets/{ticketId}` (routing, end, handoff, cancel, confirmed-resolved, awaitingCustomerReply). **Preserve:** the PATCH body byte-for-byte (`awaitingCustomerReply`, `handoff`, `customerConfirmation` are separate members of one record; a typed proxy DTO would drop whatever it does not know). `ticketId` must be numeric (`{ticketId}` is validated as digits before TigerCS is called).

### 2.6 Collections reminders, outstanding, payments (no data action)
Routes 12-17 are forwarded for completeness. `outstanding`/`payments`/`candidates` answer `503` today (no financial source); `POST reminders` and `.../outcomes` create records and, when sending is enabled, customer messages, and need the service account in `Collections:Authorization:IntegrationEmployeeIds`. **Decision for the business before enabling 16-17 in UAT** (see [TigerGroupWeb-Proxy-Change.md](TigerGroupWeb-Proxy-Change.md) section 4). The patch can ship with 16-17 left out of `ForwardRoutes.All`; the route-table test then needs the same two rows removed - on purpose, so the omission is a visible decision.

### 2.7 Agent context and screen pop (no data action) - agent surface only
`POST agent-context {genesysUserId, ...}` and `POST screen-pop {genesysUserId, ...}` are called by agent-side scripts, never by a customer flow.
* They are registered with `RouteSurface.Agent` so `IGenesysBearerValidator` can apply a stricter rule if one exists; they must **never** accept customer verification artefacts (OTP session ids, challenge ids) as authentication.
* The `screen-pop` response carries `launchUrl` - a one-time token that logs the agent into TigerCS. It is therefore never logged by the forwarder, is sent with `Cache-Control: no-store`, and must not be written to Genesys conversation attributes visible to customers.

## 3. Rules -> where the patch implements them

| Rule (Proxy-Change section 2) | Implementation |
|---|---|
| 1 Genesys auth stays at TigerGroupWeb; Genesys token never forwarded | `IGenesysBearerValidator` before anything else; the downstream `Authorization` is always the service token |
| 2 Service-account JWT, cached, renew + retry once on 401 | `TigerCsServiceTokenProvider` (renews 2 min early) and the 401 branch in `ForwardAsync`; a *second* 401 becomes `502 TigerCsAuthenticationFailed` (a 401 to Genesys would make it re-authenticate for nothing) |
| 3 `Idempotency-Key` verbatim, never generated; no auto-retry of POST without it | `ForwardRoute.ForwardHeaders`; the only automatic retry is after a 401 (the request was not processed), with the same key |
| 4 Body unchanged, no typed DTO | body read as `byte[]`, sent as `ByteArrayContent`; same bytes on the 401 retry |
| 5 Path and query byte-for-byte; unsafe segments refused | `TryBuildDownstreamTarget` over `IHttpRequestFeature.RawTarget`; literal segments exact, placeholders via `SegmentRules` |
| 6 Status, body, Content-Type unchanged (incl. `problem+json`), `Retry-After` kept | response copy block; only `Retry-After` and `Content-Language` headers are passed, `Cache-Control: no-store` added |
| 7 Time budget and deadline header; Genesys disconnect cancels | `CancelAfter(budget)` linked to `RequestAborted`; `X-Collections-Deadline-Seconds` on Collections reads; 504 when spent |
| 8 Log route, status, trace id only | one `LogInformation` with route name, status, elapsed; no bodies, queries, keys or tokens |

## 4. The patch (compiled in isolation)

Add the file below to TigerGroupWeb, replace the two seams with the existing implementations, and wire it in `Program.cs`:

```csharp
// Program.cs
builder.Services.AddTigerCsForwarding(builder.Configuration);           // binds the "Ticketing" section
builder.Services.AddSingleton<IGenesysBearerValidator, ExistingGenesysBearerValidator>(); // wrap the check the by-key routes use today
// ...
app.MapTigerCsForwarding();                                              // replaces the existing by-key / PATCH forwarding endpoints
```

```jsonc
// appsettings.json (secrets from the environment: Ticketing__ServiceUsername / Ticketing__ServicePassword)
"Ticketing": {
  "TigerCsBaseUrl": "https://<tigercs-api-host>/",
  "CollectionsTimeoutSeconds": 27,
  "DefaultTimeoutSeconds": 25
}
```

```csharp
// TigerGroupWeb / Genesys/TigerCsForwarding.cs
// Forwards the Genesys-facing routes to TigerCS. Compiles against ASP.NET Core 8+ (checked with the .NET 10 SDK).
// Replace the two interfaces' implementations with the existing Genesys-token validation and service-account login.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace TigerGroupWeb.Genesys;

// ---------------------------------------------------------------------------------------------------------------------
//  Configuration
// ---------------------------------------------------------------------------------------------------------------------

public sealed class TigerCsForwardingOptions
{
    public const string Section = "Ticketing";

    /// <summary>TigerCS.Api base URL, e.g. https://tigercs-api.internal/ (trailing slash optional).</summary>
    public string TigerCsBaseUrl { get; set; } = string.Empty;

    /// <summary>The CS Agent service account TigerGroupWeb logs in as (secrets: environment or vault, never committed).</summary>
    public string ServiceUsername { get; set; } = string.Empty;
    public string ServicePassword { get; set; } = string.Empty;

    /// <summary>Budget for Collections reads (rule 7). Default 27 s.</summary>
    public int CollectionsTimeoutSeconds { get; set; } = 27;

    /// <summary>Budget for every other route (OTP mail, CRM calls, document delivery). Default 25 s.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 25;

    /// <summary>Largest request body accepted from Genesys (bytes).</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;
}

// ---------------------------------------------------------------------------------------------------------------------
//  Seams to the code TigerGroupWeb already has
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Validates the Genesys OAuth bearer (scope <c>ticketing.genesys</c>). Wrap what the existing by-key routes use.</summary>
public interface IGenesysBearerValidator
{
    Task<bool> IsValidAsync(string? authorizationHeader, RouteSurface surface, CancellationToken cancellationToken);
}

/// <summary>Customer-facing data actions versus the agent/staff surface (agent-context, screen-pop).</summary>
public enum RouteSurface { Customer, Agent }

/// <summary>The TigerCS service-account JWT: cached until shortly before expiry, renewed on demand.</summary>
public interface ITigerCsServiceTokenProvider
{
    Task<string> GetAsync(bool forceRenew, CancellationToken cancellationToken);
}

public sealed class TigerCsServiceTokenProvider(
    IHttpClientFactory httpClientFactory, IOptions<TigerCsForwardingOptions> options, TimeProvider clock) : ITigerCsServiceTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _renewAfter;

    public async Task<string> GetAsync(bool forceRenew, CancellationToken cancellationToken)
    {
        if (!forceRenew && _token is not null && clock.GetUtcNow() < _renewAfter)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have renewed while this one waited; do not log in twice for one expiry.
            if (!forceRenew && _token is not null && clock.GetUtcNow() < _renewAfter)
            {
                return _token;
            }

            var o = options.Value;
            using var client = httpClientFactory.CreateClient(TigerCsForwarder.HttpClientName);
            using var login = await client.PostAsJsonAsync("api/auth/login", new { username = o.ServiceUsername, password = o.ServicePassword }, cancellationToken);
            login.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync(cancellationToken));
            _token = doc.RootElement.GetProperty("accessToken").GetString()!;
            var expires = doc.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset();
            _renewAfter = expires - TimeSpan.FromMinutes(2); // renew early; a downstream 401 still renews and retries once
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
//  The route table. Keep identical to docs/Genesys/TigerGroupWeb-Forwarding-Implementation.md (a TigerCS test compares that table with the controllers).
// ---------------------------------------------------------------------------------------------------------------------

public enum Budget { Default, CollectionsRead }

/// <param name="Method">GET, POST or PATCH.</param>
/// <param name="Template">Path below the site root, no leading slash. <c>{name}</c> segments are validated by <see cref="SegmentRules"/>.</param>
/// <param name="ForwardHeaders">Request headers forwarded verbatim in addition to the service-account Authorization.</param>
/// <param name="Surface">Customer data action or agent surface.</param>
/// <param name="Budget">Time budget class.</param>
public sealed record ForwardRoute(string Method, string Template, string[] ForwardHeaders, RouteSurface Surface, Budget Budget)
{
    public string Name => $"{Method} /{Template}";
}

public static class ForwardRoutes
{
    private static readonly string[] None = [];
    private static readonly string[] Idempotency = ["Idempotency-Key"];

    public static readonly IReadOnlyList<ForwardRoute> All =
    [
        // ---- Tickets (data actions 02-07, 10) ----
        new("POST", "api/genesys/tickets", None, RouteSurface.Customer, Budget.Default),
        new("PATCH", "api/genesys/tickets/{ticketId}", None, RouteSurface.Customer, Budget.Default),
        // ---- Customer lookup / unit details (01, 12) ----
        new("GET", "api/genesys/customers/lookup", None, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/customers/unit-details", None, RouteSurface.Customer, Budget.Default),
        // ---- Verification and documents (13-16, 11) ----
        new("POST", "api/genesys/verification/buyer-lookup", None, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/verification/otp/send", None, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/verification/otp/resend", None, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/verification/otp/verify", None, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/documents/send-copy", Idempotency, RouteSurface.Customer, Budget.Default),
        // ---- Collections (08, 09 and the rest of the Collections API) ----
        new("GET", "api/genesys/collections/customers/by-key/{customerKey}/payment-summary", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("GET", "api/genesys/collections/customers/by-key/{customerKey}/payment-transactions", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("GET", "api/genesys/collections/customers/{crmCustomerId}/outstanding", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("GET", "api/genesys/collections/customers/{crmCustomerId}/payments", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("GET", "api/genesys/collections/customers/{crmCustomerId}/reminders", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("GET", "api/genesys/collections/reminders/candidates", None, RouteSurface.Customer, Budget.CollectionsRead),
        new("POST", "api/genesys/collections/reminders", Idempotency, RouteSurface.Customer, Budget.Default),
        new("POST", "api/genesys/collections/reminders/{reminderId}/outcomes", Idempotency, RouteSurface.Customer, Budget.Default),
        // ---- Agent / staff surface: never customer authentication ----
        new("POST", "api/genesys/agent-context", None, RouteSurface.Agent, Budget.Default),
        new("POST", "api/genesys/screen-pop", None, RouteSurface.Agent, Budget.Default)
    ];
}

/// <summary>What a <c>{placeholder}</c> may contain, as the RAW (still percent-encoded) path segment.</summary>
public static class SegmentRules
{
    private static readonly Regex RawCharacters = new("^[A-Za-z0-9._~:%-]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Numeric = new("^[0-9]{1,18}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAcceptable(string placeholder, string rawSegment) => placeholder switch
    {
        "ticketId" or "crmCustomerId" => Numeric.IsMatch(rawSegment),
        "reminderId" => Identifier.IsMatch(rawSegment),
        "customerKey" => IsSafeKey(rawSegment),
        _ => false // a new placeholder must be given a rule on purpose
    };

    /// <summary>
    /// <c>ext%3APact%3A3001</c> and <c>ext:Pact:3001</c> are the same key. A segment that would change the downstream path
    /// once decoded (<c>%2F</c>, <c>%5C</c>, <c>%3F</c>, <c>%23</c>, double encoding, <c>.</c>, <c>..</c>, control characters) is refused.
    /// </summary>
    private static bool IsSafeKey(string raw)
    {
        if (!RawCharacters.IsMatch(raw) || raw is "." or "..")
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return decoded.Length > 0
            && decoded is not "." and not ".."
            && decoded.IndexOfAny(['/', '\\', '?', '#', '%', ' ']) < 0
            && decoded.All(c => !char.IsControl(c));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
//  The forwarder
// ---------------------------------------------------------------------------------------------------------------------

public sealed class TigerCsForwarder(
    IHttpClientFactory httpClientFactory,
    ITigerCsServiceTokenProvider tokens,
    IGenesysBearerValidator genesysAuth,
    IOptions<TigerCsForwardingOptions> options,
    ILogger<TigerCsForwarder> logger)
{
    public const string HttpClientName = "TigerCs";
    private const string FlowTimeoutHeader = "X-Genesys-Flow-Timeout-Seconds";
    private const string DeadlineHeader = "X-Collections-Deadline-Seconds";

    // Response headers handed back to Genesys. Everything else (Set-Cookie, Server, internal hops) is dropped.
    private static readonly string[] PassThroughResponseHeaders = ["Retry-After", "Content-Language"];

    public async Task ForwardAsync(HttpContext context, ForwardRoute route)
    {
        var cancellationToken = context.RequestAborted; // a Genesys disconnect cancels the downstream call
        var started = Stopwatch.GetTimestamp();
        var o = options.Value;

        // 1. Genesys authentication stays here (rule 1). Nothing below runs without it; the Genesys token is never forwarded.
        if (!await genesysAuth.IsValidAsync(context.Request.Headers.Authorization.ToString(), route.Surface, cancellationToken))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problem(context, StatusCodes.Status401Unauthorized, "GenesysAuthenticationRequired", "A valid Genesys bearer token is required.");
            return;
        }

        // 2. Raw-path safety (rule 5): match the raw request target against the route; build the downstream URL from the raw text only.
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? string.Empty;
        if (!TryBuildDownstreamTarget(route, rawTarget, out var downstreamTarget))
        {
            logger.LogWarning("Refused {Route}: the request path does not match the route or would change the downstream path.", route.Name);
            await Problem(context, StatusCodes.Status400BadRequest, "InvalidRequest", "The request path is not valid for this route.");
            return;
        }

        // 3. Body (rule 4): read once as bytes so it can be sent again unchanged after a 401 renewal.
        byte[]? body = null;
        if (route.Method is "POST" or "PATCH")
        {
            if (context.Request.ContentLength > o.MaxBodyBytes)
            {
                await Problem(context, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The request body is too large.");
                return;
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > o.MaxBodyBytes)
            {
                await Problem(context, StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge", "The request body is too large.");
                return;
            }

            body = buffer.ToArray();
        }

        // 4. Time budget (rule 7).
        var budget = route.Budget == Budget.CollectionsRead ? o.CollectionsTimeoutSeconds : o.DefaultTimeoutSeconds;
        if (int.TryParse(context.Request.Headers[FlowTimeoutHeader].ToString(), out var flowSeconds) && flowSeconds > 0)
        {
            budget = Math.Min(budget, Math.Max(1, flowSeconds - 2));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(budget));

        try
        {
            // 5. Send; on a downstream 401, renew the service token and retry exactly once (rule 2).
            var token = await tokens.GetAsync(forceRenew: false, timeout.Token);
            var response = await SendAsync(context, route, downstreamTarget, body, token, budget, started, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                token = await tokens.GetAsync(forceRenew: true, timeout.Token);
                response = await SendAsync(context, route, downstreamTarget, body, token, budget, started, timeout.Token);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // TigerGroupWeb's own credentials are rejected. 401 would make Genesys re-authenticate for nothing: it is a gateway fault.
                    logger.LogError("TigerCS rejected the service account for {Route} after renewal.", route.Name);
                    await Problem(context, StatusCodes.Status502BadGateway, "TigerCsAuthenticationFailed", "The ticketing service could not be reached with the integration account.");
                    return;
                }

                // 6. Status, Content-Type (incl. application/problem+json), Retry-After and body: unchanged (rules 4 and 6).
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                foreach (var name in PassThroughResponseHeaders)
                {
                    if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    {
                        context.Response.Headers[name] = values.ToArray();
                    }
                }

                context.Response.Headers.CacheControl = "no-store"; // screen-pop launch URLs and OTP answers must never be cached
                await response.Content.CopyToAsync(context.Response.Body, cancellationToken);

                // 8. Route, status and elapsed time only: never bodies, phone numbers, keys, tokens or the screen-pop launch URL.
                logger.LogInformation("{Route} -> {Status} in {ElapsedMs} ms", route.Name, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genesys went away; nobody is listening. The downstream call was cancelled with it.
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("{Route} exceeded its {Budget} s budget.", route.Name, budget);
            await Problem(context, StatusCodes.Status504GatewayTimeout, "GatewayTimeout", "The ticketing service did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "{Route} could not reach TigerCS.", route.Name);
            await Problem(context, StatusCodes.Status502BadGateway, "TigerCsUnreachable", "The ticketing service is not reachable.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpContext context, ForwardRoute route, string downstreamTarget, byte[]? body, string serviceToken,
        int budgetSeconds, long started, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), downstreamTarget);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken); // the TigerCS service account, never the Genesys token
        request.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        request.Headers.Accept.ParseAdd("application/problem+json");

        foreach (var name in route.ForwardHeaders)
        {
            // Rule 3: forwarded verbatim; never generated, dropped or regenerated (also not on the 401 retry).
            if (context.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                request.Headers.TryAddWithoutValidation(name, value.ToArray());
            }
        }

        if (route.Budget == Budget.CollectionsRead)
        {
            // What is left of the budget, minus 3 s for the hop back to Genesys.
            var left = budgetSeconds - (int)Stopwatch.GetElapsedTime(started).TotalSeconds - 3;
            request.Headers.TryAddWithoutValidation(DeadlineHeader, Math.Max(1, left).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body); // the bytes exactly as received: no typed DTO, so no member is dropped
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
                ? contentType
                : new MediaTypeHeaderValue(MediaTypeNames.Application.Json);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>
    /// Matches <paramref name="rawTarget"/> (path?query exactly as received) against <paramref name="route"/>. Literal segments must match
    /// exactly; each placeholder must satisfy <see cref="SegmentRules"/>. The downstream target is then the same raw text - byte for byte.
    /// </summary>
    internal static bool TryBuildDownstreamTarget(ForwardRoute route, string rawTarget, out string downstreamTarget)
    {
        downstreamTarget = string.Empty;
        var split = rawTarget.IndexOf('?', StringComparison.Ordinal);
        var rawPath = split < 0 ? rawTarget : rawTarget[..split];
        var rawQuery = split < 0 ? string.Empty : rawTarget[split..];

        if (rawQuery.Length > 2048 || rawQuery.Any(char.IsControl) || rawQuery.Contains('#'))
        {
            return false;
        }

        if (rawPath.Length == 0 || rawPath[0] != '/' || rawPath.Contains('\\') || rawPath.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var actual = rawPath[1..].Split('/');
        var expected = route.Template.Split('/');
        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].StartsWith('{'))
            {
                if (!SegmentRules.IsAcceptable(expected[i].Trim('{', '}'), actual[i]))
                {
                    return false;
                }
            }
            else if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                return false; // also rejects %2e%2e, mixed case and encoded separators in literal segments
            }
        }

        downstreamTarget = rawPath[1..] + rawQuery; // relative to the TigerCS base address
        return true;
    }

    private static Task Problem(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var payload = JsonSerializer.Serialize(new { type = $"https://tigergroup.ae/problems/{code}", title = code, status, detail, code, traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier });
        return context.Response.WriteAsync(payload, Encoding.UTF8);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
//  Registration: call from Program.cs
// ---------------------------------------------------------------------------------------------------------------------

public static class TigerCsForwardingExtensions
{
    public static IServiceCollection AddTigerCsForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TigerCsForwardingOptions>(configuration.GetSection(TigerCsForwardingOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(TigerCsForwarder.HttpClientName, (sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<TigerCsForwardingOptions>>().Value.TigerCsBaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // the per-route budget is enforced with a CancellationToken
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a redirect would carry the service token to another host
            AutomaticDecompression = DecompressionMethods.All
        });
        services.AddSingleton<ITigerCsServiceTokenProvider, TigerCsServiceTokenProvider>();
        services.AddSingleton<TigerCsForwarder>();
        return services;
    }

    /// <summary>Maps every route of <see cref="ForwardRoutes.All"/>. Call after the existing by-key mappings are removed (they are subsumed here).</summary>
    public static IEndpointRouteBuilder MapTigerCsForwarding(this IEndpointRouteBuilder app)
    {
        foreach (var route in ForwardRoutes.All)
        {
            // The template is only used for endpoint selection; the forwarder re-validates the RAW target itself.
            var pattern = route.Template;
            app.MapMethods("/" + pattern, [route.Method], (HttpContext context, TigerCsForwarder forwarder) => forwarder.ForwardAsync(context, route))
               .WithName(route.Name)
               .ExcludeFromDescription();
        }

        return app;
    }
}
```

## 5. Decisions and differences from the earlier description

* A second downstream `401` is answered `502 TigerCsAuthenticationFailed`, not `401` (see section 3, rule 2).
* Percent-encoded dot segments (`%2e%2e`) and a literal `..` are normalised by Kestrel/routing before the endpoint runs and end in **404** without reaching TigerCS; `%2F`, `%5C`, `%3F`, `%23` and double encoding in the key reach the forwarder and are answered **400**. Both mean "TigerCS was not called".
* Unknown placeholders in a new route fail closed (`SegmentRules.IsAcceptable` returns false) until a rule is written.
* The patch accepts only the 19 table routes. A new TigerCS Genesys route is not reachable until it is added to `ForwardRoutes.All` - and the build fails until the table here lists it too.
* `X-Genesys-Flow-Timeout-Seconds` is read, not forwarded. Data actions 08/09 send it (empty when unset; the parse ignores an empty value).

## 6. Acceptance script (run after deployment; none of this has been run against TigerGroupWeb)

Placeholders: `TG` public base, `GT` a valid Genesys bearer token (from the data-action-00 grant), `PHONE`, `CRM_UNIT_ID`, `KEY` a customer key such as `ext:Pact:<id>`, `CRM_CUSTOMER_ID`, `TICKET_ID`, `CODE` the emailed code.

```bash
TG=https://tigergroup.ae
GT='<genesys-access-token>'
AUTH=(-H "Authorization: Bearer $GT")
JSON=(-H 'Content-Type: application/json')
status() { curl -s -o /tmp/body.json -w '%{http_code}\n' "$@"; cat /tmp/body.json; echo; }

# --- A. Authentication: every route refuses a call without the Genesys token (expect 401 each) ---
for route in "GET /api/genesys/customers/lookup?phoneNumber=1" "POST /api/genesys/verification/otp/send" "POST /api/genesys/documents/send-copy" \
             "GET /api/genesys/collections/customers/by-key/ext%3APact%3A1/payment-summary" "POST /api/genesys/screen-pop"; do
  set -- $route; echo "$route"; status -X "$1" "${JSON[@]}" -d '{}' "$TG$2"
done

# --- B. Lookup keeps the raw query (expect 200; "phoneNumber":"+971..." in the answer, not " 971...") ---
status "${AUTH[@]}" "$TG/api/genesys/customers/lookup?phoneNumber=tel%3A%2B971<PHONE>"

# --- C. Verification and documents ---
status -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"phoneNumber":"+971<PHONE>"}' "$TG/api/genesys/verification/buyer-lookup"            # 200 Found, units[]
status -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"phoneNumber":"+971<PHONE>","crmUnitId":"<CRM_UNIT_ID>"}' "$TG/api/genesys/verification/otp/send"   # 200 CodeSent; copy challengeId
CH='<challengeId>'
curl -s -i -X POST "${AUTH[@]}" "${JSON[@]}" -d "{\"challengeId\":\"$CH\"}" "$TG/api/genesys/verification/otp/resend" | grep -iE '^HTTP|retry-after|"code"'   # 429 OTP_RESEND_TOO_SOON, Retry-After kept, problem+json body
status -X POST "${AUTH[@]}" "${JSON[@]}" -d "{\"challengeId\":\"$CH\",\"code\":\"000000\"}" "$TG/api/genesys/verification/otp/verify"            # 400 OTP_INVALID, attemptsRemaining 4
status -X POST "${AUTH[@]}" "${JSON[@]}" -d "{\"challengeId\":\"$CH\",\"code\":\"<CODE>\"}" "$TG/api/genesys/verification/otp/verify"           # 200 Verified; copy verificationSessionId
SID='<verificationSessionId>'
status -X POST "${AUTH[@]}" "${JSON[@]}" -d "{\"verificationSessionId\":\"$SID\",\"documentType\":\"Contract\"}" "$TG/api/genesys/documents/send-copy"   # 400 INVALID_REQUEST (no Idempotency-Key; the proxy must NOT invent one)
K="accept-$(date +%s):Contract:1"
status -X POST "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: $K" -d "{\"verificationSessionId\":\"$SID\",\"documentType\":\"Contract\",\"crmLeadId\":\"\"}" "$TG/api/genesys/documents/send-copy"   # 200 Sent (or SelectionRequired)
status -X POST "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: $K" -d "{\"verificationSessionId\":\"$SID\",\"documentType\":\"Contract\",\"crmLeadId\":\"\"}" "$TG/api/genesys/documents/send-copy"   # 200 duplicate:true, ONE email in the mailbox
status -X POST "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: $K" -d "{\"verificationSessionId\":\"$SID\",\"documentType\":\"UnitLayout\"}" "$TG/api/genesys/documents/send-copy"   # 409 IDEMPOTENCY_KEY_REUSED

# --- D. Unit details: unitId must survive (expect 200 mode UnitDetails; omit unitId -> UnitSelectionRequired) ---
status -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"customerReference":"crm:<CRM_CUSTOMER_ID>","phoneNumber":"tel:+971<PHONE>","unitId":<CRM_UNIT_ID>}' "$TG/api/genesys/customers/unit-details"
status -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"customerReference":"crm:<ANOTHER_CUSTOMERS_ID>","phoneNumber":"tel:+971<PHONE>","unitId":<CRM_UNIT_ID>}' "$TG/api/genesys/customers/unit-details"   # 403 CUSTOMER_NOT_VERIFIED / UNIT_NOT_ELIGIBLE, problem+json unchanged

# --- E. Collections ---
status "${AUTH[@]}" -H 'X-Genesys-Flow-Timeout-Seconds: 20' "$TG/api/genesys/collections/customers/by-key/ext%3APact%3A<ID>/payment-summary?includeTransactions=false"   # body contains nextPayment exactly as a direct TigerCS call
status "${AUTH[@]}" "$TG/api/genesys/collections/customers/by-key/ext%3APact%3A<ID>/payment-transactions?companyId=<COMPANY>&type=Paid"
for bad in 'ext%2Fx' 'a%3Fb' 'a%5Cb' 'a%252Fb' '%2e%2e'; do   # expect 400 (or 404 for dot segments); TigerCS access log shows NO request
  echo "$bad"; status "${AUTH[@]}" --path-as-is "$TG/api/genesys/collections/customers/by-key/$bad/payment-summary"
done
status "${AUTH[@]}" "$TG/api/genesys/collections/customers/<CRM_CUSTOMER_ID>/outstanding"     # 503 FinanceUnavailable relayed unchanged (no financial source yet)

# --- F. Tickets ---
status -X PATCH "${AUTH[@]}" "${JSON[@]}" -d '{"conversationId":"<CONV>","awaitingCustomerReply":true}' "$TG/api/genesys/tickets/<TICKET_ID>"    # 200, inactivityDeadlineUtc unchanged
status -X PATCH "${AUTH[@]}" "${JSON[@]}" -d '{}' "$TG/api/genesys/tickets/12x"                                                                     # 400, TigerCS not called
status -X DELETE "${AUTH[@]}" "$TG/api/genesys/tickets/<TICKET_ID>"                                                                                 # 405

# --- G. Agent surface ---
status -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"genesysUserId":"<AGENT_GUID>","conversationId":"<CONV>"}' "$TG/api/genesys/agent-context"          # 200 / 403 for an unmapped agent
curl -s -i -X POST "${AUTH[@]}" "${JSON[@]}" -d '{"genesysUserId":"<AGENT_GUID>"}' "$TG/api/genesys/screen-pop" | grep -iE '^HTTP|cache-control'    # 200, Cache-Control: no-store; confirm launchUrl is in no log

# --- H. Service account renewal: expire/revoke the cached service token (restart TigerCS or wait for expiry), repeat C: still 200 ---
# --- I. Budget: with TigerCS delayed beyond CollectionsTimeoutSeconds, E returns 504 problem+json (GatewayTimeout); cancelling the curl cancels the TigerCS request (access log) ---
```

## 7. What was and was not verified

**Verified in isolation (scratch ASP.NET Core project, .NET 10 SDK, fake TigerCS, `curl`):** the code in section 4 compiles with warnings as errors; missing or wrong Genesys token -> 401 `problem+json` with `WWW-Authenticate`; the lookup query reaches the downstream byte-for-byte (`%3A%2B` intact); `ext%3APact%3A3001` is forwarded as received, `%2F`, `%3F`, `%5C` and double-encoded keys -> 400 without a downstream call, a non-numeric ticket id -> 400, `DELETE` -> 405; POST/PATCH bodies arrive byte-for-byte including unknown members; `Idempotency-Key` is forwarded when present and absent when not (not invented); a downstream `429` with `Retry-After: 42` and `application/problem+json` reaches the caller unchanged; a downstream `401` triggers one renewal and a single retry carrying the same key and body; a spent budget -> 504; `X-Collections-Deadline-Seconds` = budget - elapsed - 3.

**Not verified (`[ext]` or out of reach):** integration with TigerGroupWeb's real Genesys token validation, its existing routing/middleware (route clashes with the current by-key and PATCH endpoints, authentication middleware order, reverse-proxy buffering and size limits in front of it), its HttpClient/Polly policies, Genesys Cloud's Velocity/JSONPath behaviour and failure-path output, TLS and network paths to TigerCS, and every acceptance step in section 6.
