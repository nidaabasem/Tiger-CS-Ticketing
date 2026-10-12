/*
  Verification of the current-month Due / Overdue rule (TigerCsTicketing).  READ-ONLY.  Run AFTER R004__read_procedures.sql, on UAT before the merge.

  Rule (month of @Today, Asia/Dubai):  Overdue = DueDate < monthStart;  Due = DueDate >= monthStart AND DueDate < nextMonthStart;  DueDate >= nextMonthStart is excluded.
  The procedures only take @Today as a parameter, so the SAME published data is checked as if today were: now, the first and the last day of the current month, 15 Dec 2026 and 10 Jan 2027
  (the December -> January rollover).  For each date and company the script compares
      usp_CollectionsRcv_GetUnits (Minimum Total cleared, result set 3 = the page's totals)
  with an independent recount straight from the PUBLISHED lines (no stored procedure), and prints OK / FAIL.
  Section 2 prints the evidence for the boundaries (the last day of the month, the first day of the next month) and section 3 the Payment Summary of one unit (set @UnitKey) next to its recount.
  Changes nothing.
*/
SET NOCOUNT ON;
DECLARE @Now date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date);
DECLARE @UnitKey nvarchar(220) = NULL;      -- e.g. N'TP140-101': a unit to compare in section 3 (NULL = skip)

DECLARE @Dates TABLE (Label varchar(40) NOT NULL, Today date NOT NULL);
INSERT @Dates VALUES ('now (Dubai)', @Now), ('first day of this month', DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1)), ('last day of this month', EOMONTH(@Now)),
                     ('15 Dec 2026 (rollover)', '20261215'), ('10 Jan 2027 (rollover)', '20270110');

CREATE TABLE #t (UnitCount int, LineCount int, DueLineCount int, OverdueLineCount int, DueTotal decimal(19,4), OverdueTotal decimal(19,4), Total decimal(19,4));
CREATE TABLE #r (Label varchar(40), Today date, MonthStart date, NextMonthStart date, CompanyId int,
                 ProcDue decimal(19,4), RecountDue decimal(19,4), ProcOverdue decimal(19,4), RecountOverdue decimal(19,4), ProcTotal decimal(19,4), RecountTotal decimal(19,4),
                 ExcludedNextMonthOnwards decimal(19,4), Result varchar(4));

DECLARE @label varchar(40), @d date, @c int, @ms date, @nms date;
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT d.Label, d.Today, c.CompanyId FROM @Dates d CROSS JOIN (VALUES (4), (32)) c (CompanyId) ORDER BY d.Today, c.CompanyId;
OPEN cur; FETCH NEXT FROM cur INTO @label, @d, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @ms = DATEFROMPARTS(YEAR(@d), MONTH(@d), 1), @nms = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(@d), MONTH(@d), 1));
    DELETE #t;
    INSERT #t EXEC dbo.usp_CollectionsRcv_GetUnits @Today = @d, @CompanyId = @c, @MinTotal = NULL, @PageNumber = 1, @PageSize = 1, @Only = 3;
    INSERT #r
    SELECT @label, @d, @ms, @nms, @c, t.DueTotal, x.Due, t.OverdueTotal, x.Overdue, t.Total, x.Due + x.Overdue, x.Excluded,
           CASE WHEN t.DueTotal = x.Due AND t.OverdueTotal = x.Overdue AND t.Total = x.Due + x.Overdue THEN 'OK' ELSE 'FAIL' END
      FROM #t t
     CROSS JOIN (SELECT ISNULL(SUM(CASE WHEN l.DueDate >= @ms AND l.DueDate < @nms THEN l.Amount END), 0) AS Due,
                        ISNULL(SUM(CASE WHEN l.DueDate < @ms THEN l.Amount END), 0) AS Overdue,
                        ISNULL(SUM(CASE WHEN l.DueDate >= @nms THEN l.Amount END), 0) AS Excluded
                   FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany co ON co.CompanyId = l.CompanyId AND co.CurrentRunId = l.RunId
                  WHERE l.CompanyId = @c AND l.Amount > 0 AND l.UnitId > 0 AND LTRIM(RTRIM(l.UnitCode)) NOT IN (N'', N'0') AND l.UnitCode NOT LIKE N'%*%') x;
    FETCH NEXT FROM cur INTO @label, @d, @c;
END;
CLOSE cur; DEALLOCATE cur;

-- 1. procedure vs independent recount (every row must say OK)
SELECT * FROM #r ORDER BY Today, CompanyId;

-- 2. boundary evidence, relative to now: the batch on the last day of this month (must be Due), the first day of next month (must be excluded), the day before this month (must be Overdue)
SELECT l.CompanyId,
       SUM(CASE WHEN l.DueDate = EOMONTH(@Now) THEN l.Amount ELSE 0 END)                         AS DueOnLastDayOfThisMonth_ShouldBeInDue,
       SUM(CASE WHEN l.DueDate = DATEADD(DAY, 1, EOMONTH(@Now)) THEN l.Amount ELSE 0 END)      AS DueOnFirstDayOfNextMonth_ShouldBeExcluded,
       SUM(CASE WHEN l.DueDate = DATEADD(DAY, -1, DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1)) THEN l.Amount ELSE 0 END) AS DueOnLastDayOfPreviousMonth_ShouldBeInOverdue,
       SUM(CASE WHEN l.DueDate > @Now AND l.DueDate <= EOMONTH(@Now) THEN l.Amount ELSE 0 END)  AS DueLaterThisMonth_ShouldBeInDue
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany co ON co.CompanyId = l.CompanyId AND co.CurrentRunId = l.RunId
 WHERE l.Amount > 0 AND l.UnitId > 0 AND LTRIM(RTRIM(l.UnitCode)) NOT IN (N'', N'0') AND l.UnitCode NOT LIKE N'%*%'
 GROUP BY l.CompanyId ORDER BY l.CompanyId;

-- 3. one unit: the Payment Summary read (result set 3 = ALL its unpaid instalments, future ones too; the application classifies them) next to the recount; its Due / Overdue must equal the Receivables row of the same unit
IF @UnitKey IS NOT NULL
BEGIN
    SELECT l.CompanyId, l.UnitCode, l.VoucherNumber, l.DueDate, l.Amount AS RemainingAmount,
           CASE WHEN l.DueDate < DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1) THEN 'Overdue' WHEN l.DueDate < DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1)) THEN 'Due' ELSE 'Excluded' END AS ExpectedClass
      FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany co ON co.CompanyId = l.CompanyId AND co.CurrentRunId = l.RunId
     WHERE UPPER(LTRIM(RTRIM(l.UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, SUBSTRING(@UnitKey, 3, 220) COLLATE Latin1_General_BIN2) AND l.Amount > 0
     ORDER BY l.CompanyId, l.DueDate DESC, l.VoucherNumber;
    EXEC dbo.usp_CollectionsRcv_GetUnit @UnitKey = @UnitKey, @CompanyId = NULL, @AsOfDate = @Now;
    EXEC dbo.usp_CollectionsRcv_GetUnits @Today = @Now, @Search = @UnitKey, @MinTotal = NULL;
END;

DROP TABLE #t; DROP TABLE #r;
