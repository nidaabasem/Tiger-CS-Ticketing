import os
import sys,time; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sql import conn
c=conn('TigerCsTicketing'); cur=c.cursor()
def t(sql,n=4):
    ts=[]
    for i in range(n):
        s=time.time(); cur.execute(sql); sizes=[]
        while True:
            if cur.description: sizes.append(len(cur.fetchall()))
            if not cur.nextset(): break
        ts.append(round((time.time()-s)*1000))
    return ts,sizes
A="@FromDate='20260101',@ToDate='20261031',@AsOfDate='20261001',@MinAmount=100"
for name,sql in [('page all towers',f"EXEC dbo.usp_Collections_GetReceivablesPage {A}"),('page tower5',f"EXEC dbo.usp_Collections_GetReceivablesPage @TowerId=5,{A}"),
                 ('page page=2000 size 25',f"EXEC dbo.usp_Collections_GetReceivablesPage {A},@PageNumber=1000"),
                 ('page search Customer 1234',f"EXEC dbo.usp_Collections_GetReceivablesPage {A},@Search=N'Customer 1234'"),
                 ('page overdue only',f"EXEC dbo.usp_Collections_GetReceivablesPage {A},@Status='overdue'")]:
    print(name, t(sql))
