# Local SQL Server harness (synthetic data - not PACT)

Reproduces the SQL-level checks behind `docs/Collections/Receivables-Snapshot.md` (section "Performance and verification") on a **local/dev SQL Server 2022**:
the real V001-V006 scripts, the real refresh procedure (`INSERT ... EXEC` through a loopback linked server named `[10.10.10.94]`), synthetic PACT output,
lock-contention and latency measurements. It never contacts PACT and must not be pointed at a production server.

```
export LOCALSQL_PASSWORD='<sa password of the throw-away instance>'     # host/user: LOCALSQL_HOST / LOCALSQL_USER
python3 sql.py 00_setup.sql                                  # databases + loopback linked server
python3 sql.py 01_fake_pact.sql PACTRPT                      # synthetic p4/p32AccountReceivables (+V2 companion shape)
python3 sql.py 02_towers.sql TigerCsTicketing                # seed towers (119 deliberately missing)
# deploy ../../V001..V006 (sql.py <file> TigerCsTicketing), then:
python3 refresh.py                                           # one refresh; add ", @ProcedureSuffix=N'V2'" for the companion shape
python3 bench_inst.py                                        # instalment-page procedure timings
python3 bench_block2.py "EXEC dbo.usp_Collections_GetInstalmentsPage @FromDate='20260101',@ToDate='20261031',@AsOfDate='20261001',@MinAmount=100,@PaymentFilter='outstanding'" label
```
`bench_block2.py` runs a reader in a loop while a refresh publishes and reports reader latency, the reader's **lock waits** (`sys.dm_exec_session_wait_stats`) and errors
(deadlocks). The .NET real-SQL tests (`RealSqlSnapshotTests`) run against the same database when `TIGERCS_PERF_SQL` holds its connection string.
