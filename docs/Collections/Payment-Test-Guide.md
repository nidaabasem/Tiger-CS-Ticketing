# Testing Customer Details -> Payment against the CRM "Pact Payment" screen

What the tab shows for the selected unit (when the PACT statement is available): **Payment Summary** `Due | Outstanding | Paid | Fines | Total` and **Payment Details** `Date | Payment Status | Amount | Cheque Number` - every row the source returned (Paid, Due, Outstanding, future; partial payments stay separate rows). Paid is green, Due / Outstanding red. Figures keep PACT's meaning (owned companies: Outstanding = not yet due, empty fines = none; rented: Outstanding = post-dated cheques, fines not computed). A figure the source did not give reads "Unavailable", never 0. Payment Status is the list the source returned the row in (Paid / Due / Outstanding), never guessed from a date or amount.

## Before you test
1. Deploy the SQL in order: `V001 -> V003 -> V010 -> V011 -> R001 -> V012 -> V013 -> R002 -> R003 -> R004`, then the application. Let each company's snapshot refresh once (every two hours, or trigger it) so the unit/account directory (`CollectionsRcvAccountUnit`) is filled.
2. Configuration: `Collections:ReceivablesSnapshot:AccountDirectoryComplete` stays **false** unless PACT confirms that the directory lists every unit and contract of each account. `CollectionsSource:EdsmNumberCulture` (e.g. `en-US`) must be set, otherwise the figures are unreadable. `CollectionsSource:CrmOwners` is enabled, eligible statuses `[8, 9]`.
3. Pick a unit whose CRM Pact Payment screen you can open (e.g. lead 293168 -> Due 0.00, Outstanding 889,393.44, Paid 594,467.56, Fines 0, Total 1,483,861.00 - comparison values only).

## Steps
1. Open the customer in Customer Details, Payment tab, and select the unit (a customer with several units must pick one; none is chosen for you).
2. **Expect the statement** (green Paid, red Due/Outstanding) when the account is proven to hold only this unit. With the default configuration that proof is PACT's contract list for the customer's phone(s) answering completely and showing a single unit on the account. Compare, in the CRM Pact Payment screen of the same lead: Due, Outstanding, Paid, Fines, Total, then every payment row (date, status, amount, cheque number).
3. **Expect a notice instead of numbers** (never a total) when the scope is not proven. The status tells you exactly why:
   * `NotExclusive` - the account also covers other units (listed, including fully paid and cancelled `*` ones); EDSM has no unit parameter, so they cannot be separated.
   * `ScopeUnproven` - nothing shows other units, but nothing proves there are none (directory not accepted as complete, contract list unavailable). The notice names the missing PACT query.
   * `UnitNotFound`, `IdentityMismatch`, `Ambiguous`, `ContractEnded`, `SourceUnavailable`, `AmountsUnreadable`, `NotConfigured` - each with its own text.
4. Fully paid unit: it must still resolve (the directory holds it); the statement shows Paid rows only and no invented Due / Outstanding rows.
5. A unit whose PACT mobile is blank must resolve and, when the scope is proven, show the statement (the EDSM `Mobile` parameter is sent as `0` for owned companies - **unverified against real EDSM**; if EDSM rejects it you will see `SourceUnavailable` for those units).
6. A unit code containing `*` must never appear in the unit picker, Receivables, Campaigns, exports or Genesys lists.
7. Record, per unit tested: expected (CRM) vs shown, the status, and the notes under the summary. Send those back if anything differs.

## Remaining live-source limitations
* **Scope proof for units without a phone-proven account**: needs a PACT query listing every unit/contract of a (CompanyId, TenantId) account, fully paid ones included, without the customer's mobile. Until then `AccountDirectoryComplete` stays false.
* EDSM `Mobile=0`, TenantId granularity (`probe_pact_tenant_granularity.sql`), and the statement figures vs the CRM screen are unverified.
* CRM owner feed ingestion on the deployed system: verify with `GET /api/collections/crm-owners/status` and `tools/verify_crm_owner_ingestion.sql`.
