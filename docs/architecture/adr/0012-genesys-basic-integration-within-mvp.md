# ADR-0012: Genesys/Geyness Basic Integration within MVP

**Status:** Accepted, conditional on ISSUE-003 resolution — **see the scope note below; a documentation follow-up is required.**
**Date:** 2026-08-17

## Context

The source requirements and both workflow diagrams name **"Geyness"** as the contracted call-center vendor throughout; a separate reference to **"Genesys"** (a distinct, well-known CCaaS platform) surfaced when this engagement was commissioned. Whether Geyness's platform *is* Genesys, another named product, or an internally built system remains unconfirmed — tracked as **ISSUE-003**, still open in Group B ("Required Before Phase 2") of the Technical Decision Register.

The previously approved MVP scope (`Tiger-CS-Ticketing-Solution-Analysis.md` §15) explicitly placed the full Geyness/Genesys call-center **platform** integration (INT-02) in **Phase 2**, on the basis that MVP's intake model is phone-only and manually operated by an agent inside the ticketing system itself, with no dependency on an external CCaaS API. Management has since asked for a **basic** Genesys integration to be included within MVP.

## Decision

Include a narrowly-scoped, adapter-based "basic" integration within MVP: `TigerCS.Integrations` exposes a minimal, decoupled `ICallCenterGateway` interface (per ADR-0001's module boundaries) capable of receiving call-metadata handoff — e.g., an inbound call's originating number and a correlation identifier — from whatever platform Geyness/Genesys actually turns out to be, without depending on a specific CCaaS vendor's SDK or deep API surface. No richer capability (live call-recording retrieval, agent-desktop screen-pop automation, outbound dialing control) is included at this "basic" level; those remain Phase 2 scope.

## Alternatives Considered

- **Defer all Geyness/Genesys platform integration to Phase 2** as originally scoped — no MVP-level integration at all.
- **Build a full, deep integration** against the Genesys Cloud CX API now, assuming Genesys is confirmed as the platform.
- **A narrow, adapter-based "basic" integration, decoupled from the unconfirmed vendor identity** (chosen).

## Advantages

- Delivers the management-requested MVP capability without committing to API-specific integration work against a vendor/platform that is still unconfirmed — the adapter can be pointed at whichever platform is ultimately confirmed, with minimal rework.
- Keeps the integration surface small and testable within the MVP timeline, consistent with the Integration Gateway pattern already established for CRM/Email/File Storage (ADR-0001), rather than introducing a large, unplanned scope addition.
- Provides an early, low-risk validation point for the call-center handoff mechanism ahead of Phase 2's deeper integration work.

## Disadvantages

- **This decision changes the MVP scope boundary previously documented and approved in `Tiger-CS-Ticketing-Solution-Analysis.md` §15 and §8**, which explicitly listed the Geyness/Genesys platform integration (INT-02) as Phase 2, gated on ISSUE-003. That document has **not yet been amended** to reflect this change, and it now contradicts this ADR until it is.
- A "basic" integration built before ISSUE-003 is resolved still carries real rework risk if the eventually-confirmed platform's actual API shape differs materially from the generic adapter's assumptions.
- Splitting call-center integration into a "basic MVP" piece and a "full Phase 2" piece adds a seam that must be designed carefully, so the Phase 2 work extends the adapter rather than replacing it outright.

## Consequences

`TigerCS.Integrations` gains a minimal `ICallCenterGateway` interface and adapter at MVP, implemented generically enough not to assume Genesys specifically. **ISSUE-003 must still be resolved** before this adapter's concrete implementation is finalized, and before any Phase 2 deepening of the integration begins.

**Required follow-up:** amend `Tiger-CS-Ticketing-Solution-Analysis.md` §15 (MVP scope) and §8 (INT-02's phase tag) to reflect this change explicitly, so the documented MVP boundary matches this ADR rather than contradicting it. This ADR does not itself make that edit, since it was scoped as an ADR-only task; the amendment should be confirmed with management before being applied, given it reopens a previously closed scope question.
