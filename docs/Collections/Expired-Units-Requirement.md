# Receivables: excluding expired units - NOT implemented (data source missing)

Status: **the Receivables page does not exclude expired units.** Only units with UnitID 0 or unit code `0`/blank are excluded. Do not treat the exclusion as done.

## What was checked
| Source in the project | Has contract end date / status? |
| --- | --- |
| `dbo.p4AccountReceivables` / `p32AccountReceivables` (deployed) and the reviewed `...V2` drafts (`docs/Collections/pact-sql`) | No. Output: CompanyID, [ProjectCode], UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status (+ PlanAmount, AllocatedAmount in V2). `Status` is the instalment status, not the contract's. |
| Local snapshot `dbo.CollectionsReceivableSnapshot` (V002) | No column for it. |
| PACT customer HTTP API `GET /v1/contracts/{mobile}` (`PactCustomerHttpGateway`, `PactContractRowHttpDto`) | **Yes**: `tenantID`, `companyID`, `unitID`, `unitCode`, `contractID`, `unitStatus`, `contractEndDate`. Rule already used elsewhere: expired = `contractEndDate` before today in Asia/Dubai, a missing/unreadable date counts as active (`PactContractActivity`). But it is one call per customer phone, so it must not run while a page loads. |
| EDSM `ContractSpEntity` (the stored procedure behind that API) | Its name and body are not in this repository. |

## What is needed
A bulk, local source keyed like the snapshot unit: `(CompanyId, TenantId, UnitId)` -> `ContractEndDate` (and optionally `UnitStatus`). Either:

1. **Preferred:** return it from PACT in the receivables refresh. Add `ContractEndDate date NULL` (and `UnitStatus`) to the output of `p4AccountReceivablesV2` / `p32AccountReceivablesV2` (join the contract table that `ContractSpEntity` reads; needs its SP/table definition from the PACT/EDSM owners), then:
   - `V002`: add the column to `dbo.CollectionsReceivableStaging` and `dbo.CollectionsReceivableSnapshot`;
   - `V005`: add the column to the INSERT...EXEC column layouts for the V2 shapes (5-6);
   - `V004`: copy it to the snapshot;
   - `V008`: add `AND (s.ContractEndDate IS NULL OR s.ContractEndDate >= @AsOfDate)` next to `s.UnitId > 0 ...` in both places (and the same in `V006`/`V007` if Campaigns should follow).
2. **Alternative:** a small table `dbo.CollectionsUnitContracts(CompanyId, TenantId, UnitId, ContractEndDate, UnitStatus, RefreshedUtc)` filled by a background job (from the same SP, in bulk), joined in `V008`.

Until one of these exists the rule cannot be applied without a per-customer API call.
