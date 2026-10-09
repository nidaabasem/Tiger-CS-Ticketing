# Customer unit and project details API (chatbot / voicebot)

Lets the bot answer "tell me about my unit" for a **verified** customer, from
Tiger CRM data, without ever disclosing another customer's unit: the unit, its
project, construction completion, expected/actual handover, and — for a customer
who has passed strong verification — their own recorded sale (actual sold price
and registration cost).

> **Status, stated plainly (2026-10-08).** The TigerCS side (this repository) is
> built and unit/integration-tested. **Tiger CRM's `GetUnitDetails` action and the
> TigerGroupWeb route are not in any repository available to the author, so neither
> was built, verified or exercised.** Until they are, production returns only the
> buyer-lookup fields; every other value, including the sale, is `null`. See
> [Status and what is still unavailable](#status-and-what-is-still-unavailable).

## Endpoint

```
POST /api/genesys/customers/unit-details
Authorization: Bearer <TigerCS service-account token>     (same as every api/genesys route)
Content-Type: application/json
```

Read-only. It is a POST only so the customer's number stays out of URLs and
access logs.

**Authentication and proxy.** Identical to the other Genesys contracts: Genesys
authenticates to TigerGroupWeb (OAuth client credentials, scope
`ticketing.genesys`), and TigerGroupWeb forwards the request to TigerCS as the
TigerCS service account, policy `CustomerVerification` (CS Agent / CS
Supervisor). Genesys never holds TigerCS credentials.
**TigerGroupWeb forwards a fixed list of routes, so it must be taught this
route (not done — its source was not accessible)** (`POST /api/genesys/customers/unit-details`, body, status and response passed
through unchanged, reusing the existing OAuth policy, `TicketingOptions` and Ticketing service-account forwarding —
no new login configuration, and CRM credentials never reach Genesys) before the Data Action `TigerCS - Customer Unit Details`
can work. That change is outside this repository.

Public URL for Genesys: `https://tigergroup.ae/api/genesys/customers/unit-details`.

The feature flag `Genesys:Enabled` governs it like every Genesys route
(`503` when off).

## Request

| Field | Type | Required | Meaning |
|---|---|---|---|
| `customerReference` | string | yes | The verified CRM customer: `crm:9001`, or the plain id `9001` (the lookup's `externalCustomerId`). Non-CRM references (`ext:…`, `phone:…`) are rejected. |
| `phoneNumber` | string | yes | The verified number the customer was identified by (`tel:+971…`, `+971…`, `971…`). It is how CRM is asked which units the customer owns. |
| `unitId` | integer | no | The CRM unit id the customer selected (from a previous `UnitSelectionRequired` answer). Omit it to get the eligible units. |
| `verificationSessionId` | GUID | no | A confirmed verification session (`POST /api/verification-sessions`, method `Otp` or `AuthenticatedDigitalUser`, owned by the calling service account, unexpired, **for this unit**). Needed **only** for the private sale fields. Without it the response is still `200` with unit/project data and `financialDetailsStatus: "VerificationRequired"`. |

### Workflow

1. **Customer lookup** — `GET customers/lookup` (Data Action 01) returns `externalCustomerId` for the caller's number.
2. **Unit selection** — call this route with `customerReference` + `phoneNumber` and no `unitId`; read `eligibleUnits`; ask the customer which one.
3. **Verification** — for sold price / registration cost, run the existing strong verification (one-time code, or an authenticated digital user) for the chosen unit and keep its `verificationSessionId`. Unit, project, completion and handover facts do not need this step.
4. **Unit details** — call again with `unitId` (and `verificationSessionId` if the customer asked about price or fees).

## How access is decided (server-side, every call)

1. `phoneNumber` is looked up in CRM through the existing Buyer lookup (the
   same service the New Ticket wizard and `customers/lookup` use). CRM
   guarantees one customer per number.
2. That customer's id must equal `customerReference`, otherwise `403 CUSTOMER_NOT_VERIFIED`.
3. `unitId` must be one of that customer's own Buyer units (Sold/Contract, as CRM
   filters them), otherwise `403 UNIT_NOT_ELIGIBLE`. A unit that belongs to
   someone else and a unit that does not exist produce the **same** answer, so
   ids cannot be probed. A unit id alone never grants access.
4. Only then are unit and project details assembled. The response carries no
   contact details, no other units' details, and no CRM notes.
5. **Sale (sold price, registration cost) additionally requires proof.** A phone
   number or customer id typed by the caller is not proof. The server loads the
   `verificationSessionId` it recorded itself and releases the sale only if the
   session is confirmed, owned by the calling service account, not expired, by an
   accepted strong method (`CrmDocuments:AcceptedVerificationMethods`, default
   `Otp`, `AuthenticatedDigitalUser` — the same policy as document copies) **and
   belongs to this unit**. Unknown, foreign, unconfirmed, expired, weak-method or
   other-unit sessions all give the same answer, `VerificationFailed`. Without proof
   CRM is not even asked for the sale (`includeSale` is not sent).
6. **Sale binding.** CRM is asked for the sale of the Lead the buyer lookup bound to
   this unit (`leadId`). CRM must echo that `leadId`; if it echoes another Lead or
   none, the sale is discarded (`NotAvailable`) — another buyer's sale or an old
   booking is never shown.

## Response

Two modes, in `mode`:

* `UnitSelectionRequired` — no `unitId` sent. `eligibleUnits` lists the customer's
  units so the bot can ask which one. `unit`, `project`, `handoverDateSource`,
  `detailsStatus` are `null`.
* `UnitDetails` — `unit`, `project`, `handoverDateSource`, `detailsStatus`, `sale`,
  `financialDetailsStatus` filled; `eligibleUnits` is `[]`.

Unavailable values are `null`. Nothing is defaulted, guessed or derived. A genuine
`0` (price, fee, completion %) is returned as `0`, never as `null`. The new members
(`project.completionPercentage`, `project.expectedCompletionDate`,
`project.actualCompletionDate`, `sale`, `financialDetailsStatus`) are additive; every
pre-existing member is unchanged.

**Currency.** `sale.soldPrice` and `sale.registrationCost` are decimals
(`{ "amount": 1850000.50, "currency": "AED" }`) in the currency CRM records for the
sale. `currency` is `null` when CRM does not state one — TigerCS never assumes AED.

**`financialDetailsStatus`** (independent of `detailsStatus`): `Available` — CRM
answered for this customer's own sale (an individual amount may still be `null`);
`VerificationRequired` — no `verificationSessionId`; `VerificationFailed` — session
invalid/expired/weak/other unit; `NotAvailable` — CRM has no sale data, or it could not
be tied to the customer's Lead; `Unavailable` — CRM unreachable. `null` in
`UnitSelectionRequired` mode.

### Sample: no unit selected (customer owns two units)

```json
{
  "mode": "UnitSelectionRequired",
  "customerReference": "crm:9001",
  "eligibleUnits": [
    { "unitId": 9200, "unitNumber": "1204", "projectId": 79, "projectName": "Tiger Tower",   "floor": 12, "bookingStatus": "Sold" },
    { "unitId": 9201, "unitNumber": "0507", "projectId": 80, "projectName": "Tiger Heights", "floor": 5,  "bookingStatus": "Contract" }
  ],
  "unit": null,
  "project": null,
  "handoverDateSource": null,
  "detailsStatus": null
}
```

### Sample: unit selected, verified, CRM answered (unit-level handover recorded)

```json
{
  "mode": "UnitDetails",
  "customerReference": "crm:9001",
  "eligibleUnits": [],
  "unit": {
    "unitId": 9200,
    "unitNumber": "1204",
    "tower": "Tower A",
    "floor": 12,
    "unitType": { "code": 2, "name": "Apartment" },
    "bedrooms": 2,
    "area": { "value": 1250.5, "unit": "sqft" },
    "booking": { "reference": "9100", "status": "Sold", "statusCode": 8 },
    "parking": [ { "number": "P-114", "level": "P1", "type": null } ],
    "expectedHandoverDate": "2027-06-30",
    "actualHandoverDate": null
  },
  "project": {
    "projectId": 79,
    "name": "Tiger Tower",
    "arabicName": null,
    "address": "Dubai, UAE",
    "status": "Under construction",
    "expectedHandoverDate": "2027-03-31",
    "actualHandoverDate": null,
    "description": "A residential tower.",
    "amenities": ["Pool", "Gym"],
    "completionPercentage": 62.5,
    "expectedCompletionDate": "2027-01-31",
    "actualCompletionDate": null
  },
  "handoverDateSource": "Unit",
  "detailsStatus": "Available",
  "sale": {
    "soldPrice": { "amount": 1850000.50, "currency": "AED" },
    "registrationCost": { "amount": 74000, "currency": "AED" }
  },
  "financialDetailsStatus": "Available"
}
```

(Illustrative values only — they are the test fixture, not Tiger data.)

### Sample: partial data — no `verificationSessionId`

Same as above but `"sale": null` and `"financialDetailsStatus": "VerificationRequired"`
(or `"VerificationFailed"` for a bad/expired session). Everything else is unchanged.

### Sample: partial data — CRM could not be reached for the details

```json
{
  "mode": "UnitDetails", "customerReference": "crm:9001", "eligibleUnits": [],
  "unit": { "unitId": 9200, "unitNumber": "1204", "tower": null, "floor": 12,
            "unitType": { "code": 2, "name": null }, "bedrooms": null, "area": null,
            "booking": { "reference": "9100", "status": "Sold", "statusCode": 8 },
            "parking": null, "expectedHandoverDate": null, "actualHandoverDate": null },
  "project": { "projectId": 79, "name": "Tiger Tower", "arabicName": null, "address": null,
               "status": null, "expectedHandoverDate": null, "actualHandoverDate": null,
               "description": null, "amenities": null,
               "completionPercentage": null, "expectedCompletionDate": null, "actualCompletionDate": null },
  "handoverDateSource": null,
  "detailsStatus": "Unavailable",
  "sale": null,
  "financialDetailsStatus": "Unavailable"
}
```

### Sample: error (unit belongs to someone else or does not exist)

```json
{
  "type": "https://tigercs.internal/problems/genesys-unit-not-eligible",
  "title": "The unit is not available to this customer",
  "status": 403,
  "detail": "The requested unit is not one of this customer's units.",
  "code": "UNIT_NOT_ELIGIBLE"
}
```

### Sample: what production returns today (only the buyer lookup answers)

```json
{
  "mode": "UnitDetails",
  "customerReference": "crm:9001",
  "eligibleUnits": [],
  "unit": {
    "unitId": 9200, "unitNumber": "1204", "tower": null, "floor": 12,
    "unitType": { "code": 2, "name": null },
    "bedrooms": null, "area": null,
    "booking": { "reference": "9100", "status": "Sold", "statusCode": 8 },
    "parking": null, "expectedHandoverDate": null, "actualHandoverDate": null
  },
  "project": {
    "projectId": 79, "name": "Tiger Tower", "arabicName": null, "address": null,
    "status": null, "expectedHandoverDate": null, "actualHandoverDate": null,
    "description": null, "amenities": null,
    "completionPercentage": null, "expectedCompletionDate": null, "actualCompletionDate": null
  },
  "handoverDateSource": null,
  "detailsStatus": "NotAvailable",
  "sale": null,
  "financialDetailsStatus": "VerificationRequired"
}
```

## Expected vs actual, and unit vs project handover dates

Four separate fields, never merged: `unit.expectedHandoverDate`,
`unit.actualHandoverDate`, `project.expectedHandoverDate`,
`project.actualHandoverDate` (ISO `yyyy-MM-dd`, or `null`). An actual date is
only present when CRM recorded one; an expected date never fills an actual
date or the reverse.

`handoverDateSource` tells the bot which pair to speak from: `"Unit"` when CRM
recorded either date on the unit itself, else `"Project"` when it recorded one
on the project, else `null`. This only chooses between recorded values; it
never produces a date. (Treating the unit-level date as taking precedence is a
presentation rule chosen here — change it in
`GenesysCustomerUnitDetailsAppService.Map` if the business decides otherwise.)

## Completion is not handover

`project.completionPercentage`, `project.expectedCompletionDate` and
`project.actualCompletionDate` describe **construction completion**.
`expectedHandoverDate` / `actualHandoverDate` (unit and project) describe **handover to
customers**. They are separate fields and are never filled from, substituted for, or
compared with each other. Likewise expected never fills actual, and a registration
**receipt document** (document copy flow) is not the registration **cost** here.

## Where each field comes from — read this before UAT

Tiger CRM publishes **one** endpoint to TigerCS today, `GET /TicketingSystem/GetBuyerByPhone`.
No field was invented for the rest.

| Field | Source today | Production value today |
|---|---|---|
| `unitId`, `unitNumber`, `floor` | CRM buyer lookup (`UnitId`, `UnitNumber`, `FloorNumber`) | real |
| `unitType.code` | buyer lookup (`UnitType`, numeric; CRM's code table is not published) | real |
| `booking.reference` | buyer lookup `LeadId` — the CRM Lead the unit was sold/contracted through | real |
| `booking.status` / `statusCode` | buyer lookup `LeadStatusName` / `LeadStatus` | real |
| `project.projectId`, `name`, `arabicName` | buyer lookup | real |
| `unit.tower`, `unitType.name`, `bedrooms`, `area` (+ unit), `parking`, unit-level handover dates | CRM `GetUnitDetails` (not yet built) | **null** until CRM deploys it |
| `project.address`, `status`, handover dates, `description`, `amenities` | CRM `GetUnitDetails` (not yet built) | **null** until CRM deploys it |
| `project.completionPercentage`, `expectedCompletionDate`, `actualCompletionDate` | CRM `GetUnitDetails` (not yet built) | **null** until CRM deploys it |
| `sale.soldPrice`, `sale.registrationCost` (+ currency) | CRM `GetUnitDetails` `unit.sale` (not yet built), only with valid verification proof | **null** until CRM deploys it |

## CRM contract (implemented by Tiger CRM — status: NOT YET BUILT OR VERIFIED)

`ICrmUnitDetailsGateway` is the read-only port for the "ICrmUnitDetailsGateway"
rows above. With `Crm:Provider = "Http"` (every real environment) it resolves
`CrmUnitDetailsHttpGateway`, which calls the route below on the same
`Crm:BaseUrl` with the same `X-SECRET-KEY` as `GetBuyerByPhone`. `Crm:Provider = "Mock"`
(test host only) serves fixtures.

```
GET {Crm:BaseUrl}/TicketingSystem/GetUnitDetails?customerId={int}&unitId={int}
X-SECRET-KEY: <Crm:SecretKey>
```

The CRM implementation **must itself check that `unitId` belongs to `customerId`**
with CRM's existing Buyer rules (Lead Sold/Contract, CustomerType Buyer — the
rules behind `GetBuyerByPhone`) and answer `404`/`found:false` otherwise. Ticketing
checks too; CRM must not rely on that.

```json
{
  "success": true, "found": true, "message": null,
  "unit": {
    "unitTypeName": "Apartment", "towerName": "Tower A", "bedrooms": 2,
    "area": 1250.5, "areaUnit": "sqft",
    "parking": [ { "number": "P-114", "level": "P1", "type": null } ],
    "expectedHandoverDate": "2027-06-30", "actualHandoverDate": null,
    "project": {
      "address": "Dubai, UAE", "status": "Under construction",
      "expectedHandoverDate": "2027-03-31", "actualHandoverDate": null,
      "description": "A residential tower.", "amenities": ["Pool", "Gym"],
      "completionPercentage": 62.5, "expectedCompletionDate": "2027-01-31", "actualCompletionDate": null
    },
    "sale": { "leadId": 9100, "soldPrice": 1850000.50, "registrationCost": 74000, "currency": "AED" }
  }
}
```

Full query string Ticketing sends:
`GetUnitDetails?customerId={int}&unitId={int}&leadId={int}[&includeSale=true]`.
`leadId` is the Lead `GetBuyerByPhone` returned for the unit. **`unit.sale` must be
omitted unless `includeSale=true`**, must be read from that Lead (not from the unit's
list price, not from another buyer's or an older booking), and must echo `leadId`.
`registrationCost` is the fee **amount** stored for the sale — never computed from a
percentage by CRM or TigerCS. Amounts: `0` is a real value, a missing one is `null`,
negatives are read as not recorded; `completionPercentage` outside 0–100 is read as
not recorded.

Rules: every member is optional and `null` means *not recorded in CRM*; dates are
ISO `yyyy-MM-dd` (an unreadable or pre-1900 value is read as not recorded);
`parking` is `null` when unknown and `[]` only when CRM knows there is none;
no internal notes, other customers or contacts may be returned. Ticketing maps
statuses as: `200 found` → `Available`; `404`, `found:false` (and any CRM that has not
deployed the route) → `NotAvailable`; timeout, 401, 400, 5xx, malformed body →
`Unavailable`. In every non-`Available` case the API still answers `200` with
CRM's buyer-lookup fields and `null` for the rest.

**The member names above are a proposal.** The Tiger CRM source and database
were not accessible when this was written, so no member has been bound to a
real table/column. The CRM developer should bind each one (or correct the
names here and in `CrmUnitDetailsHttpContracts.cs`) from the CRM schema; the
field-to-source mapping is therefore still owed. The CRM-side work item, with a
mapping template to complete, is
[CRM-GetUnitDetails-Implementation-Spec.md](CRM-GetUnitDetails-Implementation-Spec.md).

## Errors

All errors are the standard ProblemDetails body; coded ones carry `code`.

| Status | `code` | When | Bot should |
|---|---|---|---|
| 400 | — | `customerReference` missing/not a CRM customer; `phoneNumber` missing or withheld (`tel:anonymous`); `unitId` ≤ 0 | fix the request / re-verify |
| 401 | — | No/invalid bearer token | re-authenticate |
| 403 | — | Caller's role lacks `CustomerVerification` | configuration problem |
| 403 | `CUSTOMER_NOT_VERIFIED` | CRM does not resolve the number to this customer (unknown number, or a different customer) | hand over to an agent |
| 403 | `UNIT_NOT_ELIGIBLE` | The unit is not one of this customer's units (other customer's, or nonexistent) | ask again from `eligibleUnits` |
| 409 | `CUSTOMER_AMBIGUOUS` | CRM holds more than one customer for the number; nothing disclosed | hand over to an agent |
| 502 | `CRM_UNAVAILABLE` | CRM unreachable, rejected the key, or answered unusably | apologise / retry later |
| 503 | — | `Genesys:Enabled` is false | — |

Failure of the *details* source never fails the call: the answer is `200` with
CRM's own fields and `null`s, and `detailsStatus` says why.

Audit trail: a customer/unit mismatch is logged as a warning with the customer
and unit ids (never the phone number).

## Status and what is still unavailable

| Item | State |
|---|---|
| TigerCS endpoint, DTOs, gateway mapping, ownership + proof + lead-binding rules | Built; unit/integration tests pass against fakes and a canned HTTP handler |
| Tiger CRM `TicketingSystemController.GetUnitDetails` | Insertion-ready action written from the supplied controller (service-key auth, JSON 401, customer/unit/lead ownership, Sold/Contract); logic-tested against stubs, **not compiled in CRM**. Detail/handover/completion/sale columns **not yet mapped** — see [CRM-GetUnitDetails-Field-Mapping.md](CRM-GetUnitDetails-Field-Mapping.md) |
| TigerGroupWeb route forwarding | Already present in the supplied `GenesysController`/`TicketingGenesysService` (raw-body pass-through); nothing to add. Build/registration not verified |
| Fields genuinely connected to CRM today | Only the buyer-lookup fields: unit id/number/floor/type code, `booking.*`, project id/name/Arabic name |
| Fields unavailable and why | tower, unit-type name, bedrooms, area, parking, unit/project handover dates, project address/status/description/amenities, completion %, completion dates, sold price, registration cost — all need the CRM action |
| Real CRM / public website exercised | **No.** The requirement is *not* complete until the UAT procedure below passes |

## Deployment dependencies

1. Tiger CRM: implement and deploy `GET /TicketingSystem/GetUnitDetails` per the spec (same `TicketingSecretKey` / `X-SECRET-KEY` service auth as `GetBuyerByPhone`; no Session/AllowEdit; invalid key → JSON `401`, never a login redirect).
2. TigerGroupWeb: forward `POST /api/genesys/customers/unit-details` through the existing OAuth policy / `TicketingOptions` / service account, preserving body, status and response body.
3. TigerCS: `Crm:Provider=Http`, `Crm:BaseUrl`, `Crm:SecretKey` (already used by `GetBuyerByPhone`); `Genesys:Enabled=true`. Nothing else to configure; `Mock` is refused outside Development/Testing.
4. Genesys: re-import Data Action `12-customer-unit-details.json` (new input `verificationSessionId`, new outputs).

## UAT procedure (not yet run)

Use a real UAT buyer with two units, one of them with a recorded sale, and a second buyer.

1. `GET /api/genesys/customers/lookup` for buyer A's number → note `externalCustomerId`.
2. `POST https://tigergroup.ae/api/genesys/customers/unit-details` with a Genesys OAuth token, no `unitId` → `UnitSelectionRequired`, both units.
3. With `unitId` of A's unit, no `verificationSessionId` → `detailsStatus: Available`, completion + handover fields match the CRM project screen; `sale: null`, `financialDetailsStatus: VerificationRequired`.
4. Complete OTP verification for that unit, repeat with `verificationSessionId` → `sale` equals the amounts on the CRM booking/sale screen for A's Lead; currency matches.
5. Repeat with an expired or other-unit session → `VerificationFailed`, no sale.
6. Send buyer B's `unitId` with A's customer/phone → `403 UNIT_NOT_ELIGIBLE`; a non-existent id gives the identical answer.
7. Break the CRM key in UAT config → public call returns `502 CRM_UNAVAILABLE` for the buyer lookup (not a login page); with only `GetUnitDetails` failing, `200` with `detailsStatus: Unavailable`.
8. Confirm a zero-value / unrecorded case if CRM data has one (`0` stays `0`, missing stays `null`).

Record pass/fail per step in `docs/releases/`. Only a pass of steps 2–6 through the public URL closes the requirement.
