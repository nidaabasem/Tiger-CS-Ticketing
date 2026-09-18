/*
    ConfigureReopenApprovalRequirements.sql
    =======================================

    Configures ApprovalType.ReopenApproval (3) as a Role-targeted approval
    requirement on every ACTIVE request type that permits Reopen, so users
    without direct Reopen permission can ask for one.

        ApprovalType             = 3  (ReopenApproval)
        TargetKind               = 2  (Role)
        TargetRoleName           = N'CS Manager'   (Roles.CsManager)
        BlocksWorkUntilApproved  = 0
        IsActive                 = 1

    WHY A SCRIPT RATHER THAN THE SEED
    ---------------------------------
    WorkflowReferenceData.SeedAsync guards the whole approval-requirement
    block with `IF NOT EXISTS (SELECT 1 FROM RequestTypeApprovalRequirements)`
    — a per-TABLE guard, not per-row. Any environment that has ever seeded has
    rows there, so the seed can never add these and can never overwrite what
    Administration has configured. The seed was updated in the same change,
    but it reaches BRAND-NEW databases only. For every existing environment,
    THIS SCRIPT IS THE AUTHORITATIVE UPDATE.

    WHY BlocksWorkUntilApproved = 0
    -------------------------------
    A Reopen Approval is raised against a ticket that is already Closed, so
    there is no work in flight for it to hold up. Accounting and Customer
    Service approval are sequenced ahead of work by the SLA document and
    therefore keep BlocksWorkUntilApproved = 1; nothing here touches them.

    SAFETY
    ------
    * Idempotent. Re-running inserts nothing further; the second run reports
      0 inserted and leaves every row as it is.
    * Additive only. It never UPDATEs or DELETEs any row. AccountingApproval,
      CustomerServiceApproval and every other requirement are untouched — Send
      Receipts and Handover Request gain ReopenApproval as an independent
      SECOND requirement, which the unique key (RequestTypeId, ApprovalType)
      permits by design.
    * No hard-coded RequestTypeId values anywhere. Request types are matched
      by their own data; the optional baseline reconciliation at the end keys
      on (Department Code, Request Type Name), which is unique by
      IX_RequestTypes_DepartmentId_Name.
    * Transactional, with XACT_ABORT ON — it either applies fully or not at all.

    RUNNING IT
    ----------
        sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -I \
               -i ConfigureReopenApprovalRequirements.sql

    -I (QUOTED_IDENTIFIER ON) is required: sqlcmd defaults it OFF, and SQL
    Server then refuses DML against tables carrying filtered indexes. SSMS and
    SqlClient default it ON. Same rule as this repository's other deployment
    scripts.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @ReopenApproval   tinyint       = 3;             -- ApprovalType.ReopenApproval
DECLARE @TargetKindRole   tinyint       = 2;             -- ApprovalTargetKind.Role
DECLARE @ApproverRole     nvarchar(64)  = N'CS Manager'; -- Roles.CsManager
DECLARE @BlocksWork       bit           = 0;
DECLARE @Inserted         int           = 0;

-------------------------------------------------------------------------------
-- 0. Pre-flight. Fail loudly rather than writing configuration that cannot work.
-------------------------------------------------------------------------------

IF OBJECT_ID(N'[RequestTypeApprovalRequirements]', N'U') IS NULL
    OR OBJECT_ID(N'[RequestTypes]', N'U') IS NULL
    OR OBJECT_ID(N'[Departments]', N'U') IS NULL
BEGIN
    THROW 50001, N'Schema not found: apply EF migrations (through AddApprovalWorkflow) before running this script.', 1;
END;

-- The approval targets a role by name; a name no account can hold would make
-- every request unactionable. Roles are seeded from Roles.All into AspNetRoles.
IF OBJECT_ID(N'[AspNetRoles]', N'U') IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM [AspNetRoles] WHERE [Name] = @ApproverRole)
BEGIN
    DECLARE @roleMissing nvarchar(200) =
        N'Role ''' + @ApproverRole + N''' does not exist in AspNetRoles — approvals would be unactionable.';
    THROW 50002, @roleMissing, 1;
END;

-------------------------------------------------------------------------------
-- 1. Before: what is there now.
-------------------------------------------------------------------------------

PRINT '=== BEFORE ===';

SELECT
    [Department]        = d.[Code],
    [RequestType]       = rt.[Name],
    [RequestTypeActive] = rt.[IsActive],
    [AllowReopen]       = rt.[AllowReopen],
    [ExistingApprovals] = STUFF((
        SELECT N', ' + CASE r.[ApprovalType]
                           WHEN 1 THEN N'AccountingApproval'
                           WHEN 2 THEN N'CustomerServiceApproval'
                           WHEN 3 THEN N'ReopenApproval'
                           ELSE CONCAT(N'ApprovalType#', r.[ApprovalType])
                       END
               + CASE WHEN r.[IsActive] = 0 THEN N' (inactive)' ELSE N'' END
        FROM [RequestTypeApprovalRequirements] r
        WHERE r.[RequestTypeId] = rt.[RequestTypeId]
        ORDER BY r.[ApprovalType]
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
FROM [RequestTypes] rt
INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
ORDER BY d.[Code], rt.[Name];

-------------------------------------------------------------------------------
-- 2. Apply.
--
--    The business rule is a PREDICATE, not a list: "every request type that
--    supports Reopen also supports asking for one". Expressing it that way
--    means an environment carrying request types beyond the seeded baseline
--    (created through Administration) is covered too, and no RequestTypeId is
--    ever named. Section 4 reconciles what was actually matched against the
--    approved baseline so any drift is visible rather than silent.
--
--    NOT EXISTS is on (RequestTypeId, ApprovalType) with NO IsActive
--    predicate, deliberately: IX_RequestTypeApprovalRequirements_RequestTypeId
--    _ApprovalType is unique and UNFILTERED, so a row that someone
--    deliberately DEACTIVATED still occupies the slot. Inserting past it would
--    violate the index; silently reactivating it would overturn an operator's
--    decision. Such rows are left exactly as they are and listed in section 3.
-------------------------------------------------------------------------------

BEGIN TRANSACTION;

INSERT INTO [RequestTypeApprovalRequirements]
    ([RequestTypeId], [ApprovalType], [TargetKind],
     [TargetDepartmentId], [TargetRoleName], [TargetEmployeeId],
     [BlocksWorkUntilApproved], [IsActive])
SELECT
    rt.[RequestTypeId], @ReopenApproval, @TargetKindRole,
    NULL, @ApproverRole, NULL,
    @BlocksWork, 1
FROM [RequestTypes] rt
WHERE rt.[IsActive]    = 1
  AND rt.[AllowReopen] = 1
  AND NOT EXISTS (
        SELECT 1
        FROM [RequestTypeApprovalRequirements] existing
        WHERE existing.[RequestTypeId] = rt.[RequestTypeId]
          AND existing.[ApprovalType]  = @ReopenApproval);

SET @Inserted = @@ROWCOUNT;

COMMIT TRANSACTION;

PRINT CONCAT('ReopenApproval requirements inserted: ', @Inserted);

-------------------------------------------------------------------------------
-- 3. Verification — Department / Request Type / AllowReopen / ReopenApproval
--    target / active state, for every request type in the database.
-------------------------------------------------------------------------------

PRINT '=== AFTER: verification ===';

SELECT
    [Department]              = d.[Code] + N' — ' + d.[Name],
    [RequestType]             = rt.[Name],
    [RequestTypeActive]       = CASE WHEN rt.[IsActive] = 1 THEN N'Active' ELSE N'Inactive' END,
    [AllowReopen]             = CASE WHEN rt.[AllowReopen] = 1 THEN N'Yes' ELSE N'No' END,
    [ReopenApprovalTarget]    = CASE
                                    WHEN reopen.[RequestTypeApprovalRequirementId] IS NULL THEN N'(not configured)'
                                    WHEN reopen.[TargetKind] = 2 THEN N'Role → ' + reopen.[TargetRoleName]
                                    WHEN reopen.[TargetKind] = 1 THEN N'Department → ' + ISNULL(td.[Name], N'?')
                                    WHEN reopen.[TargetKind] = 3 THEN N'Employee → ' + CONVERT(nvarchar(36), reopen.[TargetEmployeeId])
                                    ELSE N'(unknown target kind)'
                                END,
    [ReopenApprovalState]     = CASE
                                    WHEN reopen.[RequestTypeApprovalRequirementId] IS NULL THEN N'—'
                                    WHEN reopen.[IsActive] = 1 THEN N'Active'
                                    ELSE N'Inactive'
                                END,
    [ReopenApprovalBlocksWork] = CASE
                                    WHEN reopen.[RequestTypeApprovalRequirementId] IS NULL THEN N'—'
                                    WHEN reopen.[BlocksWorkUntilApproved] = 1 THEN N'Yes'
                                    ELSE N'No'
                                 END,
    [OtherApprovals]          = ISNULL(STUFF((
        SELECT N', ' + CASE r.[ApprovalType]
                           WHEN 1 THEN N'AccountingApproval'
                           WHEN 2 THEN N'CustomerServiceApproval'
                           ELSE CONCAT(N'ApprovalType#', r.[ApprovalType])
                       END
        FROM [RequestTypeApprovalRequirements] r
        WHERE r.[RequestTypeId] = rt.[RequestTypeId]
          AND r.[ApprovalType] <> @ReopenApproval
        ORDER BY r.[ApprovalType]
        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''), N'(none)')
FROM [RequestTypes] rt
INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
LEFT JOIN [RequestTypeApprovalRequirements] reopen
       ON reopen.[RequestTypeId] = rt.[RequestTypeId]
      AND reopen.[ApprovalType]  = @ReopenApproval
LEFT JOIN [Departments] td ON td.[DepartmentId] = reopen.[TargetDepartmentId]
ORDER BY d.[Code], rt.[Name];

-- Anything eligible but still unconfigured is a genuine problem: the only way
-- to reach this state is a pre-existing (likely deactivated) ReopenApproval
-- row occupying the unique slot, which this script deliberately will not
-- overwrite. Reported, never silently fixed.
PRINT '=== Eligible but NOT configured (inspect before re-running) ===';

SELECT
    [Department]   = d.[Code],
    [RequestType]  = rt.[Name],
    [Reason]       = CASE
                        WHEN reopen.[RequestTypeApprovalRequirementId] IS NOT NULL AND reopen.[IsActive] = 0
                            THEN N'A DEACTIVATED ReopenApproval row already exists — left untouched; reactivate it deliberately if intended.'
                        ELSE N'Unexpected — investigate.'
                     END
FROM [RequestTypes] rt
INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
LEFT JOIN [RequestTypeApprovalRequirements] reopen
       ON reopen.[RequestTypeId] = rt.[RequestTypeId]
      AND reopen.[ApprovalType]  = @ReopenApproval
WHERE rt.[IsActive]    = 1
  AND rt.[AllowReopen] = 1
  AND (reopen.[RequestTypeApprovalRequirementId] IS NULL OR reopen.[IsActive] = 0)
ORDER BY d.[Code], rt.[Name];

-------------------------------------------------------------------------------
-- 4. Baseline reconciliation. The approved baseline is 13 request types; this
--    reports any that this environment does NOT have (renamed, moved, removed
--    or never seeded). Purely informational — the script acts on the predicate
--    above, not on this list.
-------------------------------------------------------------------------------

PRINT '=== Approved baseline rows NOT found in this environment ===';

SELECT [Department] = baseline.[DepartmentCode], [RequestType] = baseline.[RequestTypeName]
FROM (VALUES
        (N'CS',  N'NOC for Resale'),
        (N'CS',  N'NOC for Handover'),
        (N'CS',  N'NOC for Mortgage'),
        (N'CS',  N'NOC for Golden Visa'),
        (N'CS',  N'Complaint Handling'),
        (N'CS',  N'Ticketing System'),
        (N'CS',  N'E-mail'),
        (N'COL', N'E-mail'),
        (N'COL', N'Ticketing System'),
        (N'COL', N'Send Receipts'),
        (N'REG', N'Send SPA Link'),
        (N'REG', N'Register Unit'),
        (N'HO',  N'Handover Request')
     ) AS baseline([DepartmentCode], [RequestTypeName])
WHERE NOT EXISTS (
        SELECT 1
        FROM [RequestTypes] rt
        INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
        WHERE d.[Code] = baseline.[DepartmentCode]
          AND rt.[Name] = baseline.[RequestTypeName])
ORDER BY baseline.[DepartmentCode], baseline.[RequestTypeName];

PRINT '=== Configured BEYOND the approved baseline (environment-specific request types) ===';

SELECT [Department] = d.[Code], [RequestType] = rt.[Name]
FROM [RequestTypes] rt
INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
INNER JOIN [RequestTypeApprovalRequirements] reopen
        ON reopen.[RequestTypeId] = rt.[RequestTypeId]
       AND reopen.[ApprovalType]  = @ReopenApproval
WHERE NOT EXISTS (
        SELECT 1
        FROM (VALUES
                (N'CS',  N'NOC for Resale'),
                (N'CS',  N'NOC for Handover'),
                (N'CS',  N'NOC for Mortgage'),
                (N'CS',  N'NOC for Golden Visa'),
                (N'CS',  N'Complaint Handling'),
                (N'CS',  N'Ticketing System'),
                (N'CS',  N'E-mail'),
                (N'COL', N'E-mail'),
                (N'COL', N'Ticketing System'),
                (N'COL', N'Send Receipts'),
                (N'REG', N'Send SPA Link'),
                (N'REG', N'Register Unit'),
                (N'HO',  N'Handover Request')
             ) AS baseline([DepartmentCode], [RequestTypeName])
        WHERE baseline.[DepartmentCode] = d.[Code]
          AND baseline.[RequestTypeName] = rt.[Name])
ORDER BY d.[Code], rt.[Name];

PRINT '=== Untouched approvals (must be unchanged by this script) ===';

SELECT
    [Department]   = d.[Code],
    [RequestType]  = rt.[Name],
    [ApprovalType] = CASE r.[ApprovalType]
                         WHEN 1 THEN N'AccountingApproval'
                         WHEN 2 THEN N'CustomerServiceApproval'
                         ELSE CONCAT(N'ApprovalType#', r.[ApprovalType])
                     END,
    [Target]       = CASE r.[TargetKind]
                         WHEN 1 THEN N'Department → ' + ISNULL(td.[Name], N'?')
                         WHEN 2 THEN N'Role → ' + r.[TargetRoleName]
                         WHEN 3 THEN N'Employee'
                         ELSE N'?'
                     END,
    [BlocksWork]   = CASE WHEN r.[BlocksWorkUntilApproved] = 1 THEN N'Yes' ELSE N'No' END,
    [State]        = CASE WHEN r.[IsActive] = 1 THEN N'Active' ELSE N'Inactive' END
FROM [RequestTypeApprovalRequirements] r
INNER JOIN [RequestTypes] rt ON rt.[RequestTypeId] = r.[RequestTypeId]
INNER JOIN [Departments] d ON d.[DepartmentId] = rt.[DepartmentId]
LEFT JOIN [Departments] td ON td.[DepartmentId] = r.[TargetDepartmentId]
WHERE r.[ApprovalType] <> @ReopenApproval
ORDER BY d.[Code], rt.[Name], r.[ApprovalType];
GO
