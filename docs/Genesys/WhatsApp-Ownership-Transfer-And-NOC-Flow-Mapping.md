# WhatsApp "Ownership Transfer and NOC" — Genesys Flow mapping specification

Status: **specification only.** Nothing here has been imported into Genesys or run against a live Ticketing database. The live Flow and the production database are not changed by this repository.

## 1. Principles

* "Ownership Transfer and NOC" is a **parent menu**, not a Request Type. Do **not** create a Request Type with that name and do **not** map the parent to any single child. Ticketing is only ever told the **submenu option** the customer picked.
* Request Type ids are database identities and **differ per environment**. The Flow reads them from a per-environment Genesys data table. The verified ids from the SQL review (20, 1, 2, 3, 4) belong to that one database and must not be copied to another environment or into the Flow definition.
* Ticketing never infers a Request Type from `subject`. The option must arrive as `requestType.requestTypeId` (preferred) or an exact `requestTypeName`.
* The Category is resolved by Ticketing from the Request Type (active Category, same department, same name). Genesys never sends a category.
* The responsible department comes from the Request Type. A Registration request is routed to Registration even though the chat entered through Customer Service (`OriginatingDepartmentId` stays Customer Service; the transfer is audited).

## 2. Submenu → Request Type (names verified; ids per environment)

| WhatsApp submenu option | Request Type (exact name) | Department | Verified id in the reviewed DB (reference only) | Category (same name, same dept) |
|---|---|---|---|---|
| Ownership Transfer / Title Deed | Ownership Transfer / Title Deed Inquiry | Registration (REG) | 20 | 21 |
| NOC for Resale | NOC for Resale | Customer Service (CS) | 1 | 3 |
| NOC for Handover | NOC for Handover | Customer Service (CS) | 2 | 4 |
| NOC for Mortgage | NOC for Mortgage | Customer Service (CS) | 3 | 5 |
| NOC for Golden Visa | NOC for Golden Visa | Customer Service (CS) | 4 | 6 |

Selecting the **parent** only opens the submenu: no ticket classification is sent for it. If the customer abandons the submenu, create the ticket without a request type (Normal priority, human classification queue — unchanged behaviour).

## 3. Genesys data table (one per environment)

Table `TigerCS_RequestTypeMap` — key `menuOption`:

| menuOption (key) | requestTypeId (string) | requestTypeName (reference, optional) |
|---|---|---|
| `OWNERSHIP_TRANSFER_TITLE_DEED` | *(from this environment's export)* | Ownership Transfer / Title Deed Inquiry |
| `NOC_RESALE` | *(…)* | NOC for Resale |
| `NOC_HANDOVER` | *(…)* | NOC for Handover |
| `NOC_MORTGAGE` | *(…)* | NOC for Mortgage |
| `NOC_GOLDEN_VISA` | *(…)* | NOC for Golden Visa |

Fill the ids by running [`uat/export-request-type-mapping.sql`](uat/export-request-type-mapping.sql) (or `GET /api/request-types`) on **that** environment and keep only rows with `UsableByGenesys = 1`. Leave a row's id blank if it is unverified; a blank id sends no classification.

## 4. Flow instructions

1. Show the parent menu "Ownership Transfer and NOC", then the five submenu options. Store the choice in `Flow.menuOption`.
2. **Data Table Lookup** on `TigerCS_RequestTypeMap` by `menuOption`; on found, set `Flow.requestTypeId` (string) from the `requestTypeId` column. On not found/blank, leave it empty.
3. Call **TigerCS - Create or Reuse Ticket** (`02`) with:
   * `conversationId`, `channel = "WhatsApp"`, customer fields and `queueId`/`departmentCode` as today;
   * `requestTypeId = Flow.requestTypeId`;
   * **leave `requestTypeName` empty** (fallback only);
   * `subject` as today — display text only;
   * `serviceRequestId` / `humanFollowUpRequired` as today.
4. Store `ticketId` in participant data as today. If the selection is only known later, call **TigerCS - Update Routing** (`03`) with `ticketId`, `conversationId` and `requestTypeId`; repeating the same id changes nothing, and a different one is refused (409) once a person has worked the ticket.
5. Response handling: `classificationStatus = "Classified"` → done. `"AwaitingClassification"` is normal for a blank id (the ticket sits with the classification queue). **422** means the id/name is not usable in this environment (unknown, inactive, department inactive, workflow unpublished, or id and name disagree) — alert the Genesys owner; do not retry with a guessed value.

## 5. Example valid POST (UAT-style placeholder id)

`POST https://tigergroup.ae/api/genesys/tickets` — Registration option; `<ID>` is this environment's id for *Ownership Transfer / Title Deed Inquiry*:

```json
{
  "conversationId": "7f1c2a9e-0000-0000-0000-000000000001",
  "channel": "WhatsApp",
  "customerPhone": "+971500000001",
  "customerName": "Ahmed Ali",
  "departmentCode": "CS",
  "subject": "Chat received via Genesys - WhatsApp - CS - Ownership Transfer and NOC",
  "requestType": { "requestTypeId": "<ID>", "name": "" },
  "serviceRequestId": "",
  "humanFollowUpRequired": ""
}
```

Expected `201`: `classificationStatus: "Classified"`, `requestTypeId: <ID>`, `departmentId` = Registration. A repeat of the same body returns `200 AlreadyIngested` with the same ticket. A numeric `requestTypeId` (`20`) is accepted as well as the string form.

## 6. Deployment and verification checklist

- [ ] Deploy the Ticketing build containing this change (API accepts `requestTypeId` already; the new check refuses an id and name that disagree).
- [ ] On the target database run `uat/export-request-type-mapping.sql`: all five types `UsableByGenesys = 1`.
- [ ] Confirm each type has exactly one active Category of the same name in the same department (otherwise the request type is saved but Category stays "Not yet classified"). Do not create categories or request types as part of this change.
- [ ] Create/refresh the Genesys data table with this environment's ids.
- [ ] Import updated Data Actions `02` and `03` (new optional `requestTypeId` input) and publish; update the Flow per section 4. Do this in a non-production Genesys environment first.
- [ ] Test each of the five options from WhatsApp: ticket shows the right Request Type, Category and Assigned Department (Registration for Ownership Transfer, Customer Service for the NOCs); Originating Department = Customer Service.
- [ ] Re-send the same event: same ticket, no duplicate.
- [ ] Leave the submenu without choosing: ticket is Unclassified, Normal priority, no human handoff.
- [ ] Send a deliberately wrong pair (id of one option, name of another): 422, no ticket.
- [ ] Only then promote to production, with that environment's own ids.
