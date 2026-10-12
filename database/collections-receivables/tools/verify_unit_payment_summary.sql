/*
  Verification of the Payment tab's figures (TigerCsTicketing).  READ-ONLY.  Run AFTER R004__read_procedures.sql, on UAT, together with verify_month_rule.sql.

  For a sample of units (or the one unit in @UnitKey) it checks, against an independent recount of the PUBLISHED lines (no stored procedure), that
    * usp_CollectionsRcv_GetUnit (@Only = 3, the instalments the Payment tab lists) returns EVERY unpaid instalment of the unit - future months included - and nothing of another unit;
    * the cards equal the table:  Remaining Balance = sum of all lines;  Due This Month = lines due inside the current Dubai month;  Overdue = lines due before its first day;
      Remaining Balance = Overdue + Due This Month + Not yet due (so it is NOT Due + Overdue);
    * Due This Month / Overdue equal the Receivables row of the same unit (usp_CollectionsRcv_GetUnits, result set 4): one rule on every page.
  Total Amount / Paid Amount are not checked: the PACT source returns neither (they are shown as Unavailable by design).
  The sample prefers units that have instalments in the current month, before it AND after it, so every class is exercised.  Prints one line per unit with OK / FAIL, then a summary.
*/
SET NOCOUNT ON;   -- the temp tables are named #v_* on purpose: the read procedures use #k / #l / #u internally and a caller table of the same name would shadow theirs
DECLARE @UnitKey nvarchar(220) = NULL;      -- e.g. N'TP140-101'; NULL = sample @Sample units per company
DECLARE @Sample int = 10;
DECLARE @Today date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date);
DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1), @NextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1));

CREATE TABLE #v_pub (CompanyId int, TenantId nvarchar(100) COLLATE Latin1_General_BIN2, UnitId bigint, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2, VoucherNumber nvarchar(100), DueDate date, Amount decimal(19,4));
INSERT #v_pub
SELECT l.CompanyId, l.TenantId, l.UnitId, l.UnitCode, l.VoucherNumber, l.DueDate, l.Amount
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany co ON co.CompanyId = l.CompanyId AND co.CurrentRunId = l.RunId
 WHERE l.CompanyId IN (4, 32) AND l.Amount > 0 AND l.UnitId > 0 AND LTRIM(RTRIM(l.UnitCode)) NOT IN (N'', N'0') AND l.UnitCode NOT LIKE N'%*%';

CREATE TABLE #v_units (CompanyId int, TenantId nvarchar(100) COLLATE Latin1_General_BIN2, UnitId bigint, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2);
IF @UnitKey IS NOT NULL
    INSERT #v_units SELECT DISTINCT CompanyId, TenantId, UnitId, UnitCode FROM #v_pub
     WHERE UPPER(LTRIM(RTRIM(UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, SUBSTRING(@UnitKey, 3, 220) COLLATE Latin1_General_BIN2);
ELSE
    INSERT #v_units
    SELECT CompanyId, TenantId, UnitId, UnitCode FROM (
        SELECT CompanyId, TenantId, UnitId, UnitCode,
               ROW_NUMBER() OVER (PARTITION BY CompanyId ORDER BY
                   CASE WHEN SUM(CASE WHEN DueDate < @MonthStart THEN 1 ELSE 0 END) > 0 AND SUM(CASE WHEN DueDate >= @MonthStart AND DueDate < @NextMonthStart THEN 1 ELSE 0 END) > 0
                         AND SUM(CASE WHEN DueDate >= @NextMonthStart THEN 1 ELSE 0 END) > 0 THEN 0 ELSE 1 END, MAX(DueDate) DESC, UnitCode) AS rn
          FROM #v_pub GROUP BY CompanyId, TenantId, UnitId, UnitCode) x
     WHERE rn <= @Sample;

CREATE TABLE #v_get (CompanyId int, TenantId nvarchar(100), UnitId bigint, VoucherNumber nvarchar(100), ChequeNumber nvarchar(100), DueDate date, RemainingAmount decimal(19,4), SourceStatus nvarchar(50));
CREATE TABLE #v_row (CompanyId int, TenantId nvarchar(100), UnitId bigint, UnitCode nvarchar(200), FullName nvarchar(400), TowerNumber nvarchar(20), TowerName nvarchar(400), ProjectCode nvarchar(200),
                 Due decimal(19,4), Overdue decimal(19,4), Total decimal(19,4), InstalmentCount int, LatestDueDate date, CrmCustomers int, CrmCustomerId int, CrmName nvarchar(400), CrmPhone nvarchar(50), CrmEmail nvarchar(320));
CREATE TABLE #v_res (CompanyId int, UnitCode nvarchar(200), Lines int, ProcLines int, RemainingBalance decimal(19,4), ProcRemaining decimal(19,4), DueThisMonth decimal(19,4), Overdue decimal(19,4), NotYetDue decimal(19,4),
                 ReceivablesDue decimal(19,4), ReceivablesOverdue decimal(19,4), Result varchar(4));

DECLARE @c int, @t nvarchar(100), @uid bigint, @code nvarchar(200), @key nvarchar(220);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT CompanyId, TenantId, UnitId, UnitCode FROM #v_units ORDER BY CompanyId, UnitCode;
OPEN cur; FETCH NEXT FROM cur INTO @c, @t, @uid, @code;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @key = UPPER(LTRIM(RTRIM(@code)));
    DELETE #v_get; DELETE #v_row;
    INSERT #v_get EXEC dbo.usp_CollectionsRcv_GetUnit @UnitKey = @key, @CompanyId = @c, @AsOfDate = @Today, @Only = 3;
    INSERT #v_row EXEC dbo.usp_CollectionsRcv_GetUnits @Today = @Today, @CompanyId = @c, @Search = @key, @MinTotal = NULL, @PageNumber = 1, @PageSize = 100, @Only = 4;
    INSERT #v_res
    SELECT @c, @code, x.Lines, g.Lines, x.Remaining, g.Remaining, x.Due, x.Overdue, x.NotYetDue, ISNULL(u.Due, 0), ISNULL(u.Overdue, 0),
           CASE WHEN x.Lines = g.Lines AND x.Remaining = g.Remaining AND x.Remaining = x.Overdue + x.Due + x.NotYetDue
                 AND x.Due = ISNULL(u.Due, 0) AND x.Overdue = ISNULL(u.Overdue, 0) THEN 'OK' ELSE 'FAIL' END
      FROM (SELECT COUNT(*) AS Lines, ISNULL(SUM(Amount), 0) AS Remaining,
                   ISNULL(SUM(CASE WHEN DueDate >= @MonthStart AND DueDate < @NextMonthStart THEN Amount END), 0) AS Due,
                   ISNULL(SUM(CASE WHEN DueDate < @MonthStart THEN Amount END), 0) AS Overdue,
                   ISNULL(SUM(CASE WHEN DueDate >= @NextMonthStart THEN Amount END), 0) AS NotYetDue
              FROM #v_pub WHERE CompanyId = @c AND TenantId = @t AND UnitId = @uid AND UnitCode = @code) x
     CROSS JOIN (SELECT COUNT(*) AS Lines, ISNULL(SUM(RemainingAmount), 0) AS Remaining FROM #v_get WHERE CompanyId = @c AND TenantId = @t COLLATE Latin1_General_BIN2 AND UnitId = @uid) g
      LEFT JOIN (SELECT Due, Overdue FROM #v_row WHERE CompanyId = @c AND TenantId = @t COLLATE Latin1_General_BIN2 AND UnitId = @uid AND UnitCode = @code COLLATE Latin1_General_BIN2) u ON 1 = 1;
    FETCH NEXT FROM cur INTO @c, @t, @uid, @code;
END;
CLOSE cur; DEALLOCATE cur;

SELECT * FROM #v_res ORDER BY CompanyId, UnitCode;
SELECT COUNT(*) AS UnitsChecked, SUM(CASE WHEN Result = 'OK' THEN 1 ELSE 0 END) AS Ok, SUM(CASE WHEN Result = 'FAIL' THEN 1 ELSE 0 END) AS Fail,
       SUM(CASE WHEN NotYetDue > 0 THEN 1 ELSE 0 END) AS UnitsWithFutureInstalments, SUM(CASE WHEN DueThisMonth > 0 THEN 1 ELSE 0 END) AS UnitsDueThisMonth, SUM(CASE WHEN Overdue > 0 THEN 1 ELSE 0 END) AS UnitsOverdue
  FROM #v_res;
DROP TABLE #v_pub; DROP TABLE #v_units; DROP TABLE #v_get; DROP TABLE #v_row; DROP TABLE #v_res;
