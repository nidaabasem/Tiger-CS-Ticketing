/*
  Pre-cleanup verification, part 3 (TigerCsTicketing).  READ-ONLY.  Checks, through the REAL read procedure the Receivables page uses, that for every apartment on the first @Pages pages
  of each company (newest first, @PageSize apartments per page, Minimum Total cleared so nothing is hidden):
    * Total = Due + Overdue = the sum of the apartment's details (usp_CollectionsRcv_GetUnits result set 5), and Due / Overdue equal the sums of the details classified Due / Overdue;
    * the number of details = InstalmentCount;  no detail is due from the next month on;  Overdue <=> due before the first day of the current month, Due <=> due inside it;
    * the apartments are ordered by newest payment, newest first, and the details of an apartment newest first;
    * no apartment of another company is returned.
  Prints one line per company with OK / FAIL and the number of mismatches.  Raise @Pages to check more (the real volume: apartments / @PageSize pages per company).
*/
SET NOCOUNT ON;
DECLARE @Pages int = 20, @PageSize int = 100;
DECLARE @Today date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date);
DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1), @NextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1));

CREATE TABLE #u (Seq int IDENTITY(1,1) PRIMARY KEY, CompanyId int, TenantId nvarchar(100), UnitId bigint, UnitCode nvarchar(200), FullName nvarchar(400), TowerNumber nvarchar(20), TowerName nvarchar(400), ProjectCode nvarchar(200),
                 Due decimal(19,4), Overdue decimal(19,4), Total decimal(19,4), InstalmentCount int, LatestDueDate date, CrmCustomers int, CrmCustomerId int, CrmName nvarchar(400), CrmPhone nvarchar(50), CrmEmail nvarchar(320), PageNo int);
CREATE TABLE #l (Seq int IDENTITY(1,1) PRIMARY KEY, CompanyId int, TenantId nvarchar(100), UnitId bigint, UnitCode nvarchar(200), VoucherNumber nvarchar(100), ChequeNumber nvarchar(100), DueDate date, RemainingAmount decimal(19,4),
                 Mobile nvarchar(100), Email nvarchar(400), SourceStatus nvarchar(50), Classification varchar(7), PageNo int);
CREATE INDEX IX_l_apartment ON #l (CompanyId, TenantId, UnitId, UnitCode) INCLUDE (RemainingAmount, Classification);
CREATE INDEX IX_u_apartment ON #u (CompanyId, TenantId, UnitId, UnitCode);
CREATE TABLE #r (CompanyId int, ApartmentsChecked int, Payments int, TotalMismatch int, DueOverdueMismatch int, CountMismatch int, FuturePayments int, ClassificationWrong int, ApartmentOrderWrong int, DetailOrderWrong int, OtherCompanyRows int, Result varchar(6));

DECLARE @c int, @p int, @first int, @firstL int;
DECLARE company_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT CompanyId FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) ORDER BY CompanyId;
OPEN company_cursor; FETCH NEXT FROM company_cursor INTO @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    TRUNCATE TABLE #u; TRUNCATE TABLE #l; SET @p = 1;
    WHILE @p <= @Pages
    BEGIN
        INSERT #u (CompanyId, TenantId, UnitId, UnitCode, FullName, TowerNumber, TowerName, ProjectCode, Due, Overdue, Total, InstalmentCount, LatestDueDate, CrmCustomers, CrmCustomerId, CrmName, CrmPhone, CrmEmail)
            EXEC dbo.usp_CollectionsRcv_GetUnits @Today = @Today, @CompanyId = @c, @MinTotal = NULL, @PageNumber = @p, @PageSize = @PageSize, @Only = 4;
        IF @@ROWCOUNT = 0 BREAK;
        UPDATE #u SET PageNo = @p WHERE PageNo IS NULL;
        INSERT #l (CompanyId, TenantId, UnitId, UnitCode, VoucherNumber, ChequeNumber, DueDate, RemainingAmount, Mobile, Email, SourceStatus, Classification)
            EXEC dbo.usp_CollectionsRcv_GetUnits @Today = @Today, @CompanyId = @c, @MinTotal = NULL, @PageNumber = @p, @PageSize = @PageSize, @Only = 5;
        UPDATE #l SET PageNo = @p WHERE PageNo IS NULL;
        SET @p += 1;
    END;
    INSERT #r
    SELECT @c, (SELECT COUNT(*) FROM #u), (SELECT COUNT(*) FROM #l),
        (SELECT COUNT(*) FROM #u u WHERE u.Total <> ISNULL((SELECT SUM(l.RemainingAmount) FROM #l l WHERE l.CompanyId = u.CompanyId AND l.TenantId = u.TenantId AND l.UnitId = u.UnitId AND l.UnitCode = u.UnitCode), 0)),
        (SELECT COUNT(*) FROM #u u WHERE u.Total <> u.Due + u.Overdue
             OR u.Due <> ISNULL((SELECT SUM(l.RemainingAmount) FROM #l l WHERE l.CompanyId = u.CompanyId AND l.TenantId = u.TenantId AND l.UnitId = u.UnitId AND l.UnitCode = u.UnitCode AND l.Classification = 'Due'), 0)
             OR u.Overdue <> ISNULL((SELECT SUM(l.RemainingAmount) FROM #l l WHERE l.CompanyId = u.CompanyId AND l.TenantId = u.TenantId AND l.UnitId = u.UnitId AND l.UnitCode = u.UnitCode AND l.Classification = 'Overdue'), 0)),
        (SELECT COUNT(*) FROM #u u WHERE u.InstalmentCount <> (SELECT COUNT(*) FROM #l l WHERE l.CompanyId = u.CompanyId AND l.TenantId = u.TenantId AND l.UnitId = u.UnitId AND l.UnitCode = u.UnitCode)),
        (SELECT COUNT(*) FROM #l WHERE DueDate >= @NextMonthStart),
        (SELECT COUNT(*) FROM #l WHERE (DueDate < @MonthStart AND Classification <> 'Overdue') OR (DueDate >= @MonthStart AND DueDate < @NextMonthStart AND Classification <> 'Due')),
        (SELECT COUNT(*) FROM #u a JOIN #u b ON b.Seq = a.Seq + 1 WHERE b.LatestDueDate > a.LatestDueDate),
        (SELECT COUNT(*) FROM #l a JOIN #l b ON b.Seq = a.Seq + 1 AND b.CompanyId = a.CompanyId AND b.TenantId = a.TenantId AND b.UnitId = a.UnitId AND b.UnitCode = a.UnitCode WHERE b.DueDate > a.DueDate),
        (SELECT COUNT(*) FROM #u WHERE CompanyId <> @c) + (SELECT COUNT(*) FROM #l WHERE CompanyId <> @c), NULL;
    FETCH NEXT FROM company_cursor INTO @c;
END;
CLOSE company_cursor; DEALLOCATE company_cursor;
UPDATE #r SET Result = CASE WHEN ApartmentsChecked > 0 AND TotalMismatch + DueOverdueMismatch + CountMismatch + FuturePayments + ClassificationWrong + ApartmentOrderWrong + DetailOrderWrong + OtherCompanyRows = 0 THEN 'OK' ELSE 'FAIL' END;
SELECT * FROM #r ORDER BY CompanyId;
