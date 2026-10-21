# Request Type mapping for Genesys configuration

Genesys classifies the customer's intent; Ticketing only accepts a **configured, active Request Type** and applies its department, assignment and SLA.
Ticketing never guesses a type from words in a message — the intent → Request Type mapping is a Genesys-side table, and this document is its source.

## 1. Request Type identifiers are per environment

`RequestTypeId` is a database identity value, so it differs between development, UAT and production. **Never copy ids between environments.**
Each environment's mapping must be exported from that environment. Two ways:

**a) API (preferred, read-only, any signed-in staff account)**

```
GET /api/request-types                      # every active type
GET /api/request-types?departmentId=4       # one department
Authorization: Bearer <token>
```
```json
[ { "requestTypeId": 17, "name": "Coordinate Handover", "departmentId": 4, "defaultPriorityId": 3, "hasPublishedWorkflow": true } ]
```
Only **active** types are returned. A type with `hasPublishedWorkflow: false` is returned but will be **refused with 422** by the Genesys contract (it cannot govern a ticket) — do not map intents to it.

**b) SQL** — run [`uat/export-request-type-mapping.sql`](uat/export-request-type-mapping.sql) against the environment's database; it returns the same list with department code/name and a `UsableByGenesys` flag.

## 2. What to put in Genesys

Create a Genesys **data table** (or Architect lookup) with one row per intent the AI can recognise:

| Column | Meaning |
|---|---|
| `intent` | The AI intent / topic name Genesys already produces |
| `requestTypeId` | The id from the export above, for **this** environment |
| `requestTypeName` | For humans only — send the **id**, not the name, wherever possible (names can be ambiguous across departments and are refused when they are) |

Then, as soon as the intent is known, send it on the existing endpoints (no new endpoint exists):

* at creation: `POST /api/genesys/tickets` with `"requestType": { "requestTypeId": 17 }`, or
* later: `PATCH /api/genesys/tickets/{ticketId}` with `{ "conversationId": "...", "requestType": { "requestTypeId": 17 } }`.

What Ticketing does with it is unchanged and tested: validate (exists, active, department active, workflow published) → move the ticket to the Request Type's department (`OriginatingDepartmentId` untouched) → run that department's automatic assignment → apply the type's SLA policy to the running period without restarting the clock. An intent with no mapped type: **send nothing** — the ticket stays *Unclassified* and appears under *Tickets → Classification: Unclassified* for an employee to classify. Never send a placeholder id or a "General Inquiry" default to avoid that.

A different type later in the conversation (the customer's real need emerged) replaces the first only while no person has worked the ticket; afterwards the call answers `409` and nothing changes (see the audit document).

## 3. UAT baseline catalogue (names and owning departments)

This is the approved baseline imported by `ImportNewRequestTypes_UAT.sql` (see `docs/New-Request-Types-UAT-Import.md`). Ids are deliberately **not** listed — export them (section 1). Names and departments below can be edited in Administration after import, so treat the live export as authoritative.

| Request code | Department | Group | Request Type | Default priority |
|---|---|---|---|---|
| `CS-GEN-001` | Customer Service | General Inquiries | General Inquiry | Normal |
| `CS-GEN-002` | Customer Service | General Inquiries | Office Hours / Contact Information | Low |
| `CS-GEN-003` | Customer Service | General Inquiries | Construction Update | Normal |
| `CS-CMP-001` | Customer Service | Complaints & Feedback | Complaint | High |
| `CS-CMP-002` | Customer Service | Complaints & Feedback | Feedback / Suggestion | Low |
| `REG-NOC-001` | Customer Service | NOC | NOC for Resale | Normal |
| `REG-NOC-002` | Customer Service | NOC | NOC for Golden Visa | Normal |
| `REG-NOC-003` | Customer Service | NOC | NOC for Mortgage | Normal |
| `REG-CON-001` | Registration | Contracts / DLD | SPA / Contract Inquiry | Normal |
| `REG-DLD-001` | Registration | Contracts / DLD | Ownership Transfer / Title Deed Inquiry | Normal |
| `COL-PAY-001` | Collections | Payments & Collection | Payment / Outstanding Balance Inquiry | Normal |
| `COL-PAY-002` | Collections | Payments & Collection | Returned Cheque | High |
| `COL-PAY-003` | Collections | Payments & Collection | Cheque Collection | Normal |
| `COL-PAY-004` | Collections | Payments & Collection | Payment Cheque Inquiry | Normal |
| `HO-NOC-001` | Customer Service | Handover | NOC for Handover | Normal |
| `HO-HND-001` | Handover | Handover | Coordinate Handover | Normal |
| `HO-HND-002` | Handover | Handover | Schedule Handover Appointment | Normal |
| `HO-HND-003` | Handover | Handover | Move-In / Move-Out | Normal |
| `HO-HND-004` | Handover | Handover | Handover Maintenance Follow-up | Normal |
| `FM-MNT-001` | Facilities Management | Maintenance | Repair / Maintenance Request | Normal |
| `FM-UTL-001` | Facilities Management | Maintenance | Utilities Inquiry | Normal |
| `FM-COM-001` | Facilities Management | Maintenance | Common Area Maintenance | Normal |
| `FM-SVC-001` | Facilities Management | Service Charge | Service Charge Inquiry | Normal |
| `LCS-TEN-001` | Leasing Customer Services | Leasing | Tenancy Contract | Normal |
| `LCS-EJR-001` | Leasing Customer Services | Leasing | Ejari | Normal |
| `LCS-BKG-001` | Leasing Customer Services | Leasing | Booking | Normal |
| `LCS-MOV-001` | Leasing Customer Services | Leasing | Move-In / Move-Out | Normal |
| `LCS-CHK-001` | Leasing Customer Services | Leasing | Rent / DEWA / AC Cheque Inquiry | Normal |
| `BRK-COM-001` | Customer Service | Broker Inquiries | Broker Commission Inquiry | Normal |
| `BRK-CHK-001` | Customer Service | Broker Inquiries | Broker Cheque Collection | Normal |
| `SAL-INQ-001` | Customer Service | Sales | Property Sales Inquiry | Normal |
| `LEG-INQ-001` | Customer Service | Legal | Legal Notice / Permit / Approval Inquiry | High |
| `REC-HR-001` | Customer Service | Reception | HR Inquiry | Low |
| `REC-MKT-001` | Customer Service | Reception | Marketing Inquiry | Low |
| `REC-OTH-001` | Customer Service | Reception | Other Reception Inquiry | Normal |

## 4. Maintaining the mapping

* Re-export after any Request Type is added, renamed, deactivated or its workflow unpublished; a deactivated or unpublished type makes Genesys calls fail with `422 genesys-invalid-request-type` (the ticket is then **not** created on `POST`, so also monitor for 422s).
* Keep the table keyed by id. Renaming a type in Administration does not break an id-based mapping.
