/*
  probe_pact_tenant_granularity.sql - READ-ONLY. Run on the EDSM reporting database (where p4tenantStat etc. live). Send back every grid.
  Question answered: is a PACT TenantId one UNIT or one CUSTOMER ACCOUNT, and does p{N}tenantStat return unit identity per row? This decides whether a unit-scoped Payment Summary
  can be built from EDSM at all (docs/Collections/EDSM_Collections_Contract.md section 2.3 marks the granularity UNVERIFIED).
*/
SET NOCOUNT ON;

/* 1. The definitions the contract asks for */
SELECT '1_definition' AS section, o.name, m.definition
FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.name IN (N'p4tenantStat', N'p32tenantStat', N'p7tenantStat', N'p25tenantStat', N'p20tenantStat', N'p4tenantphone2', N'p32tenantphone2');

/* 2. The rows p4tenantStat returns for ONE tenant (replace @tid with the tenant of BEN ISRAELI / the TP140-407 buyer; owned company 4 shown, use p32tenantStat for company 32).
      Look for a unit / contract / cost-centre column in the result: if it exists, a unit-scoped statement can be computed from these rows. */
DECLARE @tid nvarchar(50) = N'<TENANT-ID-FROM-v1/contracts/{mobile}>';
EXEC dbo.p4tenantStat @tid = @tid;

/* 3. For a customer with several units: how many distinct TenantIds does v1/contracts/{mobile} return? (If one per unit, the account IS the unit.) Compare with the contract list. */
