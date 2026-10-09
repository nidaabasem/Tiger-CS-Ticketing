import os
import sys,time,threading,statistics; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sql import conn
from refresh import refresh
READ=sys.argv[1]; label=sys.argv[2] if len(sys.argv)>2 else ''
lat=[]; stop=False; t0=time.time(); err=[]; sid=[None]
def reader():
    c=conn('TigerCsTicketing'); cur=c.cursor(); cur.execute("SELECT @@SPID"); sid[0]=cur.fetchone()[0]
    while not stop:
        s=time.time()
        try:
            cur.execute(READ)
            while True:
                if cur.description: cur.fetchall()
                if not cur.nextset(): break
        except Exception as e:
            err.append(str(e)[:80]); continue
        lat.append(time.time()-s)
    cur.execute("SELECT ISNULL(SUM(wait_time_ms),0), ISNULL(SUM(waiting_tasks_count),0), ISNULL(MAX(max_wait_time_ms),0) FROM sys.dm_exec_session_wait_stats WHERE session_id=@@SPID AND wait_type LIKE 'LCK[_]M%'")
    lat.append(('lck',cur.fetchone()))
th=threading.Thread(target=reader); th.start(); time.sleep(2)
rs,out=refresh(); time.sleep(1); stop=True; th.join()
lck=[x for x in lat if isinstance(x,tuple)][0][1]; ds=[x for x in lat if not isinstance(x,tuple)]
print(f"{label}: refresh {rs}s; reads={len(ds)} median={statistics.median(ds)*1000:.0f}ms max={max(ds)*1000:.0f}ms; LOCK waits by the reader: total={lck[0]}ms count={lck[1]} longest={lck[2]}ms; reader errors={err}")
