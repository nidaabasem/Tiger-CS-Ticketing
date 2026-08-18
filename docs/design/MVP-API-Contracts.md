# Tiger Group — CS Ticketing System
## MVP API Contracts

| | |
|---|---|
| **Status** | Design for review — conceptual/logical contract design only |
| **Scope** | Detailed HTTP API contracts for the 3-week internal pilot MVP, covering every endpoint needed by the modules in `docs/architecture/Module-Design.md` |
| **Explicitly not done here** | No controller classes, no DTO classes, no service/repository implementations, no OpenAPI/Swagger-generated code, no actual routing configuration. This document is the source those will be written from in Phase 3. |
| **Base** | `main` @ `4fe6f19`, refining `docs/design/MVP-ERD.md` / `docs/design/MVP-Data-Dictionary.md` |
| **Related documents** | `docs/design/MVP-ERD.md`, `docs/design/MVP-Data-Dictionary.md`, `docs/architecture/SLA-Architecture.md`, `docs/architecture/Security-Architecture.md`, `docs/architecture/Genesys-Integration.md`, `docs/architecture/adr/0008` (five state dimensions), `0013-0014` (Outbox/idempotency), `0016` (SignalR) |
| **Date** | 2026-08-18 |

---

## 0. Conventions Used Throughout This Document

- **Error format:** every error response is [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807) — `{ "type", "title", "status", "detail", "instance", "traceId" }` plus an `errors` extension member (`{ "fieldName": ["message"] }`) for validation failures. Only the fields relevant to each endpoint are shown below to avoid repetition; assume the full envelope always applies.
- **Auth header:** `Authorization: Bearer <JWT>` (ADR-0004/Security-Architecture.md §2) on every endpoint except `POST /api/auth/login` and `POST /api/genesys/webhook` (which uses webhook signature validation instead — see §6).
- **Concurrency header:** endpoints that mutate a `Tickets` row require `If-Match: "<RowVersion>"` (maps to `Tickets.RowVersion`, ADR — optimistic concurrency); a mismatch returns `409 Conflict` with a ProblemDetails body (`type: .../concurrency-conflict`).
- **Idempotency header:** endpoints marked **Idempotent (client-supplied key)** require `Idempotency-Key: <guid>` and use `IdempotencyRecords` (Scope = the endpoint's own scope name) to make a retried request return the original result rather than double-executing (ADR-0014).
- **Pagination:** all `GET` list endpoints use `?page=1&pageSize=25` (`pageSize` max 100, `[ASSUMPTION]`), returning `{ "items": [...], "page", "pageSize", "totalCount" }`.
- **Roles referenced:** Agent, Supervisor, Department Head (Dept Head), CS Manager, GM, System Administrator (per `Security-Architecture.md` §3's role list). "Any authenticated staff" means no role check beyond a valid, active session.
- **Domain events / Outbox:** where an endpoint's action must be observed outside the request (notification, SLA recalculation, audit trail, Genesys correlation), the specific `OutboxMessages.EventType` values it writes are listed — always in the same DB transaction as the state change (ADR-0013).
- **Rate limiting:** MVP applies a single coarse per-user limit (`[ASSUMPTION]` 120 requests/minute) at the API gateway/middleware layer, not per-endpoint, except where called out (webhook ingestion, login).

---

## 1. Authentication and Users Module

### 1.1 `POST /api/auth/login`
- **Purpose:** Authenticate staff credentials and issue a JWT.
- **Auth:** None (anonymous).
- **Headers:** `Content-Type: application/json`.
- **Request DTO `LoginRequest`:** `Username` (string, required), `Password` (string, required).
- **Request example:**
```json
{ "username": "j.smith", "password": "•••••••••" }
```
- **Success response `200 OK`, `LoginResponse`:** `AccessToken` (string, JWT), `ExpiresAtUtc` (datetime2), `EmployeeId` (guid), `DisplayName` (string), `Roles` (string[]), `PrimaryDepartmentId` (int).
- **Success example:**
```json
{ "accessToken": "eyJ...", "expiresAtUtc": "2026-08-18T14:00:00Z", "employeeId": "b3f...", "displayName": "J. Smith", "roles": ["Agent"], "primaryDepartmentId": 2 }
```
- **Validation:** both fields required; no password-format check surfaced to the client beyond "invalid credentials" (avoid enumeration).
- **Errors:** `400` (missing fields), `401` (`type: .../invalid-credentials`, generic message regardless of whether username or password was wrong), `423 Locked` (`type: .../account-locked`, after the Security-Architecture.md §4 lockout threshold).
- **Concurrency:** N/A. **Idempotency:** not idempotent-key-based; repeated correct logins simply issue new tokens.
- **Audit:** `AuditEntries` row (`Action = "Login"`, `EntityType = "Employee"`) on both success and lockout.
- **Rate limiting:** stricter limit here than the coarse default — `[ASSUMPTION]` 10 attempts/5 minutes per username, to slow credential-stuffing.

### 1.2 `POST /api/auth/logout`
- **Purpose:** Invalidate the current session/token server-side (if using a token-revocation list) and clear client state.
- **Auth:** Any authenticated staff.
- **Success:** `204 No Content`.
- **Audit:** `AuditEntries` row (`Action = "Logout"`).

### 1.3 `GET /api/users/me`
- **Purpose:** Return the current user's profile, roles, and department memberships — the client's source of truth for what UI to render.
- **Auth:** Any authenticated staff.
- **Success `200 OK`, `CurrentUserResponse`:** `EmployeeId`, `DisplayName`, `Roles` (string[]), `Departments` (array of `{ DepartmentId, Name, IsPrimary }`), `IsGeynessStaff` (bool).
- **Errors:** `401` if token expired/invalid.

### 1.4 `GET /api/departments/{departmentId}/users`
- **Purpose:** List staff assigned to a department — populates assignment/transfer pickers.
- **Auth:** Agent and above (any authenticated staff may view; assignment actions themselves are separately authorized on the ticket endpoints).
- **Path params:** `departmentId` (int, required).
- **Query params:** `activeOnly` (bool, default `true`), `page`, `pageSize`.
- **Success `200 OK`:** paginated list of `{ EmployeeId, DisplayName, IsPrimary, Roles }`.
- **Errors:** `404` if department doesn't exist.

### 1.5 `GET /api/roles`
- **Purpose:** List the fixed MVP role set and their high-level permissions, for admin UI display.
- **Auth:** System Administrator.
- **Success `200 OK`:** array of `{ RoleName, Description }`. Static/seeded data — no create/update/delete endpoint at MVP (`[ASSUMPTION]` role set is fixed for the pilot, per `Security-Architecture.md` §3).

### 1.6 `PATCH /api/users/{employeeId}/activation`
- **Purpose:** Activate or deactivate a staff account (FR-ADM-02).
- **Auth:** System Administrator.
- **Path params:** `employeeId` (guid).
- **Request DTO `ActivationRequest`:** `IsActive` (bool, required), `Reason` (string, optional, recommended when deactivating).
- **Success `200 OK`:** updated `{ EmployeeId, DisplayName, IsActive: bool, DeactivatedAtUtc }`.
- **Validation:** cannot deactivate the last active System Administrator (`[ASSUMPTION]` — prevents total lockout; flagged for confirmation).
- **Errors:** `404` (no such employee), `409` (`type: .../last-admin`) for the last-admin case.
- **Concurrency:** none beyond normal last-write-wins on this narrow field (no `RowVersion` on `Employees` at MVP — `[ASSUMPTION]`, low contention expected).
- **Side effect:** if deactivating an employee who is `Tickets.CurrentOwnerEmployeeId` on open tickets, those tickets are **not** auto-reassigned by this endpoint — per `MVP-ERD.md` §2.2, reassignment is a separate, explicit action; this endpoint returns the count of affected open tickets in the response (`AffectedOpenTicketCount`) so the admin knows reassignment is needed.
- **Audit:** `AuditEntries` (`Action = "Activate"/"Deactivate"`, `EntityType = "Employee"`, before/after `IsActive`).

---

## 2. CRM Verification Module

### 2.1 `GET /api/crm/units/{crmUnitId}`
- **Purpose:** Verify a unit against the CRM and return current cached (or freshly synced) unit data — the first step of FR-CRM-01.
- **Auth:** Agent and above.
- **Path params:** `crmUnitId` (string, the CRM's own identifier — or, if the agent only has a spoken unit number, see §2.2 for search-by-number).
- **Success `200 OK`, `UnitVerificationResponse`:** `UnitReferenceId`, `CrmUnitId`, `UnitNumber`, `PropertyName`, `TowerName`, `UnitType`, `LastSyncedAtUtc`, `ContactCount` (int).
- **Errors:** `404` (`type: .../unit-not-found` — genuinely doesn't exist in CRM), `502 Bad Gateway` (`type: .../crm-unavailable` — CRM gateway timeout/error; this is the trigger for the Intake Record fallback flow, §2.5), `504` (CRM timeout, same fallback trigger).
- **Concurrency/Idempotency:** N/A (read-only).
- **Audit:** none (a lookup, not a state change) — `[ASSUMPTION]` verification lookups are not individually audited, only the resulting `TicketRequesterSnapshots` capture is (per ADR-0007).

### 2.2 `GET /api/crm/units/search`
- **Purpose:** Search CRM units by the raw, as-spoken unit number when the agent doesn't have the CRM ID yet.
- **Auth:** Agent and above.
- **Query params:** `unitNumber` (string, required), `propertyName` (string, optional, narrows ambiguous matches).
- **Success `200 OK`:** array of `UnitVerificationResponse` (may be 0, 1, or several matches — ambiguity is resolved by the agent, not the API).
- **Errors:** `502`/`504` same as §2.1.

### 2.3 `GET /api/crm/units/{crmUnitId}/contacts`
- **Purpose:** Retrieve the CRM's current authorized contacts for a unit (owner/tenant/representative), for the requester-confirmation step (FR-CRM-02).
- **Auth:** Agent and above.
- **Path params:** `crmUnitId` (string).
- **Success `200 OK`:** array of `ContactVerificationResponse`: `ContactReferenceId`, `CrmContactId`, `DisplayName`, `ContactChannel`, `ContactType`, `AuthorizedRepresentativeOfContactId` (nullable).
- **Errors:** `404`, `502`, `504` as above.

### 2.4 `POST /api/tickets/{ticketId}/requester-confirmation`
- **Purpose:** Record the agent's read-back confirmation of who they're speaking to, writing the immutable `TicketRequesterSnapshots` row (ADR-0007). Called during/immediately after ticket creation.
- **Auth:** Agent and above; must be the ticket's current owner or an unassigned-ticket claimant.
- **Path params:** `ticketId` (bigint).
- **Request DTO `RequesterConfirmationRequest`:** `ContactReferenceId` (int, required), `ConfirmedVerbally` (bool, required, must be `true` — a confirmation record cannot be created for an unconfirmed contact).
- **Success `201 Created`:** the created `TicketRequesterSnapshots` row (`SnapshotUnitNumber`, `SnapshotPropertyName`, etc., `CapturedAtUtc`).
- **Validation:** `409 Conflict` (`type: .../snapshot-already-exists`) if called a second time for the same ticket — this table is write-once (`MVP-ERD.md` §2.8); there is no PATCH/PUT for it, ever.
- **Concurrency:** `If-Match` on the ticket's `RowVersion`.
- **Audit:** `AuditEntries` (`Action = "ConfirmRequester"`, `EntityType = "Ticket"`).
- **Domain event:** none beyond the audit entry — this does not itself trigger notifications.

### 2.5 `POST /api/intake-records`
- **Purpose:** Create an Intake Record when the CRM is unavailable, so the call isn't lost (fallback path, `Genesys-Integration.md`/CRM outage handling).
- **Auth:** Agent and above.
- **Request DTO `CreateIntakeRecordRequest`:** `ChannelId` (tinyint, required), `RawUnitNumberEntered` (string, optional), `PriorityHint` (tinyint, optional).
- **Success `201 Created`:** `{ IntakeRecordId, ReceivedAtUtc, CrmVerificationStatus: "Unverified" }`.
- **Idempotent (client-supplied key):** yes — a retried submit (e.g., double-click) with the same `Idempotency-Key` returns the same `IntakeRecordId` rather than creating a duplicate.
- **Audit:** `AuditEntries` (`Action = "CreateIntakeRecord"`, `EntityType = "IntakeRecord"`).

### 2.6 `POST /api/intake-records/{intakeRecordId}/promote`
- **Purpose:** Once the CRM is back and the unit/contact are verified, promote an Intake Record into a full Ticket, linking the two (`MVP-ERD.md` §2.9).
- **Auth:** Agent and above.
- **Path params:** `intakeRecordId` (bigint).
- **Request DTO:** the same fields as ticket creation (§3.1) minus what's already on the Intake Record.
- **Success `201 Created`:** the created `Ticket` resource (see §3.1's response shape) plus `PromotedFromIntakeRecordId`.
- **Validation:** `409` if the Intake Record already has a `LinkedTicketId` (already promoted); `422` (`type: .../crm-still-unavailable`) if the CRM lookup this promotion depends on still fails.
- **Domain event:** `OutboxMessages` `EventType = "TicketCreated"` (same as normal creation).

### 2.7 `GET /api/intake-records`
- **Purpose:** List unresolved/unpromoted Intake Records — the "CRM outage backlog" work queue.
- **Auth:** Agent and above (own department, `[ASSUMPTION]`); Supervisor+ sees all departments.
- **Query params:** `crmVerificationStatus` (tinyint, optional filter), `promoted` (bool, optional — `false` = still pending), `page`, `pageSize`.
- **Success `200 OK`:** paginated `IntakeRecordSummary[]`.

---

## 3. Ticketing Module

### 3.1 `POST /api/tickets`
- **Purpose:** Create a new ticket (FR-TCK-01). Requires the unit/contact to already be CRM-verified in the current session (typically calling §2.1/§2.3 first) unless created via Intake Record promotion (§2.6).
- **Auth:** Agent and above.
- **Request DTO `CreateTicketRequest`:** `UnitReferenceId` (int, required), `ContactReferenceId` (int, required), `CategoryId` (int, required), `PriorityId` (tinyint, required), `RequestSummary` (string, required, ≤2000 chars), `GenesysInteractionId` (bigint, optional — links a Genesys-originated call).
- **Request example:**
```json
{
  "unitReferenceId": 4021,
  "contactReferenceId": 8890,
  "categoryId": 12,
  "priorityId": 2,
  "requestSummary": "AC unit in unit 1204 not cooling since yesterday evening.",
  "genesysInteractionId": null
}
```
- **Success `201 Created`, `TicketResponse`:** `TicketId`, `TicketNumber`, `OriginatingDepartmentId`, `CurrentDepartmentId`, `CurrentOwnerEmployeeId`, `CategoryId`, `PriorityId`, `TicketStatus`, `VerificationStatus`, `EscalationLevel`, `SlaState`, `RequestSummary`, `CreatedAtUtc`, `RowVersion`, plus the nested current `TicketSlaInstances` row (`FirstResponseDueAtUtc`, `ResolutionDueAtUtc`).
- **Validation:** `CategoryId` must route to a Department (derives `OriginatingDepartmentId`/`CurrentDepartmentId` — not client-supplied, per `MVP-ERD.md` §2.3's write-once rule); `422` if `UnitReferenceId`/`ContactReferenceId` don't have a matching `TicketRequesterSnapshots`-eligible confirmation already on file for this session `[ASSUMPTION — exact session-linkage mechanism between "verified" and "create" is an implementation detail deferred to Phase 3]`.
- **Errors:** `400` (validation), `404` (unit/contact/category/priority not found).
- **Concurrency:** N/A (create). **Idempotent (client-supplied key):** yes — prevents duplicate tickets from a double-submit.
- **Domain events / Outbox:** `TicketCreated` (drives the automated acknowledgement notification, §5.1's flow), `TicketSlaInstanceOpened`.
- **Audit:** `AuditEntries` (`Action = "Create"`, `EntityType = "Ticket"`); `TicketStatusHistory` seed rows for all five dimensions (`OldValue = null`).

### 3.2 `GET /api/tickets`
- **Purpose:** List/search/filter tickets — the queue view (FR-TCK-06).
- **Auth:** Agent and above, scoped to own department by default; Supervisor+ can query across departments.
- **Query params:** `departmentId`, `categoryId`, `priorityId`, `ticketStatus`, `escalationLevel`, `slaState`, `ownerEmployeeId`, `unitReferenceId`, `search` (free text over `TicketNumber`/`RequestSummary`), `createdFrom`/`createdTo` (date), `sortBy` (`createdAtUtc`|`priority`|`slaDueAt`, default `createdAtUtc`), `sortDir` (`asc`|`desc`), `page`, `pageSize`.
- **Success `200 OK`:** paginated `TicketSummary[]` (a lighter shape than `TicketResponse` — no nested SLA instance detail, just `SlaState` and the current due timestamp for at-a-glance display).
- **Errors:** `400` (invalid filter combination, e.g., unknown enum value).

### 3.3 `GET /api/tickets/{ticketId}`
- **Purpose:** Full ticket detail (FR-TCK-07).
- **Auth:** Agent and above.
- **Success `200 OK`:** `TicketDetailResponse` — `TicketResponse` fields plus nested `RequesterSnapshot`, `CurrentAssignment`, `CurrentResolution` (nullable), `OpenEscalations` (array), `AttachmentCount`, `NoteCount`.
- **Errors:** `404`.

### 3.4 `PATCH /api/tickets/{ticketId}`
- **Purpose:** Update mutable descriptive fields — summary, category, priority (FR-TCK-08/09). Priority changes route through the SLA module's rules (§5.6/§5.7) rather than being a bare field write; this endpoint accepts a priority change request but defers to those rules internally.
- **Auth:** Agent and above (summary/category); priority **increase** allowed for Agent+; priority **decrease** requires Dept Head+ approval (enforced here, not just at the SLA endpoints — see Validation).
- **Path params:** `ticketId`.
- **Headers:** `If-Match` required.
- **Request DTO `UpdateTicketRequest`** (all fields optional, at least one required): `RequestSummary` (string), `CategoryId` (int), `PriorityId` (tinyint).
- **Success `200 OK`:** updated `TicketResponse`.
- **Validation:** a `PriorityId` decrease without an already-approved downgrade context returns `403` (`type: .../downgrade-requires-approval`) directing the client to `POST /api/tickets/{ticketId}/sla/priority-downgrade` (§5.7) instead.
- **Errors:** `404`, `409` (concurrency), `403`.
- **Domain events:** `TicketCategoryChanged` and/or `TicketPriorityChanged` (only the changed ones), each also writing a `TicketStatusHistory` row.
- **Audit:** `AuditEntries` per changed field.

### 3.5 `POST /api/tickets/{ticketId}/assignment`
- **Purpose:** Assign or reassign a ticket to an employee (FR-TCK-10).
- **Auth:** Agent and above may self-claim an unassigned ticket in their department; reassigning *another* agent's ticket requires Supervisor+.
- **Headers:** `If-Match`.
- **Request DTO `AssignTicketRequest`:** `AssignedEmployeeId` (guid, required).
- **Success `200 OK`:** the new current `TicketAssignments` row plus updated `TicketResponse.CurrentOwnerEmployeeId`.
- **Validation:** `422` if `AssignedEmployeeId` isn't an active member of the ticket's `CurrentDepartmentId` (`UserDepartmentAssignments`).
- **Domain events:** `TicketAssigned`; prior current assignment's `IsCurrent` flips to false in the same transaction (append-only, `MVP-ERD.md` §2.12).
- **Audit:** `AuditEntries` (`Action = "Assign"`).

### 3.6 `POST /api/tickets/{ticketId}/transfer`
- **Purpose:** Transfer a ticket to a different department (FR-TCK-11).
- **Auth:** Supervisor+ in the current department.
- **Headers:** `If-Match`.
- **Request DTO `TransferTicketRequest`:** `TargetDepartmentId` (int, required), `Reason` (string, required).
- **Success `200 OK`:** updated `TicketResponse.CurrentDepartmentId`; `OriginatingDepartmentId` unchanged (write-once, `MVP-ERD.md` §2.3).
- **Validation:** `422` if `TargetDepartmentId` equals the current department; `404` if the target department is inactive.
- **Side effect:** current assignment is cleared (`CurrentOwnerEmployeeId → null`) — the receiving department must explicitly claim/assign it; a new `TicketAssignments` row is **not** auto-created.
- **Domain events:** `TicketDepartmentTransferred`; `TicketStatusHistory` row.
- **Audit:** `AuditEntries` (`Action = "Transfer"`).

### 3.7 `POST /api/tickets/{ticketId}/status`
- **Purpose:** Change `TicketStatus` (e.g., Open→InProgress→PendingCustomer→...) independent of the other four dimensions (ADR-0008).
- **Auth:** Agent and above (must be current owner, or Supervisor+).
- **Headers:** `If-Match`.
- **Request DTO `ChangeStatusRequest`:** `NewStatus` (tinyint, required), `Note` (string, optional).
- **Success `200 OK`:** updated `TicketResponse.TicketStatus`.
- **Validation:** `422` (`type: .../invalid-status-transition`) if the requested transition isn't in the allowed state machine (e.g., cannot go directly from `Open` to `Closed` without a `TicketResolutions` row — see §3.9).
- **Side effect:** if `NewStatus = PendingCustomer` (or another pause-triggering status per `SLA-Architecture.md` §6), this **also** opens a `TicketSlaPausePeriods` row via the same transaction — the client does not call the SLA pause endpoint separately for this case; **manual** pause requests unrelated to a status change use §5.3 directly.
- **Domain events:** `TicketStatusChanged`; `TicketStatusHistory` row (`Dimension = 1`).
- **Audit:** `AuditEntries`.

### 3.8 `POST /api/tickets/{ticketId}/notes`
See §4.1 (kept together with attachments for module cohesion).

### 3.9 `POST /api/tickets/{ticketId}/resolution`
- **Purpose:** Resolve a ticket (FR-RES-01/02) — the primary "close out the work" action, distinct from the final `Closed` status transition.
- **Auth:** Agent and above (must be current owner, or Supervisor+).
- **Headers:** `If-Match`.
- **Request DTO `ResolveTicketRequest`:** `ResolutionOutcome` (tinyint, required: Resolved/Cancelled/Rejected/Duplicate), `ResolutionNote` (string, required, ≤4000 chars, BR-011), `ReasonCode` (tinyint, required if Cancelled/Rejected), `DuplicateOfTicketId` (bigint, required if Duplicate).
- **Success `201 Created`:** the created `TicketResolutions` row; `TicketResponse.TicketStatus` moves to `Resolved`.
- **Validation:** `422` if `DuplicateOfTicketId` references a ticket that is itself a duplicate (no duplicate chains, `MVP-ERD.md` §2.10); `400` if `ResolutionNote` missing/empty regardless of outcome.
- **Domain events:** `TicketResolved` (drives resolution-SLA stop, §5.2's `ResolutionBreached` finalization if not already breached); `TicketStatusHistory` rows for `TicketStatus` and `ResolutionOutcome` dimensions.
- **Audit:** `AuditEntries`.

### 3.10 `POST /api/tickets/{ticketId}/close`
- **Purpose:** Final close after resolution (separately tracked per FR-RES-03, since a resolved ticket may still need a closing step — e.g., customer sign-off window).
- **Auth:** Agent and above (must be current owner, or Supervisor+).
- **Headers:** `If-Match`.
- **Success `200 OK`:** `TicketResponse.TicketStatus = Closed`.
- **Validation:** `409` (`type: .../not-yet-resolved`) if there's no current `TicketResolutions` row.
- **Domain events:** `TicketClosed`; `TicketStatusHistory` row.

### 3.11 `POST /api/tickets/{ticketId}/reopen`
- **Purpose:** Reopen a resolved/closed ticket (FR-RES-04).
- **Auth:** Agent and above, or the action may originate from a customer-facing channel outside this MVP's scope — at MVP, always an internal actor.
- **Headers:** `If-Match`.
- **Request DTO `ReopenTicketRequest`:** `Reason` (string, required).
- **Success `200 OK`:** `TicketResponse.TicketStatus = Open` (or `InProgress`, `[ASSUMPTION]`), `ReopenCount` incremented.
- **Side effect:** current `TicketResolutions.IsCurrent → false` (history preserved, `MVP-ERD.md` §2.14); a new SLA instance period may open depending on policy — flagged `[ASSUMPTION — whether reopen restarts the resolution SLA clock or resumes it is not an explicit requirement; this design assumes a fresh TicketSlaInstances period starts on reopen, consistent with §2.15's "one row per period" model, but this is a business-rule assumption, not an architecture fact]`.
- **Domain events:** `TicketReopened`.

### 3.12 `POST /api/tickets/{ticketId}/duplicate-flag`
- **Purpose:** Recommend/confirm a ticket as a duplicate outside the full resolution flow (a lighter-weight flag some workflows want before formally resolving) — kept as a distinct endpoint from §3.9 since "recommend" and "confirm" are two different actor actions per the requirement text.
- **Auth:** `Recommend`: Agent and above. `Confirm`: Supervisor+.
- **Request DTO `DuplicateFlagRequest`:** `Action` (enum: `Recommend`|`Confirm`|`Reject`, required), `DuplicateOfTicketId` (bigint, required for `Recommend`/`Confirm`), `Note` (string, optional).
- **Success `200 OK`:** `{ TicketId, DuplicateFlagStatus, DuplicateOfTicketId }`.
- **Validation:** `Confirm`/`Reject` only valid on a ticket currently in `Recommended` duplicate-flag state; `403` otherwise.
- **Note:** confirming converts this into the formal `TicketResolutions` row via §3.9 internally (`ResolutionOutcome = Duplicate`) — this endpoint alone does not fully resolve the ticket if called with `Recommend` only.

### 3.13 `GET /api/tickets/{ticketId}/timeline`
- **Purpose:** Unified, chronological view combining `TicketStatusHistory`, `TicketAssignments`, `TicketNotes`, `TicketEscalations`, and `TicketResolutions` for the ticket detail screen's activity feed (FR-TCK-12).
- **Auth:** Agent and above.
- **Query params:** `page`, `pageSize` (default larger, e.g., 50, since this is a merged feed).
- **Success `200 OK`:** paginated `TimelineEntry[]` — `{ EntryType, OccurredAtUtc, ActorEmployeeId (nullable), ActorIsSystem, Summary, Detail (polymorphic per EntryType) }`, sorted `OccurredAtUtc desc`.

---

## 4. Notes and Attachments Module

### 4.1 `POST /api/tickets/{ticketId}/notes`
- **Purpose:** Add an internal note (FR-NOTE-01).
- **Auth:** Agent and above.
- **Request DTO `CreateNoteRequest`:** `NoteText` (string, required, ≤2000 chars), `RelatedStatusChangeId` (bigint, optional).
- **Success `201 Created`:** the created `TicketNotes` row.
- **Validation:** `NoteText` required, non-empty after trim.
- **Note:** immutable once written — no PATCH/PUT/DELETE endpoint exists for notes at all (`MVP-ERD.md` §2.18); a correction is posted as a new note.
- **Audit:** `AuditEntries` (`Action = "AddNote"`).

### 4.2 `GET /api/tickets/{ticketId}/notes`
- **Purpose:** List notes for a ticket.
- **Auth:** Agent and above.
- **Query params:** `page`, `pageSize`.
- **Success `200 OK`:** paginated `TicketNoteResponse[]` sorted `CreatedAtUtc desc`.

### 4.3 `POST /api/tickets/{ticketId}/attachments`
- **Purpose:** Upload an attachment (FR-ATT-01).
- **Auth:** Agent and above.
- **Headers:** `Content-Type: multipart/form-data`.
- **Request:** multipart form with `file` (binary) plus optional `description` field.
- **Success `201 Created`:** `{ TicketAttachmentId, FileName, ContentType, SizeBytes, VirusScanStatus: "Pending", UploadedAtUtc }`.
- **Validation:** `SizeBytes` ≤25MB (`[ASSUMPTION]`, ADR-0017) → `413 Payload Too Large` if exceeded; content-type allow-list (`Security-Architecture.md` §9) → `415 Unsupported Media Type` if rejected; ≤10 attachments per ticket (`MVP-ERD.md` §2.19) → `422` (`type: .../attachment-limit-reached`) if exceeded.
- **Async behavior:** virus scanning happens after upload accept (`VirusScanStatus` starts `Pending`); the file is **not** downloadable by anyone until `VirusScanStatus = Clean` (`MVP-ERD.md` §2.19) — polling or a SignalR event (ADR-0016) informs the client when scanning completes.
- **Domain events:** `AttachmentUploaded` (triggers the async scan job), later `AttachmentScanCompleted`.
- **Audit:** `AuditEntries` (`Action = "UploadAttachment"`).

### 4.4 `GET /api/tickets/{ticketId}/attachments`
- **Purpose:** List attachment metadata for a ticket.
- **Auth:** Agent and above.
- **Success `200 OK`:** array of `{ TicketAttachmentId, FileName, ContentType, SizeBytes, VirusScanStatus, UploadedByEmployeeId, UploadedAtUtc }`. Entries with `VirusScanStatus ≠ Clean` are included but flagged `Downloadable: false`.

### 4.5 `GET /api/tickets/{ticketId}/attachments/{attachmentId}/content`
- **Purpose:** Download the actual file bytes.
- **Auth:** Agent and above.
- **Success `200 OK`:** binary stream, `Content-Disposition: attachment; filename="..."`.
- **Errors:** `403` (`type: .../scan-not-clean`) if `VirusScanStatus ≠ Clean` — enforced on every read, not just at upload (`MVP-ERD.md` §2.19); `404` if attachment doesn't exist or belongs to a different ticket than the path implies.
- **Audit:** `AuditEntries` (`Action = "DownloadAttachment"`) — download access is itself audited given these may contain sensitive photos/documents.

### 4.6 `DELETE /api/tickets/{ticketId}/attachments/{attachmentId}`
- **Purpose:** Remove an attachment, where policy permits (e.g., uploaded in error).
- **Auth:** The uploader, within a short window (`[ASSUMPTION]` 15 minutes), or Supervisor+ at any time.
- **Success `204 No Content`.**
- **Validation:** `403` if requested outside the uploader's removal window by a non-Supervisor.
- **Note:** this is a genuine delete (unlike notes/status history) because an accidentally-uploaded file is not part of the immutable audit narrative the way a status transition is — flagged `[ASSUMPTION]` since the requirement only says "remove ... where policy permits" without defining the policy; the time-boxed self-service + unrestricted Supervisor+ rule above is this design's proposed policy, not a pre-approved one.
- **Audit:** `AuditEntries` (`Action = "DeleteAttachment"`, records `FileName` in `BeforeValue` since the row itself is gone).

---

## 5. SLA and Escalation Module

### 5.1 `GET /api/tickets/{ticketId}/sla`
- **Purpose:** SLA summary for the ticket detail screen — current due dates, state, and whether paused.
- **Auth:** Agent and above.
- **Success `200 OK`, `TicketSlaSummaryResponse`:** `SlaState`, `FirstResponseDueAtUtc`, `FirstResponseBreached`, `ResolutionDueAtUtc`, `ResolutionBreached`, `IsCurrentlyPaused` (bool), `CurrentPauseReason` (nullable), `TotalPausedMinutesThisPeriod` (int).

### 5.2 `POST /api/tickets/{ticketId}/sla/first-response`
- **Purpose:** Record that the First Human Response has occurred (ISSUE-019) — called either directly by an agent action or internally when a Genesys call-answer event satisfies it (§6.3).
- **Auth:** Agent and above, or **System** (internal service-to-service call from the Genesys adapter — not exposed to end-user clients in that case).
- **Headers:** `If-Match`.
- **Request DTO `RecordFirstResponseRequest`:** `OccurredAtUtc` (datetime2, optional — defaults to server "now"; a Genesys-driven call may supply the actual call-answer timestamp), `Source` (enum: `Manual`|`GenesysCallAnswer`, required).
- **Success `200 OK`:** `TicketResponse.FirstHumanResponseAtUtc` set (write-once — see Validation).
- **Validation:** `409` (`type: .../first-response-already-recorded`) if `FirstHumanResponseAtUtc` is already non-null — this field is write-once at the ticket level (`MVP-ERD.md` §2.10).
- **Idempotent (client-supplied key):** yes, specifically to make the Genesys-driven internal call safe to retry without double effects.
- **Domain events:** `FirstResponseRecorded`; finalizes `TicketSlaInstances.FirstResponseBreached` if the response landed after `FirstResponseDueAtUtc` (immutable once set true, per `MVP-ERD.md` §2.15).

### 5.3 `POST /api/tickets/{ticketId}/sla/pause`
- **Purpose:** Manually pause the SLA clock (e.g., `PendingThirdParty`) outside of a status-change-triggered pause (§3.7).
- **Auth:** Agent and above (must be current owner, or Supervisor+).
- **Headers:** `If-Match`.
- **Request DTO `PauseSlaRequest`:** `PauseReason` (tinyint, required: `PendingCustomer`|`PendingThirdParty`).
- **Success `201 Created`:** the created `TicketSlaPausePeriods` row (`ResumedAtUtc: null`).
- **Validation:** `422` (`type: .../critical-never-pauses`) if the ticket's current priority is Critical (`MVP-ERD.md` §2.16's fixed invariant); `409` if already paused.
- **Domain events:** `TicketSlaPaused`.

### 5.4 `POST /api/tickets/{ticketId}/sla/resume`
- **Purpose:** Resume a paused SLA clock, computing and storing `PausedDurationMinutes`.
- **Auth:** Agent and above (must be current owner, or Supervisor+).
- **Headers:** `If-Match`.
- **Success `200 OK`:** the updated `TicketSlaPausePeriods` row with `ResumedAtUtc`/`PausedDurationMinutes` populated; due dates on the current `TicketSlaInstances` row shift forward by the paused duration.
- **Validation:** `409` if not currently paused.
- **Domain events:** `TicketSlaResumed`.

### 5.5 `POST /api/tickets/{ticketId}/sla/priority-upgrade`
- **Purpose:** Increase priority; per ADR-0012, the new due date becomes the **earlier of** the two computed due dates (old-priority due date vs. new-priority due date computed from original creation time).
- **Auth:** Agent and above.
- **Headers:** `If-Match`.
- **Request DTO `UpgradePriorityRequest`:** `NewPriorityId` (tinyint, required, must be numerically higher urgency than current).
- **Success `201 Created`:** the new current `TicketSlaInstances` row (`ChangeReason = Upgrade`), prior row's `PeriodEndAtUtc` set.
- **Validation:** `422` if `NewPriorityId` is not actually an upgrade (use §3.4/§5.7 path instead).
- **Domain events:** `TicketPriorityChanged`, `TicketSlaInstanceReplaced`.
- **Note:** breach flags already `true` on the prior period carry forward as historical fact — they are not reset (`MVP-ERD.md` §2.15's immutability rule).

### 5.6 `POST /api/tickets/{ticketId}/sla/priority-downgrade`
- **Purpose:** Request (and, if the caller is authorized, immediately approve) a priority decrease. Per ADR-0012, any existing breach is preserved regardless of the downgrade.
- **Auth:** Agent and above may **request**; Dept Head+ approval is required for the downgrade to take effect.
- **Headers:** `If-Match`.
- **Request DTO `DowngradePriorityRequest`:** `NewPriorityId` (tinyint, required), `Reason` (string, required), `ApprovingEmployeeId` (guid, required — the endpoint validates this employee actually holds Dept Head+ at call time; a request from an Agent without an already-authenticated Dept Head approver present returns `202 Accepted` with `Status: "PendingApproval"` rather than applying immediately — see below).
- **Success `202 Accepted`** (Agent-initiated, pending) **or `201 Created`** (Dept Head+-initiated or immediately co-signed): the new/pending `TicketSlaInstances` row.
- **Validation:** `403` if `ApprovingEmployeeId` does not hold Dept Head or above.
- **Domain events:** `TicketPriorityDowngradeRequested` (pending case) or `TicketPriorityChanged` + `TicketSlaInstanceReplaced` (approved case).
- **Note:** `TicketSlaInstances.ApprovedByEmployeeId` is required (app-enforced) before a `ChangeReason = Downgrade` row can ever become the current period (`MVP-ERD.md` §2.15) — the pending-approval `202` path exists precisely so this constraint is never bypassed.

### 5.7 `POST /api/tickets/{ticketId}/escalations`
- **Purpose:** Manually escalate a ticket (FR-ESC-01) — distinct from automatic breach/window-based escalation, which is system-generated (see `SLA-Architecture.md` §9, `ADR-0015`'s Hangfire jobs — not exposed as a client-callable endpoint, since it's system-triggered).
- **Auth:** Agent and above for `ManualFlag`; CS Manager or GM only for `ManualLevel4` (`MVP-ERD.md` §2.17).
- **Request DTO `ManualEscalationRequest`:** `Level` (tinyint, required), `TriggerType` (enum, required: `ManualFlag`|`ManualLevel4`), `Note` (string, optional).
- **Success `201 Created`:** the created `TicketEscalations` row.
- **Validation:** `403` if `TriggerType = ManualLevel4` and the actor isn't CS Manager/GM.
- **Domain events:** `TicketEscalated` (drives notification to `NotifiedRoles`, distinct from `Level` per ADR-0011).

### 5.8 `POST /api/tickets/{ticketId}/escalations/{escalationId}/respond`
- **Purpose:** Record a response to an open escalation.
- **Auth:** The notified role-holder (Dept Head/GM/CS Manager as applicable).
- **Request DTO:** `ResponseNote` (string, required).
- **Success `200 OK`:** updated `TicketEscalations` row with `RespondedAtUtc`/`RespondingEmployeeId`.

### 5.9 `GET /api/tickets/{ticketId}/escalations`
- **Purpose:** Escalation history for a ticket.
- **Auth:** Agent and above.
- **Success `200 OK`:** array of `TicketEscalations` rows, sorted `RaisedAtUtc desc`.

### 5.10 `GET /api/business-calendars/{businessCalendarId}`
- **Purpose:** Retrieve the active business calendar definition (working days + hours) used in SLA due-date computation.
- **Auth:** Any authenticated staff (read); System Administrator for edit, see below.
- **Success `200 OK`:** `{ BusinessCalendarId, Name, BusinessDayStartLocal, BusinessDayEndLocal, TimeZone, WorkingDays: [...], EffectiveFromUtc }`.

### 5.11 `POST /api/business-calendars/{businessCalendarId}/holidays`
- **Purpose:** Add a holiday date (ISSUE-012/ISSUE-017).
- **Auth:** System Administrator (enters); a separate confirmation step is flagged `[ASSUMPTION]` per the open question in `Domain-Model.md`'s `Holiday` entity and `MVP-ERD.md` §2.20 — this endpoint creates the row with `ConfirmedByEmployeeId: null`; whether an unconfirmed holiday already affects SLA math is itself unresolved and called out again here rather than silently decided.
- **Request DTO `AddHolidayRequest`:** `HolidayDate` (date, required), `Description` (string, optional).
- **Success `201 Created`:** the created `Holidays` row.
- **Validation:** `409` if `HolidayDate` already exists for this calendar.

### 5.12 `POST /api/business-calendars/{businessCalendarId}/holidays/{holidayId}/confirm`
- **Purpose:** Business-owner (Customer Service/HR) confirmation of a holiday, if the eventual answer to the open question above requires one.
- **Auth:** `[ASSUMPTION — role not yet specified beyond "Customer Service/HR"; modeled here as Supervisor+ in the Customer Service department pending clarification]`.
- **Success `200 OK`:** updated `Holidays` row with `ConfirmedByEmployeeId`/timestamp.

### 5.13 `GET /api/dashboard/sla-backlog`
See §7.3 (kept with the rest of the Dashboard module).

---

## 6. Genesys Basic Integration Module

### 6.1 `POST /api/genesys/webhook`
- **Purpose:** Receive Genesys conversation/interaction events (`Genesys-Integration.md` — webhook-driven design).
- **Auth:** **Not** bearer-token auth. Uses a webhook signature header — placeholder name `X-Genesys-Signature` `[ASSUMPTION — exact header name/scheme is one of the 8 open questions to the Genesys team, Genesys-Integration.md §15 item 2; this document uses a placeholder pending that answer]`. Requests failing signature validation are rejected before any processing.
- **Headers:** `X-Genesys-Signature` (required, placeholder scheme), `Content-Type: application/json`.
- **Request DTO:** provider-neutral mock shape — see `docs/design/Genesys-Mock-Contract.md` for the full field-by-field contract; this endpoint accepts that shape until the real Genesys payload schema is confirmed.
- **Success `202 Accepted`:** `{ Received: true, CorrelationId }` — acknowledges receipt; processing (matching to a ticket, updating `GenesysInteractions`) happens asynchronously via the Outbox pattern so a slow downstream step never blocks the webhook response (ADR-0013).
- **Idempotent (mandatory, not client-optional):** dedup key = `ConversationId + EventType` against `IdempotencyRecords` (Scope = `"GenesysWebhook"`, ADR-0014) — a redelivered event returns the same `202` without reprocessing.
- **Validation:** `401` (`type: .../invalid-signature`) on signature failure; `400` on malformed JSON; **missing optional fields (e.g., `AgentEmailOrExtension`) are accepted, not rejected** — `GenesysInteractions.AgentEmailOrExtension` is nullable specifically to keep ingestion resilient (`MVP-ERD.md` §2.11).
- **Errors:** unrecognized `EventType` values are stored with `ProcessingStatus = Received` and logged, not rejected with an error — an unknown future event type must never cause message loss.
- **Rate limiting:** `[ASSUMPTION]` a higher ceiling than the general default, since Genesys may burst-deliver (e.g., after a Tiger-side outage) — flagged as dependent on open question #5 (delivery guarantees) and #7 (rate limits) in `Genesys-Integration.md` §15.
- **Domain events / Outbox:** `GenesysInteractionReceived` written to `OutboxMessages`; downstream processing performs the agent-lookup and (if `EventType` = call-answered) internally calls the equivalent of §5.2 to satisfy First Human Response.
- **Dead-letter handling:** after `[ASSUMPTION]` 5 failed processing attempts, the `OutboxMessages` row moves to `DeadLettered` and surfaces on §6.4 below rather than retrying forever.

### 6.2 `GET /api/genesys/interactions/{conversationId}`
- **Purpose:** Retrieve a Genesys interaction by its own `ConversationId` — used to check whether/how an inbound call has been processed.
- **Auth:** Agent and above.
- **Path params:** `conversationId` (string).
- **Success `200 OK`:** the `GenesysInteractions` row, including `LinkedTicketId` (nullable) and `ProcessingStatus`.
- **Errors:** `404` if no interaction with that `ConversationId` has been received yet.

### 6.3 `POST /api/genesys/interactions/{conversationId}/link`
- **Purpose:** Manually link a received Genesys interaction to an existing (or newly created via §3.1's `GenesysInteractionId` field) ticket — the MVP's manual-linking-only scope (`Genesys-Integration.md` §6; auto-linking a returning caller is explicitly out of scope per the open question there).
- **Auth:** Agent and above.
- **Request DTO `LinkInteractionRequest`:** `TicketId` (bigint, required).
- **Success `200 OK`:** updated `GenesysInteractions.LinkedTicketId`.
- **Validation:** `409` if already linked to a different ticket.
- **Side effect:** if the interaction's `AnsweredAtUtc` is populated and the ticket's `FirstHumanResponseAtUtc` is still null, this call also satisfies First Human Response via the same internal path as §5.2 (`Source: GenesysCallAnswer`).

### 6.4 `GET /api/genesys/interactions/failed`
- **Purpose:** Operational visibility into interactions stuck in `Rejected` (signature failure) or dead-lettered processing states — the "failed integration events" queue.
- **Auth:** System Administrator, or Supervisor+ in a department that handles Genesys-originated calls.
- **Query params:** `processingStatus` (tinyint, optional filter), `page`, `pageSize`.
- **Success `200 OK`:** paginated list including `LastError`/`Attempts` where available (joined from the underlying `OutboxMessages` row).

### 6.5 `POST /api/genesys/interactions/{conversationId}/retry`
- **Purpose:** Manually retry processing a failed/dead-lettered interaction, where authorized.
- **Auth:** System Administrator.
- **Success `202 Accepted`:** `{ Retrying: true }` — resets `Attempts` handling per the Outbox retry policy (ADR-0013) and re-queues.
- **Validation:** `409` if the interaction isn't currently in a failed/dead-lettered state (nothing to retry).
- **Audit:** `AuditEntries` (`Action = "RetryGenesysInteraction"`).

### 6.6 `POST /api/genesys/agent-mapping`
- **Purpose:** Maintain the (soft, non-FK — `MVP-ERD.md` §2.11) mapping table used to resolve `AgentEmailOrExtension` to an `EmployeeId` at processing time, since this mapping is application logic, not a database constraint.
- **Auth:** System Administrator.
- **Request DTO `UpsertAgentMappingRequest`:** `GenesysAgentId` or `AgentEmailOrExtension` (at least one, required), `EmployeeId` (guid, required).
- **Success `200 OK`:** the stored mapping entry.
- **Note:** this is an application-level lookup table, not a new ERD entity — it exists to support the soft-matching behavior already flagged in `MVP-ERD.md` §2.11, not a new business decision.

---

## 7. Dashboard Module

### 7.1 `GET /api/dashboard/ticket-counts`
- **Purpose:** Counts by status and priority for the operational dashboard's headline tiles.
- **Auth:** Agent and above, scoped to own department by default; Supervisor+ across departments (`?departmentId=` optional override).
- **Query params:** `departmentId` (optional).
- **Success `200 OK`:** `{ ByStatus: { "Open": 12, "InProgress": 30, ... }, ByPriority: { "Critical": 2, "High": 9, ... } }`.

### 7.2 `GET /api/dashboard/department-distribution`
- **Purpose:** Ticket volume distribution across departments — for Supervisor+/CS Manager-level views.
- **Auth:** Supervisor+.
- **Success `200 OK`:** array of `{ DepartmentId, Name, OpenTicketCount, TotalTicketCount }`.

### 7.3 `GET /api/dashboard/sla-backlog`
- **Purpose:** Tickets currently within the warning threshold or already overdue, for the SLA-focused dashboard panel.
- **Auth:** Agent and above (own department); Supervisor+ (all).
- **Query params:** `departmentId` (optional), `onlyBreached` (bool, default `false`).
- **Success `200 OK`:** paginated list of `TicketSummary` (as §3.2) filtered to `SlaState ∈ {Warning, Breached}`, sorted by nearest due date first.

### 7.4 `GET /api/dashboard/sla-breaches`
- **Purpose:** Historical breach counts over a period, for management reporting.
- **Auth:** Supervisor+.
- **Query params:** `fromDate`, `toDate` (required), `departmentId` (optional), `priorityId` (optional).
- **Success `200 OK`:** `{ FirstResponseBreaches: n, ResolutionBreaches: n, ByDepartment: [...], ByPriority: [...] }`.

### 7.5 `GET /api/dashboard/escalation-counts`
- **Purpose:** Open/recent escalation counts by level.
- **Auth:** Supervisor+.
- **Query params:** `departmentId` (optional), `activeOnly` (bool, default `true`).
- **Success `200 OK`:** `{ ByLevel: { "1": 4, "2": 1, "3": 0, "4": 0 } }`.

---

## 8. Explicit Non-Goals of This Contract Set

Per the MVP scope boundary (`docs/architecture/README.md`'s "Explicitly Out of Scope" section), the following are **not** contracted here, and their absence is intentional, not an oversight:
- No customer-facing/self-service endpoints (no Customer Portal, no CSAT survey submission, no WhatsApp/Kiosk/Social Media intake endpoints).
- No AI/ML classification or suggestion endpoints (advanced AI features are out of scope).
- No SMS notification endpoints — MVP notification channel is Email only (`MVP-Data-Dictionary.md` §2.21).
- No bulk-import/bulk-edit endpoints for tickets — not a stated MVP requirement.
- No full KPI/advanced-reporting endpoints beyond the five Dashboard endpoints above — `Architecture-Design.md`'s broader reporting ambitions are a Phase 2+ gate.
- No endpoint exposes raw CRM API pass-through — every CRM interaction is mediated and shaped by the CRM Verification module's own contracts (§2), consistent with ADR-0006.

## 9. What This Document Does Not Cover

No controller/action code, no C# DTO record/class definitions, no ASP.NET routing attributes, no OpenAPI/Swagger YAML or generated client/server stubs, no actual authentication/authorization middleware configuration, no real database queries behind any endpoint. Those are Phase 3 ("Project Foundation") and subsequent implementation-sprint deliverables, built *from* this contract, not included in it.
