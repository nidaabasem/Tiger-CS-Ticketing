# CRM `GetUnitOwners` - minimal schema requirements

Purpose: lets Receivables, Campaigns and the Payment tab show the CRM buyer's name, mobile and e-mail for a PACT unit (CRM first, PACT only completing). Full response contract: `CRM-GetUnitOwners-Contract.md`. Nothing below is guessed; the action is a single paged LINQ query once these are known.

## Already confirmed from the repository (no action needed)
| Fact | Source |
|---|---|
| Ownership = `tblLeadCustomers` (`LeadID`, `CustomerID`, `CustomerType` Buyer, `UnitID` long?) joined to `tblLeads` (`ID`, `Status`), `tblUnits` (`ID`, `Number`, `ProjectID`), `tblProjects` (`ID`, `Name`, `ArabicName`) | `GetBuyerByPhone` / `GetUnitDetails` insertion notes |
| `tblCustomers.MobileNumber` | `GetBuyerByPhone` (substring match on it) |
| The legacy EDSM exclusion query builds the PACT unit code in CRMDB as `TRIM(ProjectCode) + '-' + TRIM(UnitNumber)` from **`tblLead`** (`ProjectCode`, `UnitNumber`, `Status`; `Status = 6` cancelled) | `PactSqlReceivablesSource.ReadDownPaymentsAsync` |
| Auth header `X-SECRET-KEY` vs `AppSettings["TicketingSecretKey"]`, JSON 401/503 without a login redirect | `GetUnitDetails` insertion file |

## Required from CRM (only these)
1. **`tblCustomers`**: the property names holding the customer's **English name**, **Arabic name** and **e-mail** (`GetBuyerByPhone` already returns them as `fullNameEnglish`, `fullNameArabic`, `email`; the EF/column names behind them are not in this repository).
2. **`tblLead` / `tblLeads`**: confirm the EF property names for **`ProjectCode`** and **`UnitNumber`** (the raw table `tblLead` has both), and confirm they are the same values that make the PACT unit code (`TP140-407`).
3. **`tblLeads.Status` enum labels** for Sold and Contract (observed 8 = Sold, 4 = Contract in the contract notes; `6` = cancelled from the legacy query) so cancelled leads are never sent.
4. Optional: **`tblProjects`** column holding the `TP###` code, if different from `tblLead.ProjectCode`.

Send either the EF classes of those tables or the output of `docs/Genesys/crm-insertion/CRM-Schema-Discovery.sql` (section 1) and `database/collections-receivables/tools/probe_crm_pact_payment.sql` (section 3).

## Result
One row per (lead, buyer customer) for Sold/Contract leads: `leadId, leadStatus, leadStatusName, customerType, customerId, fullNameEnglish, fullNameArabic, mobileNumber, email, unitId, unitNumber, projectId, projectCode, projectName`, paged and ordered by `leadId`. A unit code containing `*` is never sent as an owner.
