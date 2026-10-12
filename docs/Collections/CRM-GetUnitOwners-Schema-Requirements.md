# CRM `GetUnitOwners` - confirmed schema (no further schema is needed)

The CRM owners implemented `GetUnitOwners` and it was tested successfully (`success=true`, `total=10086`). This page records the confirmed mapping; **no schema information is outstanding**.

| Response field | CRM source |
|---|---|
| `fullNameEnglish`, `fullNameArabic`, `mobileNumber`, `email` | `tblCustomers.FullNameEnglish`, `FullNameArabic`, `MobileNumber`, `Email` |
| `customerId`, `leadId`, `unitId` | `tblLeadCustomers.CustomerID`, `LeadID`, `UnitID` |
| `unitNumber`, project link | `tblUnits.Number`, `tblUnits.ProjectID` |
| `projectCode` / `projectName` | `tblProjects.Code` / `tblProjects.Name` |
| `leadStatus` | Sold = 8, Contract = 9 (eligible); Approved = 4, Cancelled = 6 (never owners) |
| `customerType` | 1 = Buyer |

Ticketing side: `CollectionsSource:CrmOwners:EligibleLeadStatuses = [8, 9]` (fallback for a row without a status name), `BuyerCustomerType = 1`. Response contract: `CRM-GetUnitOwners-Contract.md`. Verification after deployment: `database/collections-receivables/tools/verify_crm_owner_ingestion.sql`.
