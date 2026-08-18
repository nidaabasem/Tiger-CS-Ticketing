# ADR-0019: Genesys Basic Integration

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective) — **review earlier if the open questions for the Genesys team (see `Genesys-Integration.md`) are not answered before Phase 3 coding begins.**

## Context

An earlier ADR (PR #2, ADR-0012 in that log) recorded a basic Genesys/Geyness call-center integration as a management-requested addition to MVP, flagged **conditional** because the vendor/platform identity (ISSUE-003: is the platform "Geyness" or "Genesys," and are they the same thing?) was still unresolved, and because it contradicted the Solution Analysis's then-current MVP scope (§15), which placed the full platform integration in Phase 2.

This request explicitly specifies **"Genesys APIs and webhooks"** in the technology stack and states **"Genesys Basic Integration is included in the MVP"** as a management-approved direction. This resolves ISSUE-003's core question (the platform is Genesys) and supersedes the earlier ADR's conditional status.

## Decision

Include a Genesys Basic Integration within MVP, scoped to: receiving and processing Genesys webhooks for conversation ID, caller number, Genesys agent ID, agent email/extension (when available), channel/media type, and interaction start/answer/end timestamps; linking the Genesys interaction to a ticket; treating the interaction's answer timestamp as satisfying First Human Response (operationalizing ISSUE-019, ADR-0009) when a ticket is linked; idempotent, signature-validated webhook processing with correlation IDs (ADR-0014); retry/failure handling; and a manual fallback (the existing phone-only manual flow) if Genesys is unavailable. No richer capability (outbound dialing control, call-recording retrieval, deep agent-desktop automation) is in scope for this basic integration.

## Alternatives Considered

- **Defer all Genesys platform integration to Phase 2** as originally scoped (superseded by this explicit management direction).
- **A full, deep integration** against the entire Genesys Cloud CX API surface now.
- **A narrow, webhook-driven "basic" integration** scoped to the fields listed above (chosen).

## Advantages

- Delivers the confirmed MVP capability without committing to the full breadth of the Genesys API surface within a 3-week pilot.
- The required-field list (Conversation ID, caller number, agent ID, etc.) maps directly onto a bounded, testable webhook contract (`Genesys-Integration.md`).
- Reuses the same reliability patterns (Outbox, idempotency, correlation IDs) already established for CRM and Email, rather than inventing a bespoke mechanism.

## Disadvantages

- A basic integration built now still carries rework risk if Genesys's actual webhook payload shape or authentication mechanism differs from the conceptual contract in `Genesys-Integration.md` (open questions listed there for the Genesys team).
- Splitting "basic MVP" from "full Phase 2" integration creates a seam that must be designed so Phase 2 extends this adapter rather than replacing it outright.
- The mapping from a Genesys agent ID to an internal `Employee` record depends on agent email/extension being reliably available from Genesys — not yet confirmed for every agent.

## Consequences

`TigerCS.Integrations` gains a `GenesysWebhookGateway` (or equivalent) implementing the contract in `Genesys-Integration.md`. **This ADR formally supersedes ADR-0012 from the prior ADR log (PR #2)** — that log's "conditional" flag is resolved by this explicit management direction. The Solution Analysis's MVP scope (§15) and integration tier (§8, INT-02) should be amended to match; see the open-questions/follow-up note in `Genesys-Integration.md`.

## Risks

- **Highest-risk item in this architecture package.** The webhook contract, authentication/signature mechanism, and agent-identity mapping are all conceptual pending confirmation from the Genesys team — listed explicitly in `Genesys-Integration.md`'s "Open technical questions" section. Coding against unconfirmed assumptions here is the single most likely source of pilot rework.
