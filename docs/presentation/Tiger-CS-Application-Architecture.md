# Tiger CS Ticketing System — Application and Solution Architecture

**Audience:** IT Manager, Department Management, Executive Sponsors
**Basis:** Approved architecture documentation and ADRs on `main` (`docs/architecture/`, `docs/design/`), Phase 1 (.NET 10) foundation scaffolding
**Status:** Internal Pilot Architecture — approved for architecture design; production go-live requires separate approval

---

## Slide 1 — Title

**Tiger CS Ticketing System**
Application and Solution Architecture

- Built on ASP.NET Core, .NET 10 (LTS)
- Internal Pilot Architecture

*Tiger Group — Confidential*

---

## Slide 2 — Business Objective

- **Centralize** all customer-service requests in a single system of record
- **Integrate** phone (Genesys), CRM, and internal departments into one workflow
- **Track** ownership, SLA due dates, escalation, and resolution for every ticket
- **Provide** auditability and management visibility across the full ticket lifecycle

---

## Slide 3 — System Context Architecture

**Internal users** (staff only — no customer login or portal exists)
- CS Agent
- Department Employee / Department Head
- Management (CS Manager, General Manager, Chairman/CEO)
- System Administrator / Reporting User

**Application boundary**
- Tiger CS Ticketing System (single modular-monolith application)

**External systems**
- CRM — source of truth for units and customers
- Genesys — call/conversation platform
- Office 365 Email — acknowledgement and alert delivery
- SQL Server — system of record
- File Storage — ticket attachments

The customer never accesses the system directly — all customer interaction is agent-mediated by phone (via Genesys) or by outbound email notification.

---

## Slide 4 — Application Architecture

Modular monolith — one deployable, clean internal boundaries:

| Layer | Role |
|---|---|
| Web UI | Staff-facing ticket queue and detail screens |
| ASP.NET Core API | All application entry points, policy-enforced |
| Application Modules | Business capability logic (below) |
| Domain | Ticket aggregate and business rules |
| Infrastructure | Data access, jobs, real-time notifications |
| Integrations | CRM, Genesys, Email, File Storage adapters |
| SQL Server | System-of-record database |

**Application modules:** Identity and Access · CRM Verification · Intake · Ticketing · SLA and Escalation · Genesys Integration · Notifications · Attachments · Audit · Reporting

---

## Slide 5 — Main Ticket Flow

Genesys / manual intake → CRM verification → ticket creation → classification → department routing → assignment → SLA monitoring → resolution → closure → reporting

**Escalation** triggers automatically whenever an SLA is breached, running in parallel with the main flow until the ticket is resolved.

---

## Slide 6 — Integration Architecture

- **CRM** is the source of truth for units and customers — never duplicated as the master record
- **Genesys** supplies call and conversation information, linked to tickets
- **Office 365** sends the customer acknowledgement email
- **File Storage** holds ticket attachments, referenced (not embedded) by the application
- **Outbox and idempotency** protect every integration from duplicate or lost processing
- **Feature flags** allow each integration to be safely activated independently, without a redeploy

---

## Slide 7 — Security Architecture

- **ASP.NET Core Identity** for staff authentication — no customer accounts exist
- **JWT authentication** on every API request
- **Role- and department-based authorization** — every action checked server-side
- **Nine approved roles**, spanning agents, department staff, management, and administration
- **Account deactivation** validated on every request, not only at login
- **Full audit trail** — append-only, no update or delete path
- **Secrets kept outside source control**; HTTPS everywhere; least-privilege access
- **No production credentials are ever stored in GitHub**

---

## Slide 8 — Deployment Architecture

Three separate environments — **Development, UAT/Pilot, Production** — each independently deployed to the **Tiger-approved hosting environment** (final hosting provider to be confirmed by Tiger IT; not assumed here).

Each environment contains:
- Reverse proxy / load balancer
- ASP.NET Core Web/API (single deployable unit)
- Background jobs (SLA checks, notification dispatch)
- SQL Server
- File storage
- Monitoring and logging
- Backup and recovery

---

## Slide 9 — Delivery Scope and Roadmap

- **Current:** .NET 10 solution foundation completed
- **In progress:** Identity and Access
- **Next:** CRM Verification and Ticket Creation
- **Then:** Workflow, SLA/Escalation, Genesys Integration, Email, UI, UAT

**Delivery model:** four-week, one-developer, AI-assisted pilot

> **Pilot** (this delivery) is a functional internal proof of the approved architecture. **Production Ready** is a separate, later milestone requiring its own scope, hardening, and go-live approval.

---

## Slide 10 — Key Risks and Management Decisions

- CRM and Genesys integration contract availability
- Schedule pressure from a single-developer delivery model
- UAT participation from CS and department staff
- SQL Server and target environment readiness
- Independent security review before pilot go-live
- **Production go-live requires separate, explicit management approval**

---

*Tiger Group — Confidential*
