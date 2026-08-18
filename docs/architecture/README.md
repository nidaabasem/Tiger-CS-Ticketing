# Tiger Group — CS Ticketing System: Architecture Documentation

This directory contains the formal Architecture Decision Record (ADR) log for the Tiger Group Customer Service Ticketing System, produced after management approved the MVP decisions (Status: **Approved for Architecture Design**).

## How this relates to the other project documents

| Document | Purpose |
|---|---|
| `../Tiger-CS-Ticketing-Solution-Analysis.md` | Full requirements analysis, functional/non-functional requirements, business rules, SLA rules, integrations, gap analysis, phased implementation plan |
| `../Tiger-CS-Ticketing-Management-Decisions.md` | Technical Decision Register — every open decision with options, trade-offs, recommendations, and the Final Decision Sign-Off |
| `../Tiger-CS-Ticketing-Executive-Decisions.md` | Meeting-ready, MVP-only decision summary with approval/signature fields |
| `../Tiger-CS-Ticketing-Architecture-Design.md` | Phase 2 design deliverable — ERD, module dependency diagram, full database schema design, and API contract sketch |
| **`adr/`** (this directory) | Formal, one-decision-per-file Architecture Decision Records, each tracing back to an approved decision above |

The ADRs in this log do not introduce new business decisions — each one implements, or formalizes the technical shape of, a decision already approved in the Technical Decision Register or the Executive Decisions document. Where an ADR's Context/Consequences notes a scope question that has not yet been reconciled with an existing document (see ADR-0012), that is flagged explicitly rather than silently resolved.

## Scope of this log

**This is design documentation only.** No application code, ERD regeneration, SQL schema changes, EF Core migrations, API implementation, or project scaffolding has been produced as part of this ADR set. That remains Phase 3 ("Project Foundation") and later phases of the implementation plan.

## ADR Index

| # | Title | Status |
|---|---|---|
| [0001](adr/0001-modular-monolith-architecture.md) | Modular Monolith Architecture | Accepted |
| [0002](adr/0002-aspnet-core-dotnet-8.md) | ASP.NET Core on .NET 8 | Accepted |
| [0003](adr/0003-sql-server-and-entity-framework-core.md) | SQL Server and Entity Framework Core | Accepted |
| [0004](adr/0004-aspnet-core-identity-and-authorization-policies.md) | ASP.NET Core Identity and Policy-Based Authorization | Accepted |
| [0005](adr/0005-crm-as-source-of-truth.md) | CRM as the Source of Truth | Accepted |
| [0006](adr/0006-ticket-lifecycle-design.md) | Ticket Lifecycle Design (Five Independent Dimensions) | Accepted |
| [0007](adr/0007-sla-and-escalation-architecture.md) | SLA and Escalation Architecture | Accepted |
| [0008](adr/0008-transactional-outbox-and-idempotency.md) | Transactional Outbox and Idempotency | Accepted |
| [0009](adr/0009-hangfire-background-jobs.md) | Hangfire Background Jobs | Accepted |
| [0010](adr/0010-signalr-real-time-updates.md) | SignalR Real-Time Updates | Accepted |
| [0011](adr/0011-attachment-storage.md) | Attachment Storage | Accepted |
| [0012](adr/0012-genesys-basic-integration-within-mvp.md) | Genesys/Geyness Basic Integration within MVP | **Accepted, conditional** — see scope note in the ADR |
| [0013](adr/0013-logging-monitoring-and-audit-trail.md) | Logging, Monitoring, and Audit Trail | Accepted |
| [0014](adr/0014-testing-strategy.md) | Testing Strategy | Accepted |

## A note on ADR-0012

ADR-0012 records a management-requested addition — a basic Genesys/Geyness call-center integration within MVP — that **changes the MVP scope boundary** previously documented and approved in `Tiger-CS-Ticketing-Solution-Analysis.md` §15 and §8, where this integration was placed in Phase 2, gated on the still-unresolved vendor/platform identity question (ISSUE-003). The ADR flags this explicitly rather than silently overriding the earlier scope decision. A follow-up amendment to the Solution Analysis's MVP scope and integration tier is recommended so the documents no longer disagree with each other; that amendment is not made automatically by this ADR log.

## Conventions used in this log

Each ADR follows the same structure: **Context**, **Decision**, **Alternatives Considered**, **Advantages**, **Disadvantages**, **Consequences**, and a **Status** field (Accepted / Proposed / Deprecated / Superseded). Numbering is sequential and files are never renumbered — a later change to a recorded decision is captured as a new ADR that supersedes the old one, not by editing history in place.
