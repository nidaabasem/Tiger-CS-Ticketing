# Local SQL Server harness (synthetic data - not PACT)

Builds a **throw-away local/dev SQL Server 2022** that the real-SQL tests (`src/TigerCS.Tests/Collections/Rcv/RcvRealSqlTests.cs`, `RcvSchedulingTests`, `RcvFullVolumeTimings`) run against: databases `TigerCsTicketing` and
`PACTRPT`, a loopback linked server named `[10.10.10.94]`, a synthetic `p4AccountReceivables` (13-column layout 1) and `p32AccountReceivables` (12-column layout 3), and a tower list.
It never contacts PACT and must not be pointed at a production server. (The previous pipeline's harness scripts are in `../../legacy/tests/local-sqlserver`.)

```
export LOCALSQL_PASSWORD='<sa password of the throw-away instance>'     # host/user: LOCALSQL_HOST / LOCALSQL_USER   (python3 -m pip install pymssql)
python3 sql.py 00_setup.sql                                  # databases + loopback linked server
python3 sql.py 01_fake_pact.sql PACTRPT                      # synthetic p4/p32AccountReceivables (FakeConfig: volume, delay, shape; FakeEdgeRows: hand-written rows)
python3 sql.py 02_towers.sql TigerCsTicketing                # seed towers
# deploy ../../V001 V003 V010 V011 and ../../R001..R004 with:  python3 sql.py <file> TigerCsTicketing
export TIGERCS_PERF_SQL='Server=localhost;Database=TigerCsTicketing;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False'
dotnet test src/TigerCS.Tests --filter "FullyQualifiedName~Rcv"        # without TIGERCS_PERF_SQL these tests return immediately
```
`UPDATE PACTRPT.dbo.FakeConfig SET Shape = 3 WHERE CompanyId = 32` (Sharjah's layout) is set by the tests themselves. Full-volume timings: set `TIGERCS_PERF_TIMINGS=<output file>` and point `TIGERCS_PERF_SQL` at a database that
already holds a loaded full-size snapshot (`FakeConfig` 25000 x 12 and 12000 x 12 tenants x instalments).
