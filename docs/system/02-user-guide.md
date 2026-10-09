# 2. User guide by module and role

> Part of the [documentation set](README.md). Describes the staff web UI (`TigerCS.Web`). Who may do what: [03](03-roles-and-permissions.md). Lifecycle rules: [04](04-ticket-lifecycle-sla-approvals-notifications.md).

## 2.1 Everyone
* **Sign in** at `/Login`; **Access Denied** appears when your role lacks a page. **Change password** under Account → Change Password. Password *reset* is performed by a System Administrator (Admin → Users).
* **Dashboard** (`/Dashboard`): ticket cards, channel breakdown and filters scoped to the departments you can see.
* **Tickets** (`/Tickets`): Queue (department, unassigned), My Tickets, Pending Interactions (customers awaiting a human after AI handoff), Closed. Open a ticket from its row.
* **Ticket Details**: status, assignment, SLA panel, activity, interactions/transcript, notes, approvals, customer history.

## 2.2 CS Agent / Call Center Agent / CS Supervisor
1. **Create a ticket** — `/NewTicket`: search the caller (CRM and PACT results appear as one card when a reliable match exists), pick a unit (unit 0 and expired contracts are not offered), then Department → Category → Request Type, priority and description. Ambiguous matches or manual customers are chosen explicitly.
2. **Work it** — take/assign, change status (Open → In Progress → Pending Customer → In Progress), add notes, transfer to a department, escalate.
3. **Record first response** — on the SLA panel, once you have answered the customer outside the Genesys transcript. AI/automated replies never satisfy First Response.
4. **Resolve / Close / Reopen** — Resolve with outcome + note, Close, or Reopen (within 7 days, reason and department required).
5. **Pending Interactions** — pick up customers whom the chatbot handed to a human.
6. **Genesys Screen Pop** — from Genesys the agent lands on the customer or ticket (staff sign‑in only; unrelated to customer verification).
7. **Customer verification** (CS Agent/Supervisor via Genesys data actions) — email OTP, then document copies by e‑mail ([11](11-verification-and-document-delivery.md)).

## 2.3 Department Employee / Department Head
See and act on tickets of your own department(s); Heads can also change status and record first response within the department.

## 2.4 CS Manager / General Manager / Chairman‑CEO
Cross‑department visibility, assignment/escalation, **Reports → Team Performance** (CS Agents and Call Center staff workload and SLA), approvals. A CS Manager cannot create tickets.

## 2.5 Collections roles
**Collections → Receivables** (apartments with due/overdue balances, companies 4 and 32, filters, payment vs timing status) and **Collections → Campaigns** (stage preview, Review CSV, Genesys CSV). Access requires the configured Collections grants ([10](10-collections-and-campaigns.md)). No message is sent from these pages.

## 2.6 System Administrator
**Admin**: Users and roles, Departments, Channels, Request Types, Workflows (versioned, draft → publish), SLA configuration and Genesys queue mappings (API). Central override applies to permission rules but never to the active‑account check. Audit history is recorded but has no UI page yet.
