# MVP Web Foundation and Core Ticketing UI — screenshots

Captured from the real `TigerCS.Web` application running under Kestrel, in a
headless Chromium at a 2× device pixel ratio.

## What is behind these screens

**The data is not real, and it is not production data.** No SQL Server or
Tiger CRM instance exists in the build environment, so `TigerCS.Api` was
replaced for the capture by a small local stub that answers the same routes
with the same DTO shapes. That stub lives outside this repository and ships
with nothing.

What this means when reading the screenshots:

- Every layout, style, badge, state and interaction is the real application.
- The ticket numbers, names, summaries and timestamps are illustrative. They
  are plausible service-desk content, not fabricated business metrics — there
  are no KPI tiles, no counts presented as performance, and no invented
  reporting anywhere in this increment.
- The signed-in user is a **CS Supervisor**, which is why the action bar on
  `05-details` shows Assign, Change status and Close but **not** Transfer
  (CS Manager only) and **not** Resolve (Department Employee/Head only). That
  is the server's own permission matrix, mirrored in the UI.

## Files

| File | Screen |
|---|---|
| `01-login.png` | Login |
| `02-login-invalid.png` | Login — invalid credentials |
| `03-queue.png` | Ticket queue |
| `04-queue-pending-crm.png` | Ticket queue — filtered to Pending CRM |
| `05-details.png` | Ticket details — breached SLA, escalated to level 2 |
| `06-details-closed.png` | Ticket details — closed, read-only |
| `07-details-assign-dialog.png` | Ticket details — assignment dialog |
| `08-wizard-1-intake.png` | New ticket — step 1, intake |
| `09-wizard-2-unit.png` | New ticket — step 2, CRM unit lookup |
| `10-wizard-3-contact.png` | New ticket — step 3, contact confirmation |
| `11-wizard-4-review.png` | New ticket — step 4, classify and submit |
| `12-wizard-5-confirmation.png` | New ticket — confirmation with the real ticket number |
| `13-queue-tablet.png` | Ticket queue at 834 px |
| `14-details-tablet.png` | Ticket details at 834 px |
| `15-login-tablet.png` | Login at 834 px |

## The tiger artwork

The approved photographic reference is not in this repository. The login
screen therefore renders a drawn, abstract stripe composition rather than an
invented stand-in photograph. Dropping the real asset in needs no code change:
place it at `src/TigerCS.Web/wwwroot/img/login-tiger.jpg` and set
`--login-photo` on `.login__art` in `tiger.css`; it layers over the drawn
composition and nothing else moves.
