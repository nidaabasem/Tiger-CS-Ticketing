import os
import sys,time; sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sql import conn
c=conn('TigerCsTicketing'); cur=c.cursor()
def t(sql,n=4):
    ts=[]; sizes=[]; rs=[]
    for i in range(n):
        s=time.time(); cur.execute(sql); sizes=[]; rs=[]
        while True:
            if cur.description:
                rows=cur.fetchall(); sizes.append(len(rows)); rs.append(rows[:1])
            if not cur.nextset(): break
        ts.append(round((time.time()-s)*1000))
    return ts,sizes,rs[2] if len(rs)>2 else None
A="@FromDate='20260101',@ToDate='20261031',@AsOfDate='20261001'"
for name,extra in [('outstanding min100',"@MinAmount=100,@PaymentFilter='outstanding'"),('outstanding min0',"@MinAmount=0,@PaymentFilter='outstanding'"),('unpaid',"@MinAmount=100,@PaymentFilter='unpaid'"),
                   ('partial',"@MinAmount=100,@PaymentFilter='partial'"),('paid (min ignored)',"@MinAmount=100000,@PaymentFilter='paid'"),('all (min ignored)',"@MinAmount=100000,@PaymentFilter='all'"),
                   ('tower5 outstanding',"@TowerId=5,@MinAmount=100,@PaymentFilter='outstanding'"),('Feb 2026 month',"@MinAmount=100,@PaymentFilter='outstanding'")]:
    a=A if name!='Feb 2026 month' else "@FromDate='20260201',@ToDate='20260228',@AsOfDate='20261001'"
    ts,sizes,tot=t(f"EXEC dbo.usp_Collections_GetInstalmentsPage {a},{extra}")
    print(f"{name:24} ms={ts} sets={sizes} totals={tot}")
