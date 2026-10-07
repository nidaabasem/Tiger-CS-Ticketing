# Team Performance Report (CS Manager)

The Team Performance report (`/Reports/TeamPerformance`) gives a CS Manager
one row per agent with four counts, a totals row, and — behind every count
— the list of tickets it was computed from. Every number comes from one
read, `GET /api/reports/team-performance`, computed in the database by
`TeamPerformanceQueryRepository`; every list from
`GET /api/reports/team-performance/records`, produced by the very same
predicate the count used, so a number and its list always agree.

## Who may open it

- Api: `ReportsController` carries `PolicyNames.CsManagerOrGeneralManager`
  — CS Manager, General Manager, Chairman/CEO — and System Administrator
  passes through the ADR-0024 override. Every other role receives 403.
- Web: the `/Reports` folder is gated by `ReportsPolicy` (the same roles),
  and the "Team Performance" nav item is shown to those roles only. That is
  presentation; the Api is the enforcement point. A 403 from the Api is
  rendered as a "No permission" state, never as an empty table.
- Nothing is scoped by the caller's departments: the roles that may open
  the report already see every department.

## Who is listed

- Every **active** employee (`Employees.DeactivatedAtUtc IS NULL`) holding
  the **CS Agent** role in Identity (`AspNetUserRoles`). The role set is the
  single constant `TeamPerformanceAppService.EligibleRoles`, so adding a role
  (e.g. CS Supervisor) is a one-line change.
- There is no "Call Center Agent" role. Per
  `docs/Genesys/Genesys-Cloud-Configuration.md` §4, call-centre agents are
  CS Agents who belong to the **Call Center** department (code `CC`,
  `WorkflowReferenceData.CallCenterCode`). The report derives an **agent
  type** per employee from department membership:

  | Agent type | Rule |
  | --- | --- |
  | Call Center Agent | Any membership is the department whose code is `CC`. |
  | CS Agent | Otherwise. |

- **One row per employee**, however many roles or departments they hold.
  Departments are shown comma-joined, primary first.
- Employees with **no activity** in the period are included with zeros.
- Rows are sorted by display name.

## Filters

All optional, kept in the page URL (a GET form):

| Parameter | Meaning |
| --- | --- |
| `dateFrom`, `dateTo` | UTC calendar days, inclusive. Default: the last 30 days, today included (the Dashboard's `ResolvePeriod`). A reversed range is swapped. |
| `employeeId` | One eligible employee. An employee who is not on the report yields no rows. |
| `agentType` | `CS Agent` or `Call Center Agent` (case-insensitive). An unknown value is ignored. |
| `departmentId` | Employees who are members (primary or not) of that department. |

The pickers always offer the whole eligible team, the departments any of
them belongs to, and the two agent types — not only what the current
filters left.

## Metric definitions

| Metric | Definition |
| --- | --- |
| **Currently Assigned** | Current state, **not period-bound**: tickets whose `CurrentOwnerEmployeeId` is the employee and whose `TicketStatus` is not `Closed`. |
| **Tickets Worked** | **Distinct ticket ids** on which the employee appears as the actor of at least one recorded action within the period: a `TicketAssignment` assigned to them (`AssignedAtUtc` in period); a `TicketStatusHistory` row with `ActorEmployeeId` = them; a `TicketNote` they authored; a `TicketResolution` with `ResolvingEmployeeId` = them; a `TicketWorkflowEvent` with `ActorEmployeeId` = them; or a `TicketAgentHandoff` assigned to them whose `AssignedAtUtc` or `CompletedAtUtc` is in the period. Measured from history — **not** from current ownership: a ticket the employee holds now but did nothing on in the period does not count. |
| **Completed Follow-ups** | `TicketAgentHandoff` rows with `Status = Completed`, `AssignedEmployeeId` = the employee and `CompletedAtUtc` in the period. Counts **work items, not tickets**: a ticket with two completed follow-ups counts twice. |
| **SLA Breaches** | **Distinct tickets** whose breach was recorded in the period — a `TicketStatusHistory` row with `Dimension = SlaState (4)` and `NewValue = Breached (4)`, `OccurredAtUtc` in period — attributed to the employee who **held the ticket at breach time**: the latest `TicketAssignment` for that ticket with `AssignedAtUtc <= OccurredAtUtc`. Each breach is attributed to at most one employee; a breach on a ticket nobody held is attributed to nobody; two breach rows on one ticket (first response, then resolution) count once. The current owner is irrelevant. |

"In the period" means `OccurredAtUtc >= dateFrom 00:00 UTC` and
`< dateTo + 1 day 00:00 UTC`.

### Totals row

Every figure is the sum of the rows shown (after filters). Tickets Worked
and SLA Breaches are summed per employee, so a ticket two agents both
worked counts once for each of them — the total is the team's effort, not a
ticket count.

## Records behind a count

`GET /api/reports/team-performance/records?employeeId=&metric=&dateFrom=&dateTo=`
with `metric` one of `CurrentlyAssigned`, `TicketsWorked`,
`CompletedFollowUps`, `SlaBreaches`. The period is resolved exactly as on the
report. Each record carries the ticket id, number, summary, status,
priority, current department name and creation time, plus:

- `breachedAtUtc` for SLA Breaches (the earliest breach in the period for
  that ticket);
- `completedAtUtc` for Completed Follow-ups (one row per follow-up).

Outcomes: `400` for an unknown metric, `404` for an employee who is not on
the report (expressed as `TeamPerformanceRecordsOutcome`, never an
exception). On the page the records render as a panel under the report
(`?employeeId=..&metric=..&dateFrom=..&dateTo=..`), with every row linking to
`/Tickets/{id}`; the report above it is narrowed to that employee, with a
"Show all agents" link back.

## Where it lives

| Layer | Files |
| --- | --- |
| Application | `Modules/Reporting/Dto/TeamPerformanceDtos.cs`, `Modules/Reporting/Abstractions/ITeamPerformanceQueryRepository.cs`, `Modules/Reporting/Services/TeamPerformanceAppService.cs` |
| Infrastructure | `Modules/Reporting/Repositories/TeamPerformanceQueryRepository.cs` |
| Api | `Controllers/ReportsController.cs` (`api/reports/team-performance`, `…/records`) |
| Web | `Pages/Reports/TeamPerformance.cshtml(.cs)`, `Services/Api/ReportsApiClient.cs`, `Services/Auth/ReportsPolicy.cs`, nav item in `Pages/Shared/_Nav.cshtml` |
| Tests | `Tests/Reporting/TeamPerformanceReportTests.cs` (SQLite, real SQL), `Tests/Reporting/Integration/TeamPerformanceEndpointsTests.cs` (authorization), `Tests/Web/TeamPerformanceRenderTests.cs` (Razor render) |

The report is read-only: no state changes, so no audit entry is written.
