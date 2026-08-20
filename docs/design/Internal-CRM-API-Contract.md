# Tiger Group — CS Ticketing System
## Internal CRM API Contract — Read-Only Data Access

| | |
|---|---|
| **Status** | The exact, minimal read-only surface Tiger CS Ticketing's Customer Verification module requires from Tiger Group's in-house CRM. Not a request for new CRM functionality — every operation below is a lookup already possible in principle against existing CRM unit/contact records (ADR-0006). |
| **Scope** | Three operations only: unit lookup by CRM identifier, unit search by unit number, and contact lookup for a unit. Read-only. No write path, no verification logic, no business rules — those are Tiger CS Ticketing's own responsibility (see §0, the Ownership Boundary). |
| **Explicitly not done here** | No CRM implementation. No authentication/authorization scheme for the CRM API itself (owned by the CRM team, TBD — see §5). No confirmed SLA/rate-limit numbers (§5, open). This document is the *consumer-side* contract that Tiger CS Ticketing's `ICrmGateway` is built against and that `MockCrmGateway` fakes for the pilot — implementing this contract for real, against the actual in-house CRM, is the CRM team's own separate work. |
| **Base** | `implementation/mvp-crm-verification` branch; refines `docs/design/MVP-API-Contracts.md` §2.1-§2.3 and ADR-0006 to the exact shape the shipped code (`ICrmGateway`) already compiles against |
| **Related documents** | `docs/design/MVP-ERD.md` §2.7, `docs/design/MVP-Data-Dictionary.md` §2.7, `docs/architecture/adr/0006-crm-as-source-of-truth.md`, `docs/architecture/adr/0007-immutable-crm-snapshot-on-ticket.md`; `src/TigerCS.Application/Modules/CustomerVerification/CrmIntegration/ICrmGateway.cs` — the C# mirror of this document |
| **Date** | 2026-08-20 |

---

## 0. Ownership Boundary (read this first)

**Tiger CRM's entire responsibility toward Tiger CS Ticketing is passive, read-only data access.** Tiger CRM:

- looks up a unit by unit number;
- returns that unit's immutable CRM-issued identifier and the minimum unit display details;
- returns the contacts/owners/tenants/authorized representatives linked to that unit.

Tiger CRM does **not**: implement or know about a `VerificationSession`, decide whether a requester is verified, decide whether a ticket may be created, enforce any Tiger CS Ticketing authorization/audit/expiry rule, or receive write traffic from Tiger CS Ticketing. All of that is Tiger CS Ticketing's own business logic — see `VerificationSessionAppService`'s remarks in code for the full statement of what Tiger CS Ticketing owns: `VerificationSession` and its state, selecting the requester, recording the verification method/result, deciding whether ticket creation is allowed, the immutable verification-time snapshot, and audit/authorization/expiry rules.

This document exists only to pin down the three read operations Tiger CS Ticketing needs, precisely, so the CRM team has an unambiguous target to build (or expose) against — nothing more.

**A future automated intake surface (e.g. Genesys AI voice/chat, once built) must call Tiger CS Ticketing's own API to have a requester verified. It must never call the CRM API described here directly, and must never implement any of the logic this boundary reserves for Tiger CS Ticketing.**

---

## 1. Operations Required

### 1.1 Get Unit by CRM Unit ID

- **Purpose:** Resolve a unit already known by its CRM-issued identifier — the common case once a unit has been looked up once and cached (`MVP-API-Contracts.md` §2.1).
- **Input:** `crmUnitId` (string) — the CRM's own identifier for the unit.
- **Output:** one Unit record (§2.1), or "not found" (§3).
- **Consumed by:** `ICrmGateway.GetUnitAsync`.

### 1.2 Search Units by Unit Number

- **Purpose:** Resolve a unit from the raw, as-spoken unit number an agent hears on a call, before the CRM identifier is known (`MVP-API-Contracts.md` §2.2). Ambiguity (multiple matches — e.g. the same unit number recurring across different properties) is expected and resolved by the agent, not by the CRM.
- **Input:** `unitNumber` (string, required); `propertyName` (string, optional — narrows ambiguous matches).
- **Output:** zero, one, or several Unit records (§2.1).
- **Consumed by:** `ICrmGateway.SearchUnitsAsync`.

### 1.3 Get Contacts for a Unit

- **Purpose:** Return every contact linked to a unit — owner(s), tenant(s), and any CRM-recorded authorized representative — so Tiger CS Ticketing can identify which specific contact is on the call (`MVP-API-Contracts.md` §2.3, FR-VER-04, ISSUE-007).
- **Input:** `crmUnitId` (string).
- **Output:** zero or more Contact records (§2.2).
- **Consumed by:** `ICrmGateway.GetContactsAsync`.

**That is the complete list.** No fourth operation, no write operation, no bulk/batch operation, and no operation returning anything beyond §2's fields is required for this pilot.

---

## 2. Data Shapes Required

### 2.1 Unit

| Field | Type | Required | Notes |
|---|---|---|---|
| `crmUnitId` | string | Yes | The CRM's own, immutable identifier for the unit. Tiger CS Ticketing never invents or mutates this — it is the sole key by which a unit's cache row is upserted (`UnitReferences.CrmUnitId`, unique index). |
| `unitNumber` | string | Yes | As displayed/spoken — the field §1.2's search takes as input and the field read back to the caller (FR-VER-03). |
| `propertyName` | string | No | Display only. |
| `towerName` | string | No | Display only. |
| `unitType` | string | No | Display only (e.g. "Residential", "Commercial"). |

### 2.2 Contact

| Field | Type | Required | Notes |
|---|---|---|---|
| `crmContactId` | string | Yes | The CRM's own, immutable identifier for the contact. Sole key by which a contact's cache row is upserted (`ContactReferences.CrmContactId`, unique index). |
| `displayName` | string | No | Read back to confirm identity (FR-VER-03). |
| `contactChannel` | string | No | The phone/email on file for this contact — used to cross-check that the caller matches CRM's own record. Not evidence of, or a restriction to, any particular verification channel (see §4). |
| `contactType` | enum: `Owner` \| `Tenant` \| `Representative` | Yes | Governs disclosure rules downstream (BR-030, ISSUE-007) — Tiger CS Ticketing's own business logic, not the CRM's. |
| `authorizedRepresentativeOfCrmContactId` | string | No | Populated only when `contactType = Representative`: the `crmContactId` of the owner/tenant this contact is CRM-recorded as authorized to represent. Required for ISSUE-007's disclosure rule; must reflect the CRM's own authorization record, never a self-declared claim accepted at face value by Tiger CS Ticketing. |

---

## 3. Availability / Error Contract

- A request for an unknown `crmUnitId` or unit number returns "not found" (an empty result) — not an error. Genuinely nonexistent units are expected traffic (e.g. a mistyped unit number read over the phone).
- Any other failure (timeout, 5xx, network error) must be distinguishable from "not found." Tiger CS Ticketing's own `CrmGatewayUnavailableException` maps this to a `502`/`504` at its own API boundary (`MVP-API-Contracts.md` §2.1) and is the trigger for the CRM-outage `IntakeRecord` fallback flow — Tiger CS Ticketing's own business logic, out of scope for this document and not yet built.
- **[OPEN — flagged, not assumed]** Exact latency/availability SLA, the authentication scheme for this CRM API, rate limits, and whether §1.2's unit-number search is exact-match, substring, or fuzzy are all CRM-team-owned decisions, not yet confirmed. `MockCrmGateway` — the pilot's fixture-backed stand-in, never production-ready — makes no claim about any of these; see its own remarks.

---

## 4. Explicitly Not Assumed

- **Not phone/verbal-only.** Nothing in this contract assumes the calling channel is a phone call, or that confirmation of a match happens verbally. `contactChannel` is the CRM's own on-file contact detail (used to cross-check a caller's claimed identity), not a channel restriction on this API. Tiger CS Ticketing's own `VerificationSession.Confirm()` is channel-neutral for the same reason (see that type's remarks) — a future Website/App/WhatsApp/kiosk channel (Solution-Analysis.md §2.2's FR-CH-02/03, Phase 2+) would call the same three operations above, unchanged, with its own confirmation mechanism.
- **Not a verification authority.** This API returns raw unit/contact data only. It has no opinion on whether a caller is who they claim to be, whether a ticket may be created, or any audit/expiry concern — all of that is reserved to Tiger CS Ticketing (§0).
- **Not a write API.** No operation here creates, updates, or deletes anything in the CRM. Tiger CS Ticketing's own `UnitReferences`/`ContactReferences` cache tables are refreshed by re-calling §1's read operations, never by writing back to the CRM.

---

## 5. Open Items

1. Real CRM endpoint URLs, authentication scheme, and hosting details — not yet available (why `MockCrmGateway` exists at all, per backlog S-06).
2. Exact match semantics for §1.2's unit-number search (exact vs. substring vs. fuzzy) — CRM-team-owned.
3. Latency/availability SLA and rate limits — CRM-team-owned; needed before the CRM-outage fallback's timing (`IntakeRecord`, not yet built) can be tuned with real numbers rather than a placeholder.
4. Whether the CRM can report a unit's *current* authorized-representative set live, or whether that changes on a slower cadence than unit/tenant data — affects `ContactReferences`' refresh-on-lookup cache-staleness assumption (`CrmUnitLookupAppService`'s own remarks).

---

This document is the read-only data contract; it is not an API implementation, not an OpenAPI spec, and not authorization for the CRM team to build anything beyond what §1–§2 describe. `ICrmGateway` (`src/TigerCS.Application/Modules/CustomerVerification/CrmIntegration/ICrmGateway.cs`) is the C# mirror of this contract that Tiger CS Ticketing's code actually compiles against.
