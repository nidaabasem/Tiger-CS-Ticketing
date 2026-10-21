# Collections: SQL connection stability (review refresh and receivables refresh)

## Symptoms
* Review refresh: `SqlException` transport error **10060** in `ReviewStore.AddRecordsAsync` (`SaveChangesAsync`) after "6,077 records from 4,916 source rows".
* Receivables refresh, company 4: SQL error **-1**.

Both are *transport* errors (the TCP link to the SQL Server dropped or timed out), not SQL logic errors.

## Root cause (from the code; the production logs and SQL Server were not available to confirm)
1. **Review records were written as one 2,000-row `SaveChanges` with no retry and no transaction boundary of its own.** About 50 insert statement groups travelled over one connection in
   one implicit transaction; a single network stall (10060 = TCP timeout) failed the whole refresh, and a connection that had sat idle in the pool during the multi-minute PACT read could
   be dead on first use. A failed save was also reported as "The financial source could not be read", which pointed at the wrong component.
2. **The receivables refresh holds ONE connection idle for the whole PACT fetch.** `usp_CollectionsRcv_Refresh` fetches from PACT *inside* the procedure, so the client waits on a silent
   TCP session for minutes. A firewall / NAT / load balancer that drops idle sessions then surfaces as -1 / 10054 / 10060 although the server session may still be finishing. The
   application lock in SQL and `usp_CollectionsRcv_ReapDeadRuns` keep this safe (the previous snapshot is kept; the next run or status read closes the dead attempt).
3. Concurrency is **not** the cause: a company never refreshes twice at once (in-process gate + SQL application lock), only one review run can be active (unique index), and Hangfire
   retries for the refresh jobs are intentionally off. Startup (`RefreshOnStartup`) plus cron can overlap, but the lock turns the second call into `AlreadyRunning`.

## Changes
* **Bounded batches:** `Collections:Review:PersistBatchSize` (default 500, 1-2000), each batch its own transaction.
* **Idempotent retry only where it is safe:** a batch is written as *delete this run's rows with these keys, then insert them* in one transaction, so repeating it after an ambiguous
  commit (committed but the answer was lost) converges on exactly the batch, never duplicating (the unique run+key index would refuse that anyway) or skipping. Publishing and failing a
  run are set-based and repeatable too. Up to `Collections:Review:PersistMaxAttempts` (default 3) attempts, 2 s / 4 s apart, on a fresh connection (the dead pooled connection is
  cleared). Only connection-level errors are retried (-1, -2, 53, 64, 121, 233, 258, 1205, 10053, 10054, 10060, 10061, failover/throttling); constraint and logic errors never are.
* **Failure reporting:** after the budget the run is `Failed` with "the database connection was lost or timed out after N attempt(s) (SQL error X). Nothing was published; the previous
  review data is still in use", partial records are removed, and the Campaigns page shows it with Retry. A failed *progress* write no longer fails a refresh.
* **Receivables refresh:** a transport error now reports "The connection to SQL Server was lost while the refresh was running (SQL error -1 ...)" and the log carries the error class,
  state, client connection id, server and elapsed time, so the dropped session can be found in the SQL Server logs and on the network path. The refresh is *not* retried automatically.

## What to check on the network / SQL side
* Idle-session timeout of the firewall / NAT between the API host and `10.10.10.117` (compare with the elapsed time in the new log line); raise it, or enable TCP keep-alive on the
  API host (Linux: `net.ipv4.tcp_keepalive_time` below the device timeout; Windows: `KeepAliveTime`).
* SQL Server error log / extended events around the failure time for the `clientConnectionId` logged.
* Connection string: `Connect Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=10` helps connection establishment and idle connections, not a command that is already running.

## CRM owner refresh: reading `CollectionsCrmOwnerState.LastError`
A failed CRM owner refresh used to save only "The CRM owners could not be stored." and log the exception type. It now saves a safe, specific message and logs the connection details:

* **Saved (`LastError`, also shown on the pages):** `The CRM owners could not be stored: <stage> (<procedure>) failed with SQL error <n> (class <c>, state <s>): <fixed description>. The previous CRM data was kept.`
  Stages: opening the connection, staging chunk N of M (`usp_Collections_StoreCrmUnitOwners`), publishing the run (`usp_Collections_PublishCrmUnitOwners`), recording the failure.
  The description is a fixed text per SQL number (for example 10060 / -1 = connection lost, 50040 = staged row count mismatch, 2628 = a value too long, 208 = object not found). It never copies the
  database's own message, which can contain a customer value, and never contains a connection string, password or CRM data.
* **Logged (API log, warning `CRM owners could not be stored: ...`):** stage, procedure, SQL number / class / state / line, exception type, server, database, client connection id, row count and elapsed ms.
* A failure that cannot even be recorded is logged the same way instead of being swallowed.

No retry, lock or SQL change was added; this only makes the next failure diagnosable. `CollectionsCrmOwnerRuns` does not exist by design (state is `CollectionsCrmOwnerState` + `CollectionsCrmUnitOwner.RunId`).

## "An exception has been raised that is likely due to a transient failure" on the Approved Financial Snapshot
**Cause.** EF Core's default SQL Server strategy does not retry, and when a *write* (`ExecuteUpdate`, `SaveChanges`, a transaction) hits a transient SQL error it re-throws it as
`InvalidOperationException("An exception has been raised that is likely due to a transient failure. Consider enabling ... EnableRetryOnFailure ...")` with the real `SqlException` as `InnerException`.
The review refresh writes its progress with `ExecuteUpdate` (at 5 %, 45 %, 60 % and after every batch). The progress guard only caught `DbException`, so that wrapped exception was not
recognised, fell to the catch-all, and was saved as the run's error: `InvalidOperationException: An exception has been raised ...`. The record-batch persistence added earlier was not the
failing step (reads raise the raw `SqlException`, which is why they never showed this text).

**Fix (targeted, not global).**
* Progress, publish, fail and the run reads are idempotent, so they now go through the bounded retry (`PersistMaxAttempts`, fresh connection each time). A progress write that still fails can never fail the refresh.
* The Queued -> Running claim is a compare-and-set and is deliberately **not** retried (an ambiguous first attempt would make a retry report "already claimed" and abandon a Running run). A transient failure there fails the run immediately with a clear message.
* The retry classification now includes every number EF itself treats as transient (adds 10928, 10929, 10936, 41301, 41302, 41305, 41325, 41839) and looks through EF's wrapper to the inner `SqlException`.
* The failure text names the phase (`Failed while Saving records: ...`) and never the bare EF wrapper message. Failure writes can no longer crash the job.

**Why not `EnableRetryOnFailure` globally.** The single `UseSqlServer(...)` registration serves the whole application, and about 40 code paths (tickets, SLA, Genesys, approvals) open their own
transactions through `TicketingUnitOfWork` (`Database.BeginTransactionAsync`). With a retrying strategy EF refuses user-initiated transactions unless each is wrapped in
`CreateExecutionStrategy().Execute(...)`, so enabling it globally would break all of them, and blind retries around non-idempotent writes (ticket creation, outbox, dispatch) could duplicate work.
A retry is only added where the write converges on the same state when repeated.

**Reading the next failure.** The failed run's `ProgressPercent` (kept after failure) shows where it stopped: 1 = start, 5 = reading the source, 45 = validating, 60+ = saving records.
