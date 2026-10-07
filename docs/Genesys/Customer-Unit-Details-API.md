# Customer unit and project details API (chatbot / voicebot)

Lets the bot answer "tell me about my unit" for a **verified** customer, from
Tiger CRM data, without ever disclosing another customer's unit.

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
route (not done — its source was not accessible)** (`POST /api/genesys/customers/unit-details`, body and response passed
through unchanged) before the Data Action `TigerCS - Customer Unit Details`
can work. That change is outside this repository.

The feature flag `Genesys:Enabled` governs it like every Genesys route
(`503` when off).

## Request

| Field | Type | Required | Meaning |
|---|---|---|---|
| `customerReference` | string | yes | The verified CRM customer: `crm:9001`, or the plain id `9001` (the lookup's `externalCustomerId`). Non-CRM references (`ext:…`, `phone:…`) are rejected. |
| `phoneNumber` | string | yes | The verified number the customer was identified by (`tel:+971…`, `+971…`, `971…`). It is how CRM is asked which units the customer owns. |
| `unitId` | integer | no | The CRM unit id the customer selected (from a previous `UnitSelectionRequired` answer). Omit it to get the eligible units. |

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

## Response

Two modes, in `mode`:

* `UnitSelectionRequired` — no `unitId` sent. `eligibleUnits` lists the customer's
  units so the bot can ask which one. `unit`, `project`, `handoverDateSource`,
  `detailsStatus` are `null`.
* `UnitDetails` — `unit`, `project`, `handoverDateSource`, `detailsStatus` filled;
  `eligibleUnits` is `[]`.

Unavailable values are `null`. Nothing is defaulted, guessed or derived.

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

### Sample: unit selected, CRM details source answered (unit-level handover recorded)

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
    "amenities": ["Pool", "Gym"]
  },
  "handoverDateSource": "Unit",
  "detailsStatus": "Available"
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
    "description": null, "amenities": null
  },
  "handoverDateSource": null,
  "detailsStatus": "NotAvailable"
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
      "description": "A residential tower.", "amenities": ["Pool", "Gym"]
    }
  }
}
```

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
field-to-source mapping is therefore still owed.

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
