# Tiger Group — CS Ticketing System: Architecture Documentation

**Status: Approved for Architecture Design.** This package covers the 3-week internal pilot MVP, following management's review and approval of the MVP direction, including the addition of Genesys Basic Integration within MVP (see "What changed" below).

**No application code, ERD regeneration, SQL schema, EF Core migrations, API implementation, or project scaffolding has been produced.** This is design documentation only. Phase 3 ("Project Foundation") remains the next, separately-authorized step.

## Required Review Order

1. **[System Architecture](System-Architecture.md)** — start here: system context, modules, flows, deployment
2. **[Architecture Decision Records](adr/)** — the 24 ADRs behind the technical choices in (1), including ADR-0023's .NET 10 framework upgrade and ADR-0024's System Administrator authorization override
3. **[Domain Model](Domain-Model.md)** — the conceptual entities the architecture operates on
4. **[SLA Architecture](SLA-Architecture.md)** — the most detailed, highest-stakes subsystem
5. **[Genesys Integration](Genesys-Integration.md)** — the newly-confirmed MVP scope addition; read its open-questions section carefully
6. **[Security Architecture](Security-Architecture.md)**
7. **[Architecture Review Checklist](Architecture-Review-Checklist.md)** — use this to verify everything above before Phase 3 sign-off

`Module-Design.md` is referenced throughout (1) and (3) and can be read alongside either.

## Document Index

| Document | Purpose |
|---|---|
| `System-Architecture.md` | System context, module boundaries, flows (auth, ticket, CRM, Genesys, SLA/escalation, notification, audit), background jobs, reliability, deployment, security boundaries |
| `Module-Design.md` | The 12 logical modules — responsibility, interfaces, owned data, events, dependencies, prohibited dependencies |
| `Domain-Model.md` | 19 conceptual entities — purpose, attributes, relationships, invariants, ownership, lifecycle. No SQL DDL. |
| `SLA-Architecture.md` | First Response/Resolution SLA, business calendar, pause rules, priority-change policy, escalation windows, worked examples |
| `Genesys-Integration.md` | Genesys Basic Integration design, webhook contract (conceptual), and open questions for the Genesys team |
| `Security-Architecture.md` | Authentication, authorization, data protection, webhook/upload security, logging, secrets, testing |
| `Architecture-Review-Checklist.md` | Pre-Phase-3 sign-off checklist |
| `adr/0001`–`0024` | Individual Architecture Decision Records. **ADR-0002 (.NET 8) is superseded by ADR-0023 (.NET 10)** and **ADR-0005 (authorization policies) is amended by ADR-0024 (System Administrator authorization override)** — all are kept, per this project's ADR convention below. |

### Relationship to the rest of the project documentation

| Document (outside this folder) | Purpose |
|---|---|
| `../Tiger-CS-Ticketing-Solution-Analysis.md` | Full requirements analysis; amended (§8, §15) to reflect Genesys Basic Integration moving into MVP, and (§4.1) to reflect ISSUE-024's confirmed System Administrator access decision |
| `../Tiger-CS-Ticketing-Management-Decisions.md` | Technical Decision Register — all 23 tracked items, including ISSUE-003's and ISSUE-024's resolutions |
| `../Tiger-CS-Ticketing-Executive-Decisions.md` | Meeting-ready MVP decision summary with sign-off fields |
| `../Tiger-CS-Ticketing-Architecture-Design.md` | The prior (PR #2) design pass — its 11 inline ADRs are superseded by the formal log in `adr/`; its ERD/schema/API sketch remain a useful reference alongside `Domain-Model.md` and `System-Architecture.md` |

## What Changed in This Pass

- **Genesys Basic Integration is now confirmed for MVP** by explicit management directive (this pilot's commissioning message specifies "Genesys APIs and webhooks" directly). This resolves **ISSUE-003** (the platform is Genesys) and supersedes the earlier, conditional ADR-0012 from the PR #2 ADR log. `Tiger-CS-Ticketing-Solution-Analysis.md` §8/§15 have been amended to match, so the documented MVP scope no longer contradicts this decision.
- The prior 14-ADR log (PR #2) has been **replaced** by this 22-ADR log, using an expanded template (adds Alternatives Considered as a distinct section already present, plus **Risks** and **Review Date**) and covering additional topics split out for clarity (Identity vs. authorization policies; four separate SLA/escalation ADRs instead of one combined; Outbox and idempotency split; logging split from audit).
- A full System Architecture, Module Design, Domain Model, SLA Architecture, Genesys Integration, and Security Architecture document have been added — none of which existed as standalone documents before this pass.

## What Changed Since (System Administrator Authorization Correction, 2026-08-21)

- **Management confirmed that the System Administrator role must have access to every application feature and every API endpoint**, superseding `Tiger-CS-Ticketing-Solution-Analysis.md` §4.1's exclusion of that role from every operational permission column — an exclusion the implementation had followed literally, producing `403 Forbidden` on endpoints including `POST /api/intake-records`. **ADR-0024** records the decision and the mechanism: a single central authorization override per authorization layer, so future SLA, escalation, reporting and administration policies include the role automatically rather than by amendment. It is an **authorization** override only — validation, ticket status-transition rules, closed-ticket immutability, concurrency control, database constraints, required business data and audit requirements are unchanged, and a **deactivated** administrator is still refused on every request. `Security-Architecture.md` (§2.1, §3.1), `Solution-Analysis.md` §4.1, `Tiger-CS-Ticketing-Management-Decisions.md` (ISSUE-024) and ADR-0005 (amendment note) are updated to match. No other role's permissions change, and no account gains an additional role.

## What Changed Since (Framework Upgrade, Ahead of Phase 1)

- **Management approved .NET 10 as the target framework**, superseding ADR-0002's .NET 8 selection. **ADR-0023** records the upgrade — .NET 10 (LTS), C# 14, ASP.NET Core 10, EF Core 10, ASP.NET Core Identity 10 — with every package version confirmed against a real SDK install, restore, and build in the current build environment, not assumed. `System-Architecture.md`, `Tiger-CS-Ticketing-Architecture-Design.md`, and `Tiger-CS-Ticketing-Solution-Analysis.md` are updated to say .NET 10 wherever they previously said .NET 8. ADR-0002 itself is left unedited and marked Superseded, per this project's ADR convention below.

## Remaining Open Questions

These are genuinely open — not silently resolved, not silently assumed. Anything not listed here that resembles a decision has already been approved and is traceable to an ISSUE ID.

**From the Genesys team (blocking Phase 3 implementation of the Genesys adapter — see `Genesys-Integration.md` §15 for full detail):**
1. Exact webhook/notification delivery mechanism.
2. Signature/authentication scheme for inbound webhooks.
3. Exact payload schema (field names) for conversation/interaction events.
4. Reliability of agent email/extension on every interaction-answered event.
5. Delivery guarantees (at-least-once? redelivery behavior?).
6. Whether "Genesys Basic Integration" covers voice only, or other channels Genesys might route.
7. Rate limits/API quotas relevant to capacity planning.
8. Sandbox/test environment availability within the 3-week pilot window.

**Architectural assumptions still pending confirmation (marked `[ASSUMPTION]` throughout this package — not blocking, but should be confirmed before Phase 3 locks them in):**
9. Hosting target for deployment (on-premises, Azure, or another cloud) — ADR-0022.
10. 25MB-per-file attachment size cap — ADR-0017.
11. Password policy / MFA configuration, session timeout duration, rate-limiting thresholds — `Security-Architecture.md`.
12. Whether a returning Genesys caller can be automatically linked to an existing ticket, versus this pilot's manual-linking-only scope — `Genesys-Integration.md` §6.
13. The exact holiday-date confirmation workflow between System Administrator and Customer Service/HR — `Domain-Model.md`'s `Holiday` entity.

**Still open per the Technical Decision Register (not new to this pass, listed here for completeness):**
14. ISSUE-002 (auto-ticket verification timing) — Phase 2 gate, does not affect this MVP since ticket creation stays manual even with Genesys attached.
15. ISSUE-015 (expected system scale) — Phase 2 gate.
16. ISSUE-009 (CSAT resend on reopen) — Phase 2 gate.
17. ISSUE-016 (exact retention regulation) — required before production go-live, owned by Legal/Compliance.
18. ISSUE-014 (repeat-contact definition) — Phase 3 gate.

## Explicitly Out of Scope for This MVP Architecture

Per direct instruction — none of the following appear anywhere in this package, including as an unlabeled "future-proofing" addition: **WhatsApp, Kiosk, Social Media, Customer Portal, CSAT, advanced AI features.** Extension points (e.g., swappable gateway interfaces for `IEmailGateway`, `ICrmGateway`, `IGenesysWebhookGateway`) exist so these can be added later without a redesign — but none are implemented now.

## Conventions

Each ADR follows: **Context, Decision, Alternatives Considered, Advantages, Disadvantages, Consequences, Risks, Status, Review Date.** Numbering is sequential and files are never renumbered — a later change to a recorded decision is captured as a new ADR that supersedes the old one (see ADR-0019's explicit supersession of the prior log's ADR-0012), not by editing history in place.
