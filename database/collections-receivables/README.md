# Collections receivables snapshot - SQL deployment (TigerCsTicketing)

Run in order on `10.10.10.117` / `TigerCsTicketing` (all idempotent, no credentials, no destructive statements):

| Script | Purpose |
| --- | --- |
| `V001__reconcile_CollectionsTowers.sql` | Reconcile the manually created `dbo.CollectionsTowers` (report only; creates an empty table only if missing; lookup index). |
| `V002__create_receivables_snapshot_tables.sql` | Run / company-state / snapshot / staging tables, the campaign engine's unit tables, narrow campaign index and contact dictionary. |
| `V003__fn_CollectionsTowerNumber.sql` | Tower number from a unit code. |
| `V004__usp_Collections_PublishReceivablesStaging.sql` | Validate staged rows and publish (or record the failure and keep the previous snapshot); also prepares each run for the campaign engine (unit keys, unit tables, contact registry). |
| `V005__usp_Collections_RefreshReceivables.sql` | `INSERT ... EXEC` the PACT procedures through `[10.10.10.94]`, per company, overlap-safe. |
| `V006__usp_Collections_GetReceivables.sql` | Local read procedures (tower, due-date window, Due/Overdue) and the tower list; cancelled units (`*` in the unit code) are excluded. |
| `V007__usp_Collections_GetCampaignUnits.sql` | Campaigns preview/export in SQL (filtering, per-unit aggregation, stage rule, review flags, search, totals, paging) + the contact-normalisation procedures. **Day-based revision:** optional `@Today` / `@MinTotal` parameters (per-unit DueAmount / OverdueAmount, nothing Due or Overdue = not listed, Minimum Total = unit Due + Overdue strictly greater), cancelled units (`*` in the unit code) and blank / `0` unit codes are excluded before aggregation, search also matches tower. Safe to run before the application update (new parameters are optional). |
| `V008__usp_Collections_GetInstalmentUnitsPage.sql` | Receivables unit table: the instalment filters, grouped per unit in SQL before paging; classifies Overdue / Due / Upcoming by the DAY (`@ClassifyByDay`, today in Dubai), optional `@StatusFilter`, never lists UnitID 0 or unit code `0`, search also matches tower; `@MinTotal` = Minimum Total on the unit's summed remaining amount (strictly greater); a `*` in the unit code (cancelled apartment) is excluded; the `upcoming` status was removed. **Re-run this script before deploying the application version that sends those parameters.** Procedure only; no table changes. |
| `V009__usp_Collections_GetInstalmentMonths.sql` | Receivables month overview: per due-date month overdue count and remaining amount over the whole filtered set (before paging, no month selection). Procedure only. Also excludes cancelled units (`*`). |
| `tests/local-sqlserver/04_receivables_page_cases.sql` | Synthetic cases (tenants VER-01..07) for the Receivables / Campaigns day-based tests (`RealSqlReceivablesPageTests`); development servers only. |
| `tests/probe_pact_result_shape.sql` | Read-only probe of the deployed PACT procedures' result shape (run after V001-V003, before enabling the job). |
| `tests/smoke_snapshot_publish_and_read.sql` | Rolled-back smoke test of publish validation and the read procedure (development copy). |

Full design, semantics, scheduling and configuration: [`docs/Collections/Receivables-Snapshot.md`](../../docs/Collections/Receivables-Snapshot.md).
