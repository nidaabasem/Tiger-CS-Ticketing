# Tiger Group — Customer Service Ticketing System
## Architecture Review Checklist

| | |
|---|---|
| **Status** | Approved for Architecture Design |
| **Purpose** | Checklist for human engineering review of this architecture package, and for the pilot readiness gate before Phase 3 implementation begins |
| **Date** | 2026-08-17 |

Use this before signing off the architecture package, and again before the 3-week pilot go-live.

## Requirements Traceability

- [ ] Every ADR cites the ISSUE ID(s) or FR/BR ID(s) it implements — no ADR introduces an undocumented business rule.
- [ ] Every entity in `Domain-Model.md` traces to a Solution Analysis requirement or an explicit `[ASSUMPTION]`.
- [ ] Every module in `Module-Design.md` maps to a Solution Analysis module or an explicitly new architectural concern.
- [ ] All `[ASSUMPTION]` markers in this package are listed and forwarded for confirmation (see "Remaining Open Questions" in the README).

## Module Boundaries

- [ ] The dependency graph in `Module-Design.md` is acyclic (verified in that document's "Preventing Circular Dependencies" section).
- [ ] No module accesses another module's owned data directly, only via published interfaces or events.
- [ ] `Infrastructure` has zero dependencies on any business-capability module.
- [ ] `Audit` depends only on `Infrastructure` and event contracts, never calling back into the modules it audits.

## Security

- [ ] No customer-facing authentication endpoint exists anywhere in the design (ISSUE-021).
- [ ] Every API endpoint's required authorization policy is identifiable from `Security-Architecture.md` / `Module-Design.md`.
- [ ] Genesys webhook signature validation is designed in, even though the exact scheme is still an open question.
- [ ] Personal data (caller numbers, contact names) has an explicit logging-masking rule (`Security-Architecture.md` §11).
- [ ] Audit tables are append-only by design (no update/delete path).

## SLA Correctness

- [ ] First Response and Resolution SLAs are tracked independently (ADR-0009).
- [ ] Critical SLA is designed as never-pausing, as a fixed rule, not configurable data that could be misconfigured.
- [ ] Priority upgrade uses the earlier-of-due-dates rule; downgrade requires approval and never erases a recorded breach (ADR-0012).
- [ ] All worked examples in `SLA-Architecture.md` §16 are internally consistent with the calculation rules in §3–8.
- [ ] Escalation advancement is time-and-priority-based, not retry-count-based (ADR-0011).

## Genesys Integration

- [ ] Webhook idempotency key design is specified (`ConversationId` + event type).
- [ ] Manual fallback (Genesys unavailable) is designed and does not block any core ticketing function.
- [ ] Open questions for the Genesys team are captured in one place (`Genesys-Integration.md` §15) and are not silently assumed elsewhere in the package.
- [ ] The scope boundary (basic integration only — no outbound dialing, recording retrieval, deep automation) is stated explicitly, not implied.

## CRM Dependency

- [ ] CRM remains sole source of truth; no locally-mastered copy of unit/contact data is introduced anywhere (ADR-0006).
- [ ] The immutable snapshot (ADR-0007) is structurally distinct from the refreshable cache (ADR-0006) in `Domain-Model.md`.
- [ ] CRM-outage fallback (Intake Record + provisional tickets for Critical/High) is designed, per ISSUE-006.

## Reliability

- [ ] Every cross-boundary effect (notification, integration call) goes through the Transactional Outbox (ADR-0013) — no direct synchronous external call from a request handler is designed in.
- [ ] Idempotency keys are specified for the SLA sweep/scheduled-job overlap and for Genesys webhook redelivery (ADR-0014).
- [ ] Retry-then-dead-letter is the documented pattern for every outbound integration (Email, CRM, Genesys).

## Auditability

- [ ] `TicketStatusHistory` covers all five ticket-state dimensions, not just `TicketStatus`.
- [ ] `AuditEntry` covers administrative actions distinct from ticket-lifecycle actions.
- [ ] Correlation IDs are propagated consistently across logs, the Outbox, and both audit tables (ADR-0014/0018).

## Performance

- [ ] The SLA sweep is documented as a backstop only, not the primary detection mechanism, to avoid unnecessary load (ADR-0015).
- [ ] SignalR is documented as change-events-only, never a per-second broadcast, to avoid unnecessary connection load (ADR-0016).
- [ ] Dashboard and Reporting is read-only by design, isolating reporting load from the transactional write path.

## Testing

- [ ] The three-layer testing strategy (unit/integration/end-to-end) is documented with the specific high-risk logic it must cover (ADR-0021).
- [ ] Security-relevant logic (authorization, webhook signature validation) is explicitly called out as requiring dedicated test coverage (`Security-Architecture.md` §15).

## Deployment Readiness

- [ ] The hosting target is confirmed, or explicitly flagged as an open question blocking Phase 3 deployment planning (ADR-0022).
- [ ] Configuration (SLA policy, holiday calendar, integration endpoints) is externalized, not hardcoded.
- [ ] Database migrations are documented as an explicit, reviewed step — never automatic on startup.

## Three-Week Pilot Scope Control

- [ ] MVP scope in this package matches the instruction's explicit list (internal web app, auth/RBAC, CRM verification, manual ticket creation, classification/routing, core lifecycle, notes/attachments, basic Genesys integration, FR/Resolution SLA tracking, escalation, email acknowledgement, audit trail, basic dashboard) — nothing more, nothing less.
- [ ] WhatsApp, Kiosk, Social Media, Customer Portal, CSAT, and advanced AI features are **not** present anywhere in this architecture package, including as an unlabeled "future-proofing" addition.
- [ ] Extension points exist for future integrations (e.g., `IEmailGateway`, `ICrmGateway`, `IGenesysWebhookGateway` as swappable interfaces) without any Phase 2/3 feature being implemented now.
- [ ] Every document in this package states its status accurately (Approved for Architecture Design) and does not imply implementation has begun.
