/*
  PREFLIGHT for the V2 receivables procedures. READ-ONLY: it creates nothing and changes nothing.
  Run it first, in the database that currently holds dbo.p4AccountReceivables / dbo.p32AccountReceivables
  (the database named by ConnectionStrings:PACTRPT). Every row of the result must read OK before 10-/11- are run.
*/
SET NOCOUNT ON;
DECLARE @r TABLE (Seq int IDENTITY(1,1), CheckName nvarchar(120), Result nvarchar(10), Detail nvarchar(400));

INSERT @r SELECT N'Current database', N'INFO', DB_NAME();
INSERT @r SELECT N'SQL Server version (needs 2012+ for window frames and THROW)', CASE WHEN CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) >= 11 THEN N'OK' ELSE N'FAIL' END,
                 CONVERT(nvarchar(100), SERVERPROPERTY('ProductVersion'));
INSERT @r SELECT N'Database compatibility level >= 110', CASE WHEN compatibility_level >= 110 THEN N'OK' ELSE N'FAIL' END, CONVERT(nvarchar(20), compatibility_level)
FROM sys.databases WHERE name = DB_NAME();
INSERT @r SELECT N'Original dbo.p4AccountReceivables exists here', CASE WHEN OBJECT_ID(N'dbo.p4AccountReceivables', N'P') IS NOT NULL THEN N'OK' ELSE N'FAIL' END, N'V2 must live beside the original';
INSERT @r SELECT N'Original dbo.p32AccountReceivables exists here', CASE WHEN OBJECT_ID(N'dbo.p32AccountReceivables', N'P') IS NOT NULL THEN N'OK' ELSE N'FAIL' END, N'V2 must live beside the original';
INSERT @r SELECT N'dbo.p4AccountReceivablesV2 already deployed', CASE WHEN OBJECT_ID(N'dbo.p4AccountReceivablesV2', N'P') IS NULL THEN N'OK' ELSE N'INFO' END,
                 CASE WHEN OBJECT_ID(N'dbo.p4AccountReceivablesV2', N'P') IS NULL THEN N'not present (a first deployment)' ELSE N'present: deployment will replace its body' END;
INSERT @r SELECT N'dbo.p32AccountReceivablesV2 already deployed', CASE WHEN OBJECT_ID(N'dbo.p32AccountReceivablesV2', N'P') IS NULL THEN N'OK' ELSE N'INFO' END,
                 CASE WHEN OBJECT_ID(N'dbo.p32AccountReceivablesV2', N'P') IS NULL THEN N'not present (a first deployment)' ELSE N'present: deployment will replace its body' END;
INSERT @r SELECT N'CREATE PROCEDURE permission', CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE PROCEDURE') = 1 THEN N'OK' ELSE N'FAIL' END, SUSER_SNAME();
INSERT @r SELECT N'Access to database pact2c4 (company 4 ledger)', CASE WHEN DB_ID(N'pact2c4') IS NOT NULL AND HAS_DBACCESS(N'pact2c4') = 1 THEN N'OK' ELSE N'FAIL' END, N'p4AccountReceivablesV2 reads pact2c4..';
INSERT @r SELECT N'Access to database PACT2C32 (company 32 ledger)', CASE WHEN DB_ID(N'PACT2C32') IS NOT NULL AND HAS_DBACCESS(N'PACT2C32') = 1 THEN N'OK' ELSE N'FAIL' END, N'p32AccountReceivablesV2 reads PACT2C32..';

SELECT Seq, CheckName, Result, Detail FROM @r ORDER BY Seq;

-- Who may run the originals today: grant the same principals EXECUTE on the V2 procedures (see 15-grant-execute.sql).
SELECT OBJECT_NAME(p.major_id) AS ProcedureName, USER_NAME(p.grantee_principal_id) AS Grantee, p.permission_name, p.state_desc
FROM sys.database_permissions p
WHERE p.class = 1 AND OBJECT_NAME(p.major_id) IN (N'p4AccountReceivables', N'p32AccountReceivables')
ORDER BY ProcedureName, Grantee;
