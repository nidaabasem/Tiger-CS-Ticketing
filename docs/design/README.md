# Tiger Group — CS Ticketing System: Detailed Design Documentation

**Status: Design for review.** This package refines the approved MVP architecture (`docs/architecture/`) to implementation-ready detail for the 3-week internal pilot: entity/column detail, API contracts, a provisional Genesys mock contract, UI wireframes, requirement traceability, an implementation backlog, and a design review checklist.

**No application code, SQL DDL, EF Core migrations, controllers/services/repositories, OpenAPI-generated code, or UI implementation has been produced.** This is design documentation only. Phase 3 ("Project Foundation") remains the next, separately-authorized step.

## Required Review Order

1. **[MVP ERD](MVP-ERD.md)** — start here: Mermaid ER diagram, relationship cardinalities, ownership, delete behavior, integrity notes
2. **[MVP Data Dictionary](MVP-Data-Dictionary.md)** — column-level type/nullability detail for every entity in (1), same section numbers
3. **[MVP API Contracts](MVP-API-Contracts.md)** — every endpoint across the 6 modules, built from (1)/(2)
4. **[Genesys Mock Contract](Genesys-Mock-Contract.md)** — the provisional, explicitly-not-official payload shape behind the webhook endpoint in (3)'s §6
5. **[MVP UI Wireframes](MVP-UI-Wireframes.md)** — 20 screens, structural specs and Mermaid flow, built against (3)'s endpoints
6. **[MVP Traceability Matrix](MVP-Traceability-Matrix.md)** — cross-references (1)–(5) back to every MVP requirement; verify this before trusting the package is complete
7. **[MVP Implementation Backlog](MVP-Implementation-Backlog.md)** — the 3-week plan built from (1)–(6)
8. **[MVP Design Review Checklist](MVP-Design-Review-Checklist.md)** — use this to verify (1)–(7) before Phase 3 sign-off

## Document Index

| Document | Purpose |
|---|---|
| `MVP-ERD.md` | Mermaid ER diagram, design principles carried forward, relationship cardinalities/ownership/delete-behavior/integrity notes per entity |
| `MVP-Data-Dictionary.md` | Column-by-column type/nullability/notes for every entity, cross-referenced to the ERD's section numbers |
| `MVP-API-Contracts.md` | HTTP contracts for Authentication/Users, CRM Verification, Ticketing, Notes/Attachments, SLA/Escalation, Genesys, Dashboard |
| `Genesys-Mock-Contract.md` | Provider-neutral, temporary webhook payload contract — explicitly not the official Genesys schema |
| `MVP-UI-Wireframes.md` | 20 screens' structural specs (layout regions, fields, actions, states) plus a screen-flow Mermaid diagram |
| `MVP-Traceability-Matrix.md` | Requirement → Decision/Issue → ADR → Entity → Endpoint → Screen → Test mapping; gap list; Phase 2/3 exclusion confirmation |
| `MVP-Implementation-Backlog.md` | Week 1/2/3 backlog, critical path, parallel workstreams, capacity assumption, Pilot-Done vs. Production-Ready distinction |
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

See `MVP-Traceability-Matrix.md` §9 (requirement-coverage gaps) and `MVP-Design-Review-Checklist.md`'s "Summary of Open Items Found During This Review" for the full list. Nothing there is silently resolved by this index — both remain the authoritative open-items record.
