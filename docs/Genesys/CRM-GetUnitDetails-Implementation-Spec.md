# Tiger CRM — `TicketingSystem/GetUnitDetails` implementation spec

**Audience:** the Tiger CRM developer. **Status (2026-10-09): action written but not compiled in CRM; schema mappings partly blocked.** See [CRM-GetUnitDetails-Field-Mapping.md](CRM-GetUnitDetails-Field-Mapping.md) (supersedes the mapping template below) and `crm-insertion/`. Original note: **not built.** The Tiger CRM source and
database schema were not accessible when this was written, so **no CRM table, column or
enum is named here — every "CRM source" cell is yours to fill from the schema.** Nothing
below may be satisfied with a guessed column, a fixed percentage, or the unit's current
list price.

Consumer: `CrmUnitDetailsHttpGateway` in Tiger-CS-Ticketing; public contract in
[Customer-Unit-Details-API.md](Customer-Unit-Details-API.md).

## Route and authentication

```
GET /TicketingSystem/GetUnitDetails?customerId={int}&unitId={int}&leadId={int}[&includeSale=true]
X-SECRET-KEY: <TicketingSecretKey>
```

* Add `GetUnitDetails` to the existing `TicketingSystemController` and authenticate exactly
  as `GetBuyerByPhone` does (`TicketingSecretKey` / `X-SECRET-KEY`). Do **not** use the CRM
  browser Session or `AllowEdit`.
* A missing/invalid key returns **HTTP 401 with a JSON body** (same envelope), never a
  redirect to the login page.
* Envelope: `{ "success": bool, "found": bool, "message": string|null, "unit": {...}|null }`.
  Ownership failure or unknown unit → `404` or `found:false` (the same answer for both).
  Unexpected error → `500` JSON.

## Authorization (re-check in CRM; Ticketing also checks)

Before returning anything private, apply the same eligibility as `GetBuyerByPhone`:
`customerId` is an eligible **Buyer**, `unitId` is one of that customer's units through a
Lead in **Sold/Contract**, and that Lead is `leadId`. If `leadId` is not that customer's
eligible Lead for the unit → not found. Never read a sale from another buyer's Lead or
from an unrelated historical booking of the same unit.

## Response (`unit`)

```json
{
  "unitTypeName": "...", "towerName": "...", "bedrooms": 0, "area": 0.0, "areaUnit": "...",
  "parking": [ { "number": "...", "level": "...", "type": "..." } ],
  "expectedHandoverDate": "yyyy-MM-dd", "actualHandoverDate": "yyyy-MM-dd",
  "project": {
    "address": "...", "status": "...",
    "expectedHandoverDate": "yyyy-MM-dd", "actualHandoverDate": "yyyy-MM-dd",
    "description": "...", "amenities": ["..."],
    "completionPercentage": 0.0,
    "expectedCompletionDate": "yyyy-MM-dd", "actualCompletionDate": "yyyy-MM-dd"
  },
  "sale": { "leadId": 0, "soldPrice": 0.0, "registrationCost": 0.0, "currency": "ISO-4217" }
}
```

Rules: `sale` only when `includeSale=true`, else omit it. `null` = not recorded; `0` = a
recorded zero (do not coalesce). Dates ISO; never a `/Date()/` or `0001-01-01` sentinel
(Ticketing reads those as not recorded). No internal notes, other customers, contacts.

## Field-to-source mapping — TO BE COMPLETED FROM THE CRM SCHEMA

| Public field | CRM source (table.column / existing calculation) | Meaning | Unit / currency | Nullable | Status |
|---|---|---|---|---|---|
| `unit.unitId`, `unitNumber`, `floor`, `unitType.code` | `GetBuyerByPhone` members `UnitId`, `UnitNumber`, `FloorNumber`, `UnitType` | as named | — | no | **connected today** (via buyer lookup) |
| `booking.reference` / `status` / `statusCode` | `GetBuyerByPhone` `LeadId`, `LeadStatusName`, `LeadStatus` | the Lead the unit was sold/contracted through | — | no | **connected today** |
| `project.projectId`, `name`, `arabicName` | `GetBuyerByPhone` `ProjectId`, `ProjectName`, `ProjectArabicName` | project identity | — | arabic: yes | **connected today** |
| `unit.unitTypeName` | *CRM to supply* | display name of the unit-type code | text | yes | not bound |
| `unit.tower` | *CRM to supply* | tower/building | text | yes | not bound |
| `unit.bedrooms` | *CRM to supply* | bedroom count (studio ≠ 0 unless recorded) | count | yes | not bound |
| `unit.area` / `areaUnit` | *CRM to supply* | area and the unit it is recorded in | e.g. sqft/sqm — as recorded | yes | not bound |
| `unit.parking[]` | *CRM to supply* | parking allocated to the unit | — | list: `null` unknown, `[]` none | not bound |
| `unit.expectedHandoverDate` / `actualHandoverDate` | *CRM to supply* | unit-level planned / actual handover | date | yes | not bound |
| `project.address`, `status`, `description`, `amenities` | *CRM to supply* | customer-facing project facts | text | yes | not bound |
| `project.expectedHandoverDate` / `actualHandoverDate` | *CRM to supply* | project-level planned / actual handover | date | yes | not bound |
| `project.completionPercentage` | *CRM to supply* | construction completion | percent 0–100 | yes (0 is real) | not bound |
| `project.expectedCompletionDate` / `actualCompletionDate` | *CRM to supply* | planned / actual construction completion — **not handover** | date | yes | not bound |
| `sale.soldPrice` | *CRM to supply* — the price **this customer's Lead** agreed, **not** the unit list/asking price | actual sold price | currency in `sale.currency` | yes (0 is real) | not bound |
| `sale.registrationCost` | *CRM to supply* — the registration fee **amount** stored for the sale; **not** a percentage, **not** the registration-receipt document, **not** derived | registration fee | currency in `sale.currency` | yes (0 is real) | not bound |
| `sale.currency` | *CRM to supply* | currency of the two amounts | ISO-4217 | yes | not bound |

If a datum is not stored anywhere in CRM, say so in the status column and leave the
member out — Ticketing will return `null`. Do not synthesise it.

## Acceptance checks for CRM

1. Valid key + buyer's own unit + correct `leadId` → `200 found`, values equal the CRM unit, booking/sale and project screens.
2. Same without `includeSale=true` → no `sale` member.
3. Another buyer's unit, unknown unit, wrong `leadId` → identical not-found answer, no data.
4. Wrong/absent key → `401` JSON, no redirect.
5. A sale priced `0` / a project at `0%` returns `0`; an unrecorded one returns `null`.
