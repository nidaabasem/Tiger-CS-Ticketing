import os
import sys,time,threading; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sql import conn
from refresh import refresh
res={}
def run(): res['r']=refresh()
th=threading.Thread(target=run); th.start()
c=conn('TigerCsTicketing'); cur=c.cursor(); seen={}
while th.is_alive():
    cur.execute("""SELECT l.resource_type, l.request_mode, COUNT(*) FROM sys.dm_tran_locks l
                   WHERE l.resource_database_id = DB_ID() AND l.resource_associated_entity_id = OBJECT_ID('dbo.CollectionsReceivableSnapshot') AND l.request_session_id <> @@SPID
                   GROUP BY l.resource_type, l.request_mode""")
    for t,m,n in cur.fetchall(): seen[(t,m)]=max(seen.get((t,m),0),n)
    time.sleep(0.05)
th.join(); print('max concurrent locks held on Snapshot table by the refresh:', seen)
