/*
    Exports the Request Type -> id mapping Genesys needs, for THIS environment's database.
    Read-only. Identifiers differ between environments - run it in each one and never copy ids across.

    UsableByGenesys = 1 only when the type is active, its owning department is active and its workflow has a Published version:
    exactly the conditions under which POST /api/genesys/tickets and PATCH /api/genesys/tickets/{id} accept it (otherwise 422).
*/
SELECT  rt.RequestTypeId,
        rt.Name              AS RequestTypeName,
        d.DepartmentId,
        d.Code               AS DepartmentCode,
        d.Name               AS DepartmentName,
        rt.DefaultPriorityId,
        rt.IsActive,
        CASE WHEN rt.IsActive = 1
              AND d.IsActive = 1
              AND EXISTS (SELECT 1 FROM dbo.WorkflowTemplates w
                          WHERE w.WorkflowId = rt.WorkflowId AND w.Status = 2)   -- 2 = Published
             THEN 1 ELSE 0 END AS UsableByGenesys
FROM    dbo.RequestTypes rt
JOIN    dbo.Departments  d ON d.DepartmentId = rt.DepartmentId
ORDER BY d.Name, rt.Name;
