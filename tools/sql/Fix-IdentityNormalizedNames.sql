/*
  Fix-IdentityNormalizedNames.sql

  Why: ASP.NET Identity looks a user up by NormalizedUserName (always upper-case). An AspNetUsers row whose
  NormalizedUserName / NormalizedEmail is NULL or not UPPER(UserName) / UPPER(Email) can never sign in:
  UserManager.FindByNameAsync finds nothing, so TigerCS shows "The email/employee ID or password was not accepted."
  Rows created through the application (UserManager.CreateAsync) are always correct; rows inserted or edited
  directly in SQL are the usual cause.

  Preferred fix for a single user: Admin > Users > Reset password (Identity rewrites the normalized columns).
  Use this script to repair many rows at once.

  Safe by default: it only reports unless @Apply = 1. Nothing is touched outside the Normalized* columns.
  Passwords, security stamps and roles are NOT changed.

  Usage: run against the target database, review the first result set, then set @Apply = 1 and run again.
*/
SET NOCOUNT ON;

DECLARE @Apply bit = 0;   -- 0 = report only (default), 1 = repair

-- Rows that cannot sign in because of their normalized columns.
SELECT u.Id,
       u.UserName,
       u.NormalizedUserName,
       u.Email,
       u.NormalizedEmail,
       CASE WHEN u.PasswordHash IS NULL THEN 'NO PASSWORD' ELSE 'has password' END AS PasswordState
FROM AspNetUsers u
WHERE u.NormalizedUserName IS NULL
   OR u.NormalizedUserName <> UPPER(u.UserName) COLLATE Latin1_General_CS_AS
   OR (u.Email IS NOT NULL AND (u.NormalizedEmail IS NULL
       OR u.NormalizedEmail <> UPPER(u.Email) COLLATE Latin1_General_CS_AS));

-- The repair would collide on the unique NormalizedUserName index if two users differ only by case.
SELECT UPPER(UserName) AS WouldCollideOn, COUNT(*) AS Users
FROM AspNetUsers
GROUP BY UPPER(UserName)
HAVING COUNT(*) > 1;

IF @Apply = 1
BEGIN
    IF EXISTS (SELECT 1 FROM AspNetUsers GROUP BY UPPER(UserName) HAVING COUNT(*) > 1)
    BEGIN
        RAISERROR('Two users differ only by case; resolve them before applying. Nothing was changed.', 16, 1);
        RETURN;
    END;

    BEGIN TRAN;

    UPDATE u
    SET NormalizedUserName = UPPER(u.UserName),
        NormalizedEmail    = CASE WHEN u.Email IS NULL THEN NULL ELSE UPPER(u.Email) END
    FROM AspNetUsers u
    WHERE u.NormalizedUserName IS NULL
       OR u.NormalizedUserName <> UPPER(u.UserName) COLLATE Latin1_General_CS_AS
       OR (u.Email IS NOT NULL AND (u.NormalizedEmail IS NULL
           OR u.NormalizedEmail <> UPPER(u.Email) COLLATE Latin1_General_CS_AS));

    SELECT @@ROWCOUNT AS RowsRepaired;

    COMMIT TRAN;
END
ELSE
    PRINT 'Report only. Set @Apply = 1 to repair the rows listed above.';
