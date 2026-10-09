"""Tiny T-SQL runner for the LOCAL test harness (pymssql). Credentials come from the environment - nothing is stored in the repository.
   LOCALSQL_HOST (default localhost), LOCALSQL_USER (default sa), LOCALSQL_PASSWORD (required)."""
import os, re, sys, pymssql

def conn(db='master'):
    password = os.environ['LOCALSQL_PASSWORD']
    return pymssql.connect(os.environ.get('LOCALSQL_HOST', 'localhost'), os.environ.get('LOCALSQL_USER', 'sa'), password, db, autocommit=True, login_timeout=30, timeout=0)

def run_file(path, db='master', quiet=False):
    sql = open(path, encoding='utf-8-sig').read().replace('$(LOCALSQL_PASSWORD)', os.environ['LOCALSQL_PASSWORD'])
    cur = conn(db).cursor()
    for batch in re.split(r'^\s*GO\s*$', sql, flags=re.M | re.I):
        if batch.strip():
            cur.execute(batch)
            while True:
                if cur.description and not quiet:
                    rows = cur.fetchall(); print([d[0] for d in cur.description]); [print(r) for r in rows[:8]]
                if not cur.nextset(): break

if __name__ == '__main__':
    run_file(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else 'master')
