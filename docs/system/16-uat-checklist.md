# 16. End‑to‑end UAT checklist and validation evidence

> Part of the [documentation set](README.md). **No item below has been run on UAT** — this session has no network path to UAT, CRM, PACT, EDSM, SMTP, TigerGroupWeb or Genesys. Do not send real customer messages, place reminder calls or collect payments while testing; use test numbers/mailboxes.

## 16.1 Local validation evidence (recorded by this review)

| Check | Result |
|---|---|
| `dotnet test src/TigerCS.slnx` on `main` `a1cba71` | 3216 passed, 0 failed |
| Same after cherry‑picking the CRM‑documents/OTP commits | 3389 passed, 0 failed |
| Same after the first review fixes (PR #78, commit `aad2876`) | 3396 passed, 0 failed |
| After SLA pause/request-type SLA, downgrade approval, read-only roles/service identity, Genesys contract + campaign tests, Tasleeh provider, migration `AddSlaPauseAndPriorityDowngrade` | **3779 passed, 0 failed** (needs `DOTNET_USE_POLLING_FILE_WATCHER=1` on hosts with a low inotify limit) |
| `dotnet ef migrations has-pending-model-changes` | "No changes" before and after the new migration |
| CI (GitHub Actions) on the updated PR | pending — see PR checks; the *DB Migration Validation* workflow (real SQL Server) was updated to expect 60 tables |
| `dotnet build` (all projects) | succeeded |
| Data action JSON | `08` was invalid after template rendering (fixed); all `successTemplate`s now render valid JSON with `null` substitutions (script in the PR description) |
| SQL Server / Hangfire / migrations applied | **not run** (SQLite and fakes only) |
| Real CRM/PACT/EDSM/SMTP/Genesys | **not run** |

## 16.2 UAT checklist (run in order; mark Pass/Fail with evidence)

| # | Journey | Precondition | Steps | Expected | Result |
|---|---|---|---|---|---|
| 1 | Login/roles | users per role | log in each role; open Admin, Reports, Collections | access matches [03](03-roles-and-permissions.md); denied → Access Denied | ☐ |
| 2 | Password change | — | Account → change password | success; old password rejected | ☐ |
| 3 | New Ticket – CRM+PACT | test customer in both | search phone; select unit | one unified card; no unit 0; expired contracts hidden in wizard | ☐ |
| 4 | Ticket 0‑unit guard | API tool | POST ticket with `CrmBuyerUnitId=0` | 4xx `CrmBuyerReferenceMismatch` | ☐ |
| 5 | Lifecycle | ticket | Open→InProgress→PendingCustomer→InProgress→Resolved→Closed | history rows with actor/time; Pending Third Party not offered | ☐ |
| 6 | Reopen | closed ticket < 7 d | CS Agent reopens with reason+department | InProgress; original closure rows retained; Dept Employee refused | ☐ |
| 7 | First response | manual ticket | press **Record first response** | timestamp set; no breach; second press refused | ☐ |
| 8 | Genesys ticket | queue mapping exists | `POST /api/genesys/tickets` via public URL, twice with same conversationId | 201 then 200 (reuse); unmapped queue → 422 | ☐ |
| 9 | Handoff | ticket | PATCH handoff (agent available / unavailable) | queue/follow‑up per [08](08-genesys-integration-guide.md) | ☐ |
| 10 | Conversation end + transcript | ticket | PATCH `ended` with transcript (senders Customer/HumanAgent/VirtualAgent/System) | transcript stored; first human response recorded only for HumanAgent | ☐ |
| 11 | Inactivity | `BackgroundJobs` on | send data action 10; wait timeout | ticket closed by System, one e‑mail | ☐ |
| 12 | Screen Pop | agent mapped, base URL set | pop with `tel:+971…` | lands on lookup with number; ticket pop signs in mapped staff only | ☐ |
| 13 | OTP | CRM buyer with e‑mail, SMTP | buyer‑lookup → otp/send → otp/verify | code mailed; Verified + session; wrong code ×5 → locked | ☐ |
| 14 | Document copy | CRM routes, `CrmDocuments` on | send‑copy with session; repeat same `Idempotency-Key` | e‑mail with attachment once; `duplicate:true`; foreign record refused | ☐ |
| 15 | Unit details | CRM `GetUnitDetails` | unit‑details with/without `unitId` | selection list then details; other customer's unit → 403 | ☐ |
| 16 | Collections summary | PACT mapping via ticket | data action 08 with `customerKey` | valid JSON; unknown key 404; source down 503 | ☐ |
| 17 | Receivables & campaigns | PACTRPT creds | open Receivables, Campaigns (each stage) | lists render; Review CSV downloads; Genesys CSV refused while unvalidated | ☐ |
| 18 | Notifications | SMTP | create/resolve/close/reopen | one e‑mail per event, no duplicates | ☐ |
| 19 | Team Performance | CS Manager | open report | CS Agents + Call Center staff listed | ☐ |
| 20a | Pause / resume | non-Critical ticket | InProgress → PendingCustomer → InProgress; read SLA panel | paused state shown while pending; Resolution due moved by paused time; Critical never pauses | ☐ |
| 20b | Downgrade approval | ticket with priority High; Dept Head user | agent requests downgrade; check ticket; Dept Head approves | unchanged while pending; after approval new priority + new SLA period; prior breach flag kept; requester cannot approve | ☐ |
| 20c | Chairman/CEO read-only | CEO user | open ticket, try status/notes/escalate/assign via UI and API | no controls; API 403; reports readable | ☐ |
| 20d | Service identity | Genesys service account listed in `Authorization:ServiceIdentity` | call `/api/dashboard`, `/api/reports/*`, `/api/admin/*` | 403; Genesys routes still work | ☐ |
| 20 | Reminders | — | **do not run** until D10 cleared | dry‑run candidates only | ☐ |
