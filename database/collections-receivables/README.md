# Collections receivables snapshot - SQL deployment (TigerCsTicketing)

Run in order on `10.10.10.117` / `TigerCsTicketing` (all idempotent, no credentials, no destructive statements):

| Script | Purpose |
| --- | --- |
| `V001__reconcile_CollectionsTowers.sql` | Reconcile the manually created `dbo.CollectionsTowers` (report only; creates an empty table only if missing; lookup index). |
| `V002__create_receivables_snapshot_tables.sql` | Run / company-state / snapshot / staging tables. |
| `V003__fn_CollectionsTowerNumber.sql` | Tower number from a unit code. |
| `V004__usp_Collections_PublishReceivablesStaging.sql` | Validate staged rows and publish (or record the failure and keep the previous snapshot). |
| `V005__usp_Collections_RefreshReceivables.sql` | `INSERT ... EXEC` the PACT procedures through `[10.10.10.94]`, per company, overlap-safe. |
| `V006__usp_Collections_GetReceivables.sql` | Local read procedures (tower, due-date window, Due/Overdue) and the tower list. |
| `tests/probe_pact_result_shape.sql` | Read-only probe of the deployed PACT procedures' result shape (run after V001-V003, before enabling the job). |
| `tests/smoke_snapshot_publish_and_read.sql` | Rolled-back smoke test of publish validation and the read procedure (development copy). |

Full design, semantics, scheduling and configuration: [`docs/Collections/Receivables-Snapshot.md`](../../docs/Collections/Receivables-Snapshot.md).
