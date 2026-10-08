# Genesys queue → department mappings required on UAT

TigerCS resolves a Genesys ticket's department in this order and never falls back to a default:
`departmentId` → `departmentCode` → active `GenesysQueueMappings` row for `queueId` → otherwise HTTP 422
`genesys-department-not-resolved`. Queue ids are Genesys-environment data and are **not in the
repository** (no migration or seed creates mappings), so the real ids below must be supplied by the
Genesys administrator.

## Mappings to create (Admin → Genesys routing, or `POST /api/admin/genesys/queue-mappings`)

| Genesys queue (name as shown in Genesys) | Genesys queue id | Department code | Notes |
|---|---|---|---|
| Customer Service / general | _to be supplied_ | `CS` | |
| Leasing Customer Services (e.g. `TH_CS_Leasing_WM`) | _to be supplied_ | `LCS` | The reported subject `TH_CS_Leasing_WM` implies Leasing |
| Facilities / maintenance | _to be supplied_ | `FM` | |
| Collections | _to be supplied_ | `COL` | |
| Legal (only after decision D6) | _to be supplied_ | _Legal department code, if created_ | |
| Finance | _only if a Finance queue exists_ | `FIN` | Finance must never be a catch-all |

Department codes must exist and be active on UAT (`SELECT DepartmentId, Code, IsActive FROM Departments`);
ids differ between environments, so flows should send `departmentCode`, not numeric ids.

## Verify

1. Run `01_Investigate_Finance_Routing_UAT.sql` (read-only) before and after.
2. One test conversation per queue: ticket number prefix `TG-<CODE>-`, audit entry `GenesysInquiryIngested`
   shows `DepartmentSource=QueueMapping` and the queue id.
3. A conversation from an unmapped queue must return 422 (that is correct, not a bug).
4. After any human transfer, `CurrentDepartmentId` changes and `OriginatingDepartmentId` does not; a
   pending human-handoff raised afterwards belongs to the new current department.
