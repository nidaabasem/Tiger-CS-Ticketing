import os
import sys,time; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sql import conn
def refresh(extra=""):
    c=conn('TigerCsTicketing'); cur=c.cursor(); t=time.time()
    cur.execute("EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource=N'perf'"+extra)
    out=[]
    while True:
        if cur.description: out.append(cur.fetchall())
        if not cur.nextset(): break
    return round(time.time()-t,1), out
if __name__=='__main__':
    s,o=refresh(); print(s,o)
