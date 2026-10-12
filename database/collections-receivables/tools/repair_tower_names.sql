/*
  repair_tower_names.sql - bring dbo.CollectionsTowers in line with the towers PACT actually holds, naming each tower from its SOURCE (never a name typed here).  Re-runnable.

  Why: Receivables / Campaigns / the tower picker show a tower's name from dbo.CollectionsTowers. A tower PACT holds that has no row there (TP119: V001 "intentionally NOT seeded"), or whose row only
  repeats the number (TowerName = N'119'), showed its bare number. The real name is what CRM calls the project of that tower (the CRM owner feed, V010/V012). This script reads that name through
  dbo.fn_Collections_TowerNames() - it is the SAME rule the pages use at run time - and writes it into CollectionsTowers so the tower can also be chosen in the picker.

  What it does, per (company, tower) of the CURRENT PACT snapshot (the company is the one whose snapshot holds the tower; a tower in both companies is handled once per company):
    INSERT     no CollectionsTowers row for (company, number) and CRM holds exactly ONE name for that company's tower  -> a new active row.
    RENAME     the row exists but its name is blank or only repeats the number, and CRM holds exactly one name           -> TowerName only (nothing else of the row changes).
    NO SOURCE  no usable name anywhere (or two different CRM names): REPORTED, nothing is written - the name must be entered by a person who knows it.
  Never deleted, never overwritten when the existing name is already a real name, never guessed.

  Usage:  run as is = DRY RUN (reports, changes nothing).  Set @Apply = 1 below to write.  Needs V012 and a loaded PACT snapshot + CRM owner run.
          Safe to run again and again: after an apply the second run reports nothing to do.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Apply bit = 0;

IF OBJECT_ID(N'dbo.fn_Collections_TowerNames', N'IF') IS NULL
    THROW 50120, N'dbo.fn_Collections_TowerNames does not exist: run V012 first.', 1;

-- The towers the current snapshot holds, per company.
SELECT DISTINCT s.CompanyId, LTRIM(RTRIM(s.TowerNumber)) AS TowerNumber
  INTO #pact
  FROM dbo.CollectionsRcvCompany c
  JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL AND s.TowerNumber IS NOT NULL AND LTRIM(RTRIM(s.TowerNumber)) <> N'';

-- What each (company, tower) would be called by the pages, and from which source.
SELECT p.CompanyId, p.TowerNumber,
       t.TowerId, CONVERT(nvarchar(400), t.TowerName) AS StoredName,
       n.TowerName AS SourceName, n.NameSource
  INTO #plan
  FROM #pact p
  OUTER APPLY (SELECT TOP (1) x.TowerId, x.TowerName FROM dbo.CollectionsTowers x
                WHERE x.CompanyId = p.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), x.TowerNumber))) = p.TowerNumber
                ORDER BY CASE WHEN x.IsActive = 1 THEN 0 ELSE 1 END, x.TowerId) t
  LEFT JOIN dbo.fn_Collections_TowerNames() n ON n.CompanyId = p.CompanyId AND n.TowerNumber = p.TowerNumber;

SELECT CASE WHEN TowerId IS NULL AND NameSource = 'Crm' AND TowerNumber NOT LIKE N'%[^0-9]%' THEN 'INSERT'
            WHEN TowerId IS NOT NULL AND NameSource = 'Crm' THEN 'RENAME'
            WHEN NameSource = 'CollectionsTowers' THEN 'OK'
            ELSE 'NO SOURCE' END AS Action_,
       CompanyId, TowerNumber, TowerId, StoredName, SourceName, NameSource
  INTO #actions
  FROM #plan;

PRINT N'--- 1. Proposed changes (INSERT / RENAME). Nothing is written unless @Apply = 1 ---';
SELECT Action_, CompanyId, TowerNumber, TowerId, StoredName AS OldName, SourceName AS NewName, NameSource
  FROM #actions WHERE Action_ IN ('INSERT', 'RENAME') ORDER BY CompanyId, TRY_CONVERT(int, TowerNumber) DESC;

PRINT N'--- 2. Towers with NO usable name from any source: a person must name them (NOT written) ---';
SELECT CompanyId, TowerNumber, TowerId, StoredName AS StoredName,
       CASE WHEN TowerId IS NULL AND NameSource = 'Crm' THEN 'non-numeric tower number: not inserted'
            WHEN TowerId IS NULL THEN 'no CollectionsTowers row and CRM holds no single name'
            ELSE 'stored name is blank or only the number, and CRM holds no single name' END AS Why
  FROM #actions WHERE Action_ = 'NO SOURCE' ORDER BY CompanyId, TRY_CONVERT(int, TowerNumber) DESC;

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;
        UPDATE t SET TowerName = a.SourceName
          FROM dbo.CollectionsTowers t JOIN #actions a ON a.TowerId = t.TowerId
         WHERE a.Action_ = 'RENAME';
        DECLARE @renamed int = @@ROWCOUNT;

        INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId, IsActive)
        SELECT a.TowerNumber, a.SourceName, a.CompanyId, 1
          FROM #actions a
         WHERE a.Action_ = 'INSERT'
           AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsTowers x WHERE x.CompanyId = a.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), x.TowerNumber))) = a.TowerNumber);
        DECLARE @inserted int = @@ROWCOUNT;
    COMMIT;
    PRINT CONCAT(N'APPLIED: ', @inserted, N' tower(s) inserted, ', @renamed, N' renamed.');
END
ELSE
BEGIN
    DECLARE @wouldInsert int = (SELECT COUNT(*) FROM #actions WHERE Action_ = 'INSERT'), @wouldRename int = (SELECT COUNT(*) FROM #actions WHERE Action_ = 'RENAME');
    PRINT CONCAT(N'DRY RUN: ', @wouldInsert, N' insert(s) and ', @wouldRename, N' rename(s) proposed. Set @Apply = 1 to write them.');
END;

PRINT N'--- 3. Result: every tower of the snapshot as the pages show it now ---';
SELECT p.CompanyId, p.TowerNumber, n.TowerName, n.NameSource
  FROM #pact p LEFT JOIN dbo.fn_Collections_TowerNames() n ON n.CompanyId = p.CompanyId AND n.TowerNumber = p.TowerNumber
 ORDER BY TRY_CONVERT(int, p.TowerNumber) DESC, p.CompanyId;
