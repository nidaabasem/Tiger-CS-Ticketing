# Tiger Group — CS Ticketing System: Detailed Design Documentation

**Status: Design for review — revised following a senior-.NET-solution-architect review pass, and again to record management's approved delivery decision.** This package refines the approved MVP architecture (`docs/architecture/`) to implementation-ready detail for a **4-week, 1-developer functional pilot** (revised from the original 3-week/4-person plan — see `MVP-Implementation-Backlog.md` §0): entity/column detail, API contracts, a provisional Genesys mock contract, UI wireframes, requirement traceability, an implementation backlog, a design review checklist, and a formal architecture-review findings log. **Genesys integration is deferred entirely from this pilot's scope and, whenever built, must ship behind a feature flag until Genesys confirms its sandbox, webhook schema, and authentication mechanism. No production deployment is authorized at this stage.**

**No application code, SQL DDL, EF Core migrations, controllers/services/repositories, OpenAPI-generated code, or UI implementation has been produced.** This is design documentation only. Phase 3 ("Project Foundation") remains the next, separately-authorized step.

**Read `MVP-Design-Review-Findings.md` first if you only read one document** — it lists every defect found in the senior-architecture-review pass (circular verification dependency, a missing Genesys entity, incorrect Genesys idempotency modeling, a signature-handling contradiction, a priority-downgrade self-authorization defect, an attachment-retention violation, and a backlog capacity gap), each already resolved in the documents below. **All nine findings are now resolved** — the backlog capacity gap (DR-08) was resolved by management's approved decision to run a 4-week, 1-developer pilot with reduced scope, recorded in `MVP-Implementation-Backlog.md` §0.

## Required Review Order

1. **[MVP Design Review Findings](MVP-Design-Review-Findings.md)** — start here: what the review found, what changed, and what's still open
2. **[MVP ERD](MVP-ERD.md)** — Mermaid ER diagram, relationship cardinalities, ownership, delete behavior, integrity notes
3. **[MVP Data Dictionary](MVP-Data-Dictionary.md)** — column-level type/nullability detail for every entity in (2), same section numbers
4. **[MVP API Contracts](MVP-API-Contracts.md)** — every endpoint across the 6 modules, built from (2)/(3)
5. **[Genesys Mock Contract](Genesys-Mock-Contract.md)** — the provisional, explicitly-not-official payload shape behind the webhook endpoint in (4)'s §6
6. **[MVP UI Wireframes](MVP-UI-Wireframes.md)** — 20 screens, structural specs and Mermaid flow, built against (4)'s endpoints
7. **[MVP Traceability Matrix](MVP-Traceability-Matrix.md)** — cross-references (2)–(6) back to every MVP requirement; verify this before trusting the package is complete
8. **[MVP Implementation Backlog](MVP-Implementation-Backlog.md)** — the approved 4-week, 1-developer pilot plan, built from (2)–(7); the original 3-week/4-person plan is retained in the same document as a reference appendix for a future team scale-up
9. **[MVP Design Review Checklist](MVP-Design-Review-Checklist.md)** — use this to verify (2)–(8) before Phase 3 sign-off

## Document Index

| Document | Purpose |
|---|---|
| `MVP-Design-Review-Findings.md` | Senior-architecture-review findings: severity, documents changed, resolution, remaining decision, implementation-blocking status |
| `MVP-ERD.md` | Mermaid ER diagram, design principles carried forward, relationship cardinalities/ownership/delete-behavior/integrity notes per entity (27 entity groups as of this review pass) |
| `MVP-Data-Dictionary.md` | Column-by-column type/nullability/notes for every entity, cross-referenced to the ERD's section numbers |
| `MVP-API-Contracts.md` | HTTP contracts for Authentication/Users, CRM Verification (incl. Verification Sessions), Ticketing, Notes/Attachments, SLA/Escalation (incl. Priority Downgrade Requests), Genesys, Dashboard |
| `Genesys-Mock-Contract.md` | Provider-neutral, temporary webhook payload contract — explicitly not the official Genesys schema |
| `MVP-UI-Wireframes.md` | 20 screens' structural specs (layout regions, fields, actions, states) plus a screen-flow Mermaid diagram |
| `MVP-Traceability-Matrix.md` | Requirement → Decision/Issue → ADR → Entity → Endpoint → Screen → Test mapping; gap list; Phase 2/3 exclusion confirmation |
| `MVP-Implementation-Backlog.md` | **Approved plan:** 4-week, 1-developer sequential backlog with a workload-hours-per-week table, Genesys feature-flag policy, and a Pilot-Done vs. Production-Ready distinction (no production deployment authorized). **Reference appendix:** the original 3-week/4-person backlog, critical path, and parallel workstreams, retained for a future team scale-up |
| `MVP-Design-Review-Checklist.md` | 17-category pre-Phase-3 review checklist with an open-items log |

### Relationship to the rest of the project documentation

| Document (outside this folder) | Purpose |
|---|---|
| `../Tiger-CS-Ticketing-Solution-Analysis.md` | Full requirements analysis — the FR/BR/ISSUE IDs this package's traceability matrix maps against |
| `../Tiger-CS-Ticketing-Management-Decisions.md` | Technical Decision Register |
| `../Tiger-CS-Ticketing-Executive-Decisions.md` | Meeting-ready MVP decision summary |
| `../architecture/README.md` | The approved MVP architecture package (System Architecture, 22 ADRs, Domain Model, SLA/Genesys/Security Architecture) this design package refines |

## Explicitly Out of Scope for This Design Pass

Per direct instruction, and reconfirmed in `MVP-Traceability-Matrix.md` §10 — none of the following appear anywhere in this package: **WhatsApp, Kiosk, Social Media, Customer Portal, CSAT, advanced AI features, SMS notifications, advanced/formatted reports, the full 10-metric KPI dashboard, auto-ticket creation from any digital channel.**

## Open Items Carried Forward

See `MVP-Design-Review-Findings.md`'s "Remaining decision or dependency" column, `MVP-Traceability-Matrix.md` §9 (requirement-coverage gaps), and `MVP-Design-Review-Checklist.md`'s "Summary of Open Items Found During This Review" for the full list. Nothing there is silently resolved by this index — all three remain the authoritative open-items record. **DR-08 (backlog capacity) is no longer an open item** — it was resolved by management's approved decision (4-week, 1-developer pilot; Genesys feature-flagged and deferred; mock validation not production-ready; no production deployment authorized), recorded in `MVP-Implementation-Backlog.md` §0 and `MVP-Design-Review-Findings.md`.
