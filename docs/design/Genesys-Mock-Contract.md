# Tiger Group — CS Ticketing System
## Genesys Mock Contract (Provider-Neutral, Temporary)

| | |
|---|---|
| **Status** | **Provisional / mock only — NOT the official Genesys schema.** For design and future implementation-planning purposes only. |
| **Scope** | A provider-neutral, best-guess shape for the webhook/event payload `docs/design/MVP-API-Contracts.md` §6.1 (`POST /api/genesys/webhook`) will receive, built so design work (API contracts, ERD field mapping, wireframes) can proceed without blocking on the real Genesys integration team. |
| **Explicitly not done here** | No real Genesys API/webhook documentation was consulted or reproduced. No claim is made that this matches Genesys Cloud's actual event schema, field names, or delivery mechanism. No code (adapter, DTO, mapper) is implemented from this. |
| **Base** | `main` @ `4fe6f19`; refines `docs/architecture/Genesys-Integration.md` |
| **Related documents** | `docs/architecture/Genesys-Integration.md` (§15 lists the 8 open questions this mock exists to work around), `docs/design/MVP-ERD.md` §2.11 / `docs/design/MVP-Data-Dictionary.md` §2.11 (`GenesysInteractions` entity), `docs/design/MVP-API-Contracts.md` §6 |
| **Date** | 2026-08-18 |

---

## 0. Why This Document Exists, and Its One Rule

`Genesys-Integration.md` §15 lists eight questions that only the Genesys team (or a real sandbox) can answer: delivery mechanism, signature scheme, exact payload field names, agent-identity reliability, delivery guarantees, channel scope, rate limits, and sandbox availability. Detailed design (API contracts, ERD, wireframes) cannot simply stop and wait for those answers without blocking the entire pilot. This document fills that gap with an explicit, clearly-labeled **placeholder** contract.

**The one rule governing this document: nothing here may be treated as confirmed.** Every field, behavior, and assumption below is either marked `[MOCK]` (a stand-in name/shape we invented) or `[TO CONFIRM]` (a behavior that depends on an answer only Genesys can give). **This entire contract must be replaced or explicitly re-mapped once the real Genesys payload schema and delivery mechanism are confirmed** — that re-mapping is Phase 3 work, not a re-run of this document.

---

## 1. Mock Event Payload

```json
{
  "eventId": "9f3b2e7a-1c44-4e2a-8a6a-5e2f0a9d7b10",
  "eventType": "conversation.call.answered",
  "conversationId": "conv-88213-af9c",
  "callerNumber": "+9715xxxxxxx",
  "agentId": "genesys-agent-4471",
  "agentEmail": "j.smith@tigergroup.example",
  "agentExtension": "4471",
  "mediaType": "voice",
  "direction": "inbound",
  "startedAt": "2026-08-18T09:14:02Z",
  "answeredAt": "2026-08-18T09:14:19Z",
  "endedAt": null,
  "recordingUrl": null,
  "correlationId": "b7e1c9a0-2222-4a11-9abc-1234567890ab",
  "metadata": {
    "queueName": "CS-General",
    "wrapUpCode": null
  }
}
```

## 2. Field-by-Field Contract

| Field | Type | Required | Notes |
|---|---|---|---|
| `eventId` | string (guid) | **Yes** | `[MOCK]` invented as the idempotency anchor. **[TO CONFIRM]** whether Genesys issues a stable per-event ID at all, or whether dedup must instead key off `conversationId + eventType` (this mock's own webhook endpoint, `MVP-API-Contracts.md` §6.1, actually dedupes on the latter for exactly this reason — see §5). |
| `eventType` | string | **Yes** | `[MOCK]` names, loosely modeled on plausible Genesys Cloud topic names (e.g., `conversation.call.answered`, `conversation.call.ended`). **[TO CONFIRM]** real enum values — see `Genesys-Integration.md` §15 item 3. |
| `conversationId` | string | **Yes** | Assumed to be Genesys's own stable identifier for the call/interaction. Maps to `GenesysInteractions.ConversationId` (`MVP-Data-Dictionary.md` §2.11), which is defined as unique. |
| `callerNumber` | string | No | E.164-ish format assumed. Maps to `GenesysInteractions.CallerNumber` — masked in logs per `Security-Architecture.md` §11 the moment it's received, never stored or logged unmasked. |
| `agentId` | string | No | Genesys's own agent identifier. Maps to `GenesysInteractions.GenesysAgentId`. |
| `agentEmail` | string | No | `[MOCK]` — this mock **splits** email and extension into two fields; the ERD's `GenesysInteractions.AgentEmailOrExtension` column (`MVP-Data-Dictionary.md` §2.11) stores **whichever one is populated** as a single combined field. This is a deliberate reconciliation, not an oversight — see §6 below. |
| `agentExtension` | string | No | `[MOCK]` — see `agentEmail` above. |
| `mediaType` | string | **Yes** | MVP scope is voice only (`Genesys-Integration.md`); `[TO CONFIRM]` whether "Genesys Basic Integration" could ever surface a non-voice value — `Genesys-Integration.md` §15 item 6. |
| `direction` | string | No | `inbound`/`outbound`. `[MOCK]` — not used by any current API contract field but included for completeness/future use. |
| `startedAt` | string (ISO 8601, UTC) | **Yes** | All timestamps in this contract are ISO 8601 with a `Z` (UTC) suffix — no local-time payloads are accepted. |
| `answeredAt` | string (ISO 8601, UTC) | No | Null until the call is actually answered; its presence is what satisfies First Human Response (`MVP-API-Contracts.md` §5.2, `Source: GenesysCallAnswer`). |
| `endedAt` | string (ISO 8601, UTC) | No | Populated by a later event as the call concludes — see §4 (out-of-order handling). |
| `recordingUrl` | string (URL) | No | `[MOCK]` — only present "when available"; must never be assumed present. |
| `correlationId` | string (guid) | No | `[MOCK]` — for tracing a single logical interaction across multiple webhook deliveries (start/answer/end), distinct from `eventId`. Maps to `GenesysInteractions.CorrelationId` (ADR-0014). |
| `metadata` | object | No | `[MOCK]` — free-form bag for anything not otherwise modeled (queue name, wrap-up code, etc.). Not individually validated; stored as-is in the interaction record's raw-payload archive (not a modeled ERD column) for forward compatibility. |

---

## 3. Authentication and Signature Validation (Placeholder)

- **Auth placeholder:** inbound requests are expected to carry a header — this mock names it `X-Genesys-Signature` — computed as an HMAC over the raw request body using a shared secret provisioned out-of-band. **[TO CONFIRM]** the real header name and signing scheme (`Genesys-Integration.md` §15 item 2); this mock's name and algorithm choice are both placeholders, not a Genesys specification.
- **Signature-failure behavior — finalized in the senior-architecture-review pass (Finding DR-04), resolving a contradiction that existed between this section and the data dictionary:** requests failing signature validation are rejected `401` **before any parsing or persistence**, and are **not** written to `GenesysInteractions`, `GenesysInteractionEvents`, or any audit table — they never reach the idempotency/dedup layer described in §4/§5. Earlier drafts of `MVP-Data-Dictionary.md` §2.11 listed a `ProcessingStatus = Rejected (signature failure)` value, which implied such a request *was* persisted with that status — that value has been removed from the data model entirely, and this document's "rejected before persistence" behavior is now the single, consistent source of truth.
- **What is recorded instead:** a lightweight **security-log** entry (a separate, non-application-audit sink — e.g. the standard security/SIEM-bound log stream), containing only: timestamp, source IP (if available), request byte-length, and outcome (`SignatureRejected`). **This entry never contains the raw request body.** Since an unauthenticated request's claimed `ConversationId`/`CallerNumber`/any other field cannot be trusted, none of those claimed values are logged either — logging them would risk persisting attacker-supplied or malformed PII under the guise of a legitimate field. A sustained run of signature failures is an operational/alerting concern (repeated failures may indicate a misconfigured secret or an attack), not a data-model concern.
- **[TO CONFIRM]** whether Genesys signs the raw body, a canonicalized form, or something else (headers-plus-body, etc.) — the exact canonicalization matters for signature verification and cannot be guessed.

---

## 4. Behaviors to Document

**Idempotency model corrected in the senior-architecture-review pass (Finding DR-03).** The original model bound a single `IdempotencyRecords` row directly to a `GenesysInteractions` row — the wrong grain, since one conversation produces multiple events over its lifecycle, and the old model would have accepted only the first event per conversation and silently dropped every subsequent one. Idempotency is now tracked per received event via `GenesysInteractionEvents` (`MVP-ERD.md` §2.26):

- **Preferred key — provider `EventId`:** when `eventId` is confirmed by Genesys to be stable and unique per delivery (`[TO CONFIRM]` — `Genesys-Integration.md` §15 item 1, still open), the idempotency key is `"GenesysEvent:" + eventId`. This is the simplest, most reliable option and is used **as soon as it can be trusted**, not held back in favor of the fallback below once confirmed.
- **Safe fallback key — used until `eventId` reliability is confirmed, or for any event where it is absent:** `ConversationId + EventType + RawPayloadHash + a short time-bucket (`[ASSUMPTION]` 5 seconds)`. This is deliberately **not** a bare `ConversationId + EventType` key (the original design's fallback) — a bare key would incorrectly collapse two *genuinely distinct* events of the same type on the same call (e.g., a call placed on hold twice) into a single stored event, silently losing the second one. Including `RawPayloadHash` and a short time-bucket means only **near-identical redeliveries arriving close together in time** are treated as duplicates; two real, separate occurrences of the same event type — which will normally differ in timestamp and often in other payload fields — fall into different time-buckets or hash differently, and are both retained.
- **`RawPayloadHash`, not the raw payload:** the hash (not the payload itself) is what participates in the fallback key and is what gets persisted (`GenesysInteractionEvents.RawPayloadHash`) — the raw body is never stored, consistent with §3's signature-failure logging rule and the broader "no sensitive raw payload persisted" principle (Finding DR-04).
- **`ConversationId` uniqueness behavior (unchanged from the original design):** one `ConversationId` may generate multiple distinct webhook deliveries over the call's lifecycle (started → answered → ended, and possibly repeated events of the same type). All events for one conversation map to **one** `GenesysInteractions` parent row, progressively filled in on an apply-if-absent basis (`StartedAtUtc` on the first event, `AnsweredAtUtc`/`EndedAtUtc` on later ones, never overwritten once set) — while each individual delivery is now also recorded as its own `GenesysInteractionEvents` child row. `[TO CONFIRM]` — this assumes Genesys's real model works the same way.
- **Retry expectations:** `[TO CONFIRM]` delivery guarantees (`Genesys-Integration.md` §15 item 5) — this mock assumes **at-least-once** delivery (the safer assumption) and is built to tolerate redelivery via the idempotency key above, never to assume at-most-once.
- **Duplicate-event handling:** a duplicate (same resolved idempotency key, whichever form is currently in effect) is acknowledged `202` but not reprocessed — see `MVP-API-Contracts.md` §6.1. This is now enforced at the individual-event grain, so a duplicate `answered` event does not affect processing of a distinct, later `ended` event for the same conversation.
- **Unknown-agent behavior:** if `agentId`/`agentEmail`/`agentExtension` cannot be resolved to an `Employee` via `GenesysAgentMappings` (`MVP-ERD.md` §2.25, `MVP-API-Contracts.md` §6.6 — backed by a real table as of Finding DR-02), the interaction is still stored and processed — an unresolved agent link **never blocks ingestion or ticket-linking**, since the only mandatory correlation for MVP is `ConversationId` ↔ `Ticket`, not agent identity.
- **Missing-field behavior:** every field marked "No" (not required) above may be absent or null without rejecting the event — see `MVP-API-Contracts.md` §6.1's validation notes. Only `eventId`, `eventType`, `conversationId`, `mediaType`, and `startedAt` are treated as required; an event missing one of those five is rejected `400` and logged, not silently dropped.
- **Out-of-order event handling:** because delivery is assumed at-least-once and `[TO CONFIRM]` whether ordering is guaranteed, an `ended` event may in principle be processed before an `answered` event (e.g., under redelivery/retry races). Processing logic applies each field to the parent `GenesysInteractions` row independently and idempotently (e.g., "set `EndedAtUtc` if not already set, regardless of arrival order") rather than assuming a strict start→answer→end sequence — this is unchanged from the original design and remains correct under the corrected per-event idempotency model.
- **Manual fallback:** if no webhook for a live call arrives within a configured window, or the Genesys channel is down entirely, agents fall back to fully manual ticket creation (`MVP-API-Contracts.md` §3.1, now via a `VerificationSessions` flow with `GenesysInteractionId: null` — Finding DR-01) — consistent with `Genesys-Integration.md`'s manual-fallback design. This mock does not attempt to detect "missing" events itself; that detection (a timeout/reconciliation job) is a Phase 3 implementation concern.
- **Logging/audit:** `CallerNumber` is masked before any log line is written (`Security-Architecture.md` §11); the raw inbound payload is **never** persisted or archived, for accepted events or rejected ones alike — only `RawPayloadHash` (accepted events, §2.26) or minimal security-log metadata (rejected events, §3) is retained.
- **Dead-letter handling:** an **event** that fails processing repeatedly (`[ASSUMPTION]` 5 attempts, per `MVP-API-Contracts.md` §6.1) moves to a dead-lettered state, surfaced via `GET /api/genesys/interactions/failed` (§6.4 there) for manual review/retry — it is never silently discarded. This is corrected to be per-event, not per-conversation, matching the rest of this section's grain fix.

---

## 5. Reconciling This Mock Against the `GenesysInteractions` Entity

**Corrected in this review pass (Finding DR-03):** the columns below live on `GenesysInteractions` (the aggregate, per-conversation row) unless marked otherwise; `eventId`, `correlationId`, and the raw-payload-derived hash are per-event and now live on `GenesysInteractionEvents` (`MVP-Data-Dictionary.md` §2.26) instead.

| Mock field | Column (`MVP-Data-Dictionary.md`) | Reconciliation note |
|---|---|---|
| `eventId` | `GenesysInteractionEvents.ProviderEventId` (§2.26) | **Corrected:** previously described as "not a column"; it is now persisted, per event, and is the **preferred** idempotency-key input once Genesys confirms its reliability (§4 above). |
| `conversationId` | `ConversationId` | Direct map. |
| `callerNumber` | `CallerNumber` | Direct map (masked at rest/in logs per Security-Architecture.md). |
| `agentId` | `GenesysAgentId` | Direct map. |
| `agentEmail` **or** `agentExtension` | `AgentEmailOrExtension` (single column) | **Deliberate collapse**: the ERD models one nullable string column, populated with whichever of the mock's two separate fields is present (email preferred if both happen to be present, `[ASSUMPTION]`). This keeps the schema simple given the field's reliability is already an open question (`Genesys-Integration.md` §15 item 4) — no value in modeling two separate nullable columns for data whose presence at all is unconfirmed. |
| `mediaType` | `ChannelMediaType` | Direct map. |
| `startedAt` | `StartedAtUtc` | Direct map. |
| `answeredAt` | `AnsweredAtUtc` | Direct map; also drives First Human Response satisfaction. |
| `endedAt` | `EndedAtUtc` | Direct map. |
| `recordingUrl` | *(not a column)* | Not modeled in the MVP ERD at all — no requirement calls for storing/serving recordings in this pilot. Flagged here so a future reader knows the omission is deliberate, not a gap. |
| `correlationId` | `GenesysInteractionEvents.CorrelationId` (§2.26) | **Corrected:** moved to the per-event table, consistent with the rest of this correction — a `correlationId` traces one delivery, not the whole conversation. |
| *(derived, not from the mock payload)* | `GenesysInteractionEvents.RawPayloadHash` (§2.26) | A hash of the canonicalized payload, computed on receipt — **the raw payload itself is never persisted**, per Finding DR-04. Used as an input to the fallback idempotency key (§4). |
| `metadata` | *(not a column)* | No modeled column; if retained at all in Phase 3, it would need its own JSON column or side-table — out of scope for this design pass since no requirement currently reads any `metadata` sub-field. |
| *(derived)* | `LinkedTicketId`, `ProcessingStatus` | Not part of the inbound payload at all — these are set by Tiger's own processing logic (`MVP-API-Contracts.md` §6.1/§6.3), not received from Genesys. |

---

## 6. Open Questions Requiring Genesys-Team Confirmation

These restate — and do not duplicate resolution of — `Genesys-Integration.md` §15's list, scoped specifically to what this mock had to guess at:

1. Real webhook delivery mechanism (push endpoint Tiger exposes vs. Genesys-hosted queue Tiger polls/subscribes to) — determines whether `POST /api/genesys/webhook` (`MVP-API-Contracts.md` §6.1) is even the right shape of endpoint.
2. Real signature/auth header name and canonicalization scheme (§3 above).
3. Real event-type enum values and exact field names/casing (this mock's `eventType` strings and field names are invented).
4. Reliability/presence of agent email or extension on every interaction-answered event (§5's reconciliation note).
5. Delivery guarantees — at-least-once confirmed or not, and whether ordering is guaranteed (§4's out-of-order handling note).
6. Whether "Genesys Basic Integration" covers voice only or other channels.
7. Rate limits/API quotas.
8. Sandbox/test environment availability within the pilot window.

## 7. What This Document Does Not Cover

No real Genesys Cloud (or other CX platform) API/webhook schema, no working adapter/mapper code, no signature-verification implementation, no sandbox credentials or configuration. This document's sole purpose is to let `MVP-API-Contracts.md` §6 and `MVP-ERD.md`/`MVP-Data-Dictionary.md` §2.11 be written to *something* concrete now, with every guess clearly labeled, so Phase 3 can replace this document's content wholesale once real answers arrive — without having to re-derive which design decisions depended on which guess.
