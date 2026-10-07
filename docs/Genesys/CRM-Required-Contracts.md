# Tiger CRM — contracts TigerCS needs for chatbot verification and document copies

**Audience:** the CRM team. **Status:** specification only; nothing in this document is implemented on the CRM side.
**Feature status:** the document-copy feature is **PENDING REAL UAT VERIFICATION** (see [§8](#8-status-and-exit-criteria)).

> **Phase 1 decision (implemented): buyers requesting their own documents, built on `GetBuyerByPhone` alone.** TigerCS caches the
> verification unit/contact from the buyer lookup (§5, "buyers-only"), verifies with a server-side **email OTP** to the address CRM
> returns (§2 — no longer a gap on TigerCS's side), and uses `GetCustomerDocuments` + the file routes (§6). **The CRM team does not
> need to build §4 (`GetUnit`, `SearchUnits`, `GetUnitContacts`) for this phase**; §4 remains the contract for tenants, representatives
> and unit-number lookup later. What CRM still owes for Phase 1: the §7 confirmations (email/phone data quality, file-route
> authentication) and, for real UAT, a buyer with a usable email.

Every shape below was taken from the code TigerCS actually compiles against, and every JSON sample lives as a file in
[`crm-contracts/`](crm-contracts/) that an automated test (`CrmContractSamplesTests`) deserialises into those C# types. No CRM database
field is assumed: where CRM already returns a value via `GetBuyerByPhone` it is named; where it does not, that is stated and the
field is left optional or null.

## 1. What the code does today (so the gap is exact)

| Fact | Where |
|---|---|
| A verification session is created by `POST /api/verification-sessions` from a **`UnitReferenceId` and `ContactReferenceId`** — rows in TigerCS's own `UnitReferences` / `ContactReferences` cache. Nothing else creates those rows. | `VerificationSessionAppService`, `CrmUnitLookupAppService` |
| The cache is filled **only** by `ICrmGateway.GetUnitAsync / SearchUnitsAsync / GetContactsAsync`, reached through `GET /api/crm/units/{id}`, `GET /api/crm/units/search`, `GET /api/crm/units/{id}/contacts`. | `CrmController`, `CrmUnitLookupAppService` |
| With `Crm:Provider = Http` (UAT, Production) `ICrmGateway` is `UnimplementedCrmHttpGateway`, which **throws for all three operations**. So no unit/contact row can be cached, so **no verification session can be created** against real CRM. This is the blocker. | `UnimplementedCrmHttpGateway` |
| `UnitReferences.CrmUnitId` and `ContactReferences.CrmContactId` are **globally unique** (nvarchar(64)); a contact row belongs to exactly one unit and its unit link is never changed after the first insert. A contact id reused across two units therefore fails session creation for the second unit (`UnitOrContactNotFound`). | `UnitReferenceConfiguration`, `ContactReferenceConfiguration`, `UpsertContactAsync` |
| The document-copy flow resolves the CRM customer from the **verified contact's phone** (`contactChannel`) through the existing `GetBuyerByPhone`, then matches the verified unit to one of that customer's leads (by `unitId`, else unit number + project). | `CrmDocumentCopyAppService` |

## 2. Identity proof — now built in TigerCS (Phase 1)

When this document was first written, `POST /api/verification-sessions` took `confirmed: true` and `verificationMethod` from the caller, so
a session labelled `Otp` was only an assertion; neither TigerCS nor CRM checked a code. **That is closed for the chatbot document flow:**
TigerCS now issues the code itself (`/api/genesys/verification/otp/send`), checks it on the server (`/otp/verify`), and creates the
`Otp` session only from a verified challenge; the generic endpoint refuses `Otp`, and `send-copy` accepts only a session that carries the
recorded proof. Requires no CRM endpoint: the code goes to `customer.email` from `GetBuyerByPhone`. Delivery is email only (SMTP);
there is no SMS/WhatsApp sender.

What it still depends on is **CRM's data**: the proof is "the person can read the mailbox CRM holds", so the email on record must be
the customer's own and current (§7 item 4). Details: [`Document-Copy-API.md`](Document-Copy-API.md).

## 3. Authentication, transport, conventions (all operations)

* Base: `Crm:BaseUrl` (today `https://tigercrm.tigergroup.ae:8014`). HTTPS.
* **Auth: header `X-SECRET-KEY: <shared secret>`** — the same secret and the same check `TicketingSystem/GetBuyerByPhone` already
  uses (`TicketingSecretKey` on the CRM side; `Crm:SecretKey` / `Crm__SecretKey` in TigerCS). No new credential. Server-to-server only.
* **Missing or wrong secret → `401` with a JSON body, never a `302` to a login page and never `200` HTML.** TigerCS treats HTML/redirects as an invalid response.
* Same envelope as `GetBuyerByPhone`: JSON, camelCase, `{ "success": bool, "found": bool, "message": string|null, … }`.
* **"Not found" is `200` with `found: false`** (and empty collections) — never a `404`. A `404` must mean the *route* does not exist, so TigerCS can tell "CRM lacks the endpoint" from "no such unit". (`GetBuyerByPhone`'s `found` flag is the precedent.)
* Read-only, idempotent, no side effects. TigerCS will use a 15 s timeout, as for `GetBuyerByPhone`; please answer well under 10 s.
* **Route names `GetUnit`, `SearchUnits`, `GetUnitContacts` are proposals** following the existing `TicketingSystem/…` naming — no CRM route for them exists today. If CRM prefers other names, return them to us and the (not yet written) TigerCS HTTP gateway follows; the parameters, envelope and field rules are the contract.

## 4. The three operations the code expects (`ICrmGateway`)

For the chatbot flow only 4.1 and 4.3 are required (the customer's units come from `GetBuyerByPhone`; the unit id is then known). 4.2 serves the agent desk (unit number heard on a call).

### 4.1 Get unit — `GET /TicketingSystem/GetUnit?crmUnitId={string}`

Maps to `ICrmGateway.GetUnitAsync` → `CrmUnitResult(CrmUnitId, UnitNumber, PropertyName, TowerName, UnitType)`.

| Field | Type | Required | Rule |
|---|---|---|---|
| `crmUnitId` | string ≤ 64 | **yes** | CRM's immutable unit id. **Must equal `String(unitId)` as returned by `GetBuyerByPhone`** — that equality is how the document flow matches the verified unit to the customer's lead. |
| `unitNumber` | string 1–50 | **yes** | As spoken/displayed. |
| `propertyName` | string ≤ 200 | no | = `GetBuyerByPhone` `projectName`. |
| `towerName` | string ≤ 200 | no | Display only. `GetBuyerByPhone` has **no** tower field — send only if CRM already holds one. |
| `unitType` | string ≤ 50 | no | A label (e.g. "Residential"). `GetBuyerByPhone` gives only a numeric `unitType` code with no label — **do not send the number as a label**; send a label only if CRM already has one. |

`200` found — [`get-unit.200.json`](crm-contracts/get-unit.200.json):
```json
{ "success": true, "found": true, "message": null,
  "unit": { "crmUnitId": "1101", "unitNumber": "1205", "propertyName": "Tiger Sky Tower", "towerName": "Tower 1", "unitType": "Residential" } }
```
`200` not found — [`get-unit.not-found.200.json`](crm-contracts/get-unit.not-found.200.json): `{ "success": true, "found": false, "message": "No unit with this id.", "unit": null }`

### 4.2 Search units — `GET /TicketingSystem/SearchUnits?unitNumber={string}&propertyName={string?}`

Maps to `ICrmGateway.SearchUnitsAsync` → `IReadOnlyList<CrmUnitResult>`. `unitNumber` required; `propertyName` optional narrowing. Match semantics (exact/substring) are CRM's to state; **exact match on `unitNumber`** is what the agent flow assumes. Several results are normal (same number in different projects).

`200` — [`search-units.200.json`](crm-contracts/search-units.200.json): `{ "success": true, "found": true, "message": null, "units": [ <unit as 4.1>, … ] }`
No match — [`search-units.none.200.json`](crm-contracts/search-units.none.200.json): `{ "success": true, "found": false, "message": null, "units": [] }`

### 4.3 Get unit contacts — `GET /TicketingSystem/GetUnitContacts?crmUnitId={string}`

Maps to `ICrmGateway.GetContactsAsync` → `IReadOnlyList<CrmContactResult(CrmContactId, DisplayName, ContactChannel, ContactType, AuthorizedRepresentativeOfCrmContactId)>`.

| Field | Type | Required | Rule |
|---|---|---|---|
| `crmContactId` | string ≤ 64 | **yes** | **Unique per person-on-unit, not per person.** If CRM's natural key is the customer id, return a unit-scoped id (e.g. `"{customerId}-{unitId}"` → `"9001-1101"`); the same customer on two units must get two different ids. Stable across calls. |
| `displayName` | string ≤ 200 | no | Read back to the caller. |
| `contactChannel` | string ≤ 200 | no (**yes for Owner**) | For an `Owner`, the customer's **phone number** — the document flow looks the customer up by it, so it must be a number `GetBuyerByPhone` would find the same customer by (`+971…`). Email is not usable there. |
| `contactType` | `"Owner"` \| `"Tenant"` \| `"Representative"` | **yes** | String, exactly these three. Only `Owner` can obtain documents. |
| `authorizedRepresentativeOfCrmContactId` | string \| null | when `Representative` | The `crmContactId` (from this same response) of the owner/tenant represented — from CRM's own authorization record, never self-declared. Null otherwise. |

`200` — [`get-unit-contacts.200.json`](crm-contracts/get-unit-contacts.200.json); unknown unit — [`get-unit-contacts.unit-not-found.200.json`](crm-contracts/get-unit-contacts.unit-not-found.200.json) (`found:false`, `contacts: []`). A **known unit with no contacts** is `found: true, contacts: []`.

### 4.4 Errors (all three)

| Condition | Response | TigerCS behaviour |
|---|---|---|
| Missing/blank required parameter | `400` + `{ "success": false, "message": "crmUnitId is required." }` ([`error.problem.json`](crm-contracts/error.problem.json)) | CRM-invalid-request; never retried; logged as a TigerCS defect. |
| Missing/wrong `X-SECRET-KEY` | `401` + same JSON error body | Operations alert: `Crm:SecretKey` wrong. |
| Secret valid but not permitted | `403` + JSON body (optional) | As 401, distinct log. |
| Unknown unit / no match | `200`, `found:false` | `404` from `/api/crm/units/{id}`; empty list from search. |
| `success: false` on a `200` | body `{ "success": false, "message": … }` | Treated as unavailable. |
| Server error / dependency down / timeout | `500` / `503` | `502 crm-unavailable`; the agent/bot may retry. |

## 5. What `GetBuyerByPhone` already satisfies — and the option that needs no new CRM endpoint

`GET /TicketingSystem/GetBuyerByPhone?phoneNumber=` (existing, header `X-SECRET-KEY`) — sample [`get-buyer-by-phone.200.json`](crm-contracts/get-buyer-by-phone.200.json).

| Needed by the verification/document code | `GetBuyerByPhone` field | Verdict |
|---|---|---|
| `CrmUnitResult.CrmUnitId` | `units[].unitId` (int → `"1101"`) | ✅ |
| `UnitNumber` | `units[].unitNumber` (nullable in the response; **must be non-empty**) | ✅ if present |
| `PropertyName` | `units[].projectName` | ✅ |
| `TowerName` | — | ❌ none (leave null) |
| `UnitType` (label) | `units[].unitType` is a numeric code only | ❌ no label; leave null unless CRM supplies the mapping |
| `CrmContactResult.CrmContactId` | `customer.customerId` | ⚠️ not unit-scoped → derive `"{customerId}-{unitId}"` (see §4.3) |
| `DisplayName` | `customer.fullNameEnglish` (or Arabic) | ✅ |
| `ContactChannel` | `customer.mobileNumber` | ✅ (phone; also `customer.email`) |
| `ContactType = Owner` | `units[].customerType = 1` / `customerTypeName = "Buyer"` | ⚠️ "Buyer ⇒ Owner" is a business statement CRM must confirm (§7) |
| `Tenant`, `Representative`, `authorizedRepresentativeOf…` | — | ❌ not available |
| Document flow: `CustomerID`, `LeadID`, customer email | `customer.customerId`, `units[].leadId`, `customer.email` | ✅ |
| Lookup **by unit id or unit number** | — (phone-keyed only) | ❌ 4.1 / 4.2 cannot be served from it |

**Consequence.** For a *buyer calling from their own registered number* everything verification needs is already in `GetBuyerByPhone`
except tower, unit-type label, tenants and representatives. **If the chatbot scope is buyers/owners only, CRM needs to build nothing**:
TigerCS can cache `UnitReferences`/`ContactReferences` from the buyer lookup (a TigerCS change; it does not weaken verification, since the
session still requires proof — §2). Tenants, representatives, and by-unit-number lookup need 4.1–4.3. **Decision needed from you:**
buyers-only (no CRM work; TigerCS builds the cache path) or full 4.1–4.3.

## 6. File-download contract (what `CrmDocumentHttpGateway` does with each `fileUrl`)

`GetCustomerDocuments` (already agreed — sample [`get-customer-documents.200.json`](crm-contracts/get-customer-documents.200.json)) returns `fileUrl` per attachment. TigerCS downloads that reference itself, once, inside the request that sends the email. Exactly:

**Request**
* `GET <fileUrl resolved>` — no body, no cookies, no query added.
* **Header `X-SECRET-KEY: <same shared secret>`.** This is the only credential TigerCS has; if the file route cannot validate it, downloads fail with 401/403.
* `fileUrl` forms TigerCS accepts: absolute `https://…` on the **same scheme, host and port as `Crm:BaseUrl`**; root-relative `/Uploads/x.pdf`; relative `Uploads/x.pdf`; legacy `~/Uploads/x.pdf`. Everything else is refused **without sending the secret**: other hosts or ports, `http` when the base is `https`, protocol-relative `//host/…`, `file:`, `ftp:`, `javascript:`. A different file host is possible only if it is **https** and listed in TigerCS's `Crm:DocumentFileHosts` — tell us the host. **TigerCS never sends `X-SECRET-KEY` to such a host** (it is not CRM); it is fetched without credentials, so it must serve the file by an unguessable or pre-signed URL. Only the CRM origin receives the secret.
* Redirects are **not followed** (a redirect could carry the secret to another host).
* The reference must stay valid for at least several minutes (TigerCS lists then downloads inside one request); it need not be permanent. It is never logged, stored or returned to Genesys.

**Response**
| Case | Required response | TigerCS result |
|---|---|---|
| File available | `200`, body = the **raw file bytes** (no JSON, no base64). **Not `text/html`.** `Content-Length` recommended. ≤ 10 MB (`Crm:MaxDocumentBytes`). TigerCS decides the real type **from the bytes** (PDF, PNG, JPEG, GIF, WebP, TIFF; DOC/DOCX/BMP when a declared type or name agrees) and rejects anything it cannot establish as a document — so `Content-Type` and the name's extension are hints, not requirements. | Emailed as an attachment named from the attachment `name` (extension replaced by the true one: `Layout Plan` → `Layout Plan.png`). `Content-Disposition` filename is used only as a hint. |
| File no longer exists | `404` | "document no longer available" — the send is marked failed, no email. |
| Wrong/missing secret | `401` (never `302`/login HTML) | `CRM_AUTHENTICATION_FAILED` (502). |
| Secret valid, not allowed | `403` | `CRM_ACCESS_DENIED` (502). |
| Malformed request | `400` | `CRM_REQUEST_REJECTED` (502). |
| Redirect (3xx) | — | refused: `CRM_INVALID_RESPONSE`. |
| HTML body with `200` (a login/error page) | — | refused: `CRM_INVALID_RESPONSE`. |
| `500`/`503`/timeout (30 s) | | `DOCUMENT_SOURCE_UNAVAILABLE`, retryable with the same `Idempotency-Key`. |
| Larger than 10 MB | | `DOCUMENT_TOO_LARGE`, not sent. |

**Authentication is the open question — please confirm which holds today for the `Uploads` routes behind `fileUrl`:** (1) public static files, no auth; (2) cookie/forms auth; (3) none but unguessable URLs; (4) something that checks `X-SECRET-KEY`. TigerCS works with (1)/(3)/(4) as is; (2) cannot work — it needs a server route that validates `X-SECRET-KEY`. Quick CRM-side probe: `curl -i <fileUrl>` without the header must not return the file for any private document; `curl -i -H "X-SECRET-KEY: …" <fileUrl>` must return `200` + the file.

**Authorization model to accept:** the shared secret authorises reading *any* file route; TigerCS enforces ownership by only ever fetching references that `GetCustomerDocuments` just returned for the verified customer and lead. If CRM wants that enforced on its side too, the stronger option is a download route keyed by ids, e.g. `GET /TicketingSystem/GetCustomerDocumentFile?customerId=&leadId=&documentType=&attachmentId=` (CRM re-checks the attachment belongs to that lead/customer; Layout has no `attachmentId`). That is a small TigerCS change once the route exists; say if you prefer it.

## 7. Confirmations we need from CRM (no field is assumed)

1. `crmUnitId` = `String(unitId)` of `GetBuyerByPhone` (§4.1) — yes/no.
2. Unit-scoped `crmContactId` for owners (§4.3) — what id will you return?
3. `customerType = 1 / "Buyer"` means the person **owns** the unit (`contactType: "Owner"`) — yes/no.
4. `customer.mobileNumber` and `customer.email` are the customer's own, current, verified contact details, and `mobileNumber` is returned in a form `GetBuyerByPhone?phoneNumber=+971…` finds it by (E.164, or state the stored format).
5. Tenants and representatives in scope for the chatbot? (If not, buyers-only needs no CRM build — §5.)
6. File route authentication, and the file host if it is not the CRM host (§6).
7. Whether `GetCustomerDocuments` can return the same `fileUrl` for repeated calls (stable for minutes).

## 8. Status and exit criteria

**The document-copy feature is PENDING REAL UAT VERIFICATION.** Everything below has been verified only against stubs and fixtures (3 000+ automated tests); nothing has run against real CRM, the real file routes, real SMTP, or the public Genesys route. It stays `CrmDocuments:Enabled=false` by default and must be described as pending until all of this has been observed in UAT and recorded in `docs/releases/UAT-Chatbot-Inactivity-And-Document-Copy.md`:

1. A real UAT customer **completes verification** through the public route — `buyer-lookup`, `otp/send`, the email arrives in their mailbox, `otp/verify` returns the session (the OTP is TigerCS's own, §2).
2. They **select a unit** (a customer with ≥ 2 units, or the matched verified unit).
3. They **select a document** (a lead with ≥ 2 documents → `SelectionRequired`, then the chosen `recordId`), or the single document is resolved.
4. **Exactly one email** with the correct attachment reaches that customer's mailbox — via the public Genesys → TigerGroupWeb → TigerCS route (`POST /api/genesys/documents/send-copy`), with the retry of the same `Idempotency-Key` sending nothing more.
5. Negative checks recorded: another customer's lead refused, wrong secret → `CRM_AUTHENTICATION_FAILED`, document with nothing on record → 404.
