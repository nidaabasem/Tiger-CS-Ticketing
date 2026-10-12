"""Campaign read latency, idle and while a refresh publishes (synthetic data, local SQL Server). Usage: python3 bench_campaign.py [label]
Runs the SQL engine's stored procedure in a tight loop on one connection - the same statement the application issues - and reports percentiles
and the reader's lock waits. For the full service call (engine + mapping) use the .NET timing test (TIGERCS_PERF_TIMINGS=1)."""
import sys, time, threading, statistics
sys.path.insert(0, '.')
from sql import conn
from refresh import refresh

READ = ("EXEC dbo.usp_Collections_GetCampaignUnits @FromDate='20260101',@ToDate='20261009',@MinAmount=100,@StageFrom=NULL,"
        "@StageToExclusive='20260909',@Threshold=0,@NormVersion=1,@Take=25")
label = sys.argv[1] if len(sys.argv) > 1 else 'campaign'

def pct(xs, p):
    xs = sorted(xs); return xs[min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))]

def loop(stop, out):
    c = conn('TigerCsTicketing'); cur = c.cursor()
    cur.execute("SELECT @@SPID"); out['spid'] = cur.fetchone()[0]
    while not stop.is_set():
        s = time.time()
        try:
            cur.execute(READ)
            while True:
                if cur.description: cur.fetchall()
                if not cur.nextset(): break
        except Exception as e:
            out['err'].append(str(e)[:80]); continue
        out['lat'].append((time.time() - s) * 1000)
    cur.execute("SELECT ISNULL(SUM(wait_time_ms),0), ISNULL(SUM(waiting_tasks_count),0), ISNULL(MAX(max_wait_time_ms),0) FROM sys.dm_exec_session_wait_stats WHERE session_id=@@SPID AND wait_type LIKE 'LCK[_]M%'")
    out['lck'] = cur.fetchone()

def report(tag, out):
    d = out['lat']
    print(f"{label} {tag}: n={len(d)} p50={pct(d,50):.0f}ms p95={pct(d,95):.0f}ms max={max(d):.0f}ms; reader LOCK waits total={out['lck'][0]}ms count={out['lck'][1]} longest={out['lck'][2]}ms; errors={out['err']}")

# idle
stop = threading.Event(); idle = {'lat': [], 'err': []}
t = threading.Thread(target=loop, args=(stop, idle)); t.start(); time.sleep(40); stop.set(); t.join(); report('idle', idle)
# during a refresh
stop = threading.Event(); busy = {'lat': [], 'err': []}
t = threading.Thread(target=loop, args=(stop, busy)); t.start(); time.sleep(2)
secs, _ = refresh(); time.sleep(1); stop.set(); t.join()
print(f"refresh took {secs}s"); report('during refresh', busy)
