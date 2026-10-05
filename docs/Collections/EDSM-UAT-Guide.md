# EDSM Collections: UAT guide (financial reads only)

This guide covers the EDSM view on the Payment tab for PACT-identified
customers, against a **UAT** EDSM. It enables **reads only**: reminders,
outcome reporting and the scheduler stay off. The automated tests use fixture
responses built from `EDSM_Collections_Contract.md`. Nothing below has been run
against a real EDSM yet, and each step records that evidence.

## 1. Configuration (TigerCS.Api)

Set these keys in the UAT environment. Use environment variables, so `:`
becomes `__`. Keep the API key in the secret store, never in a file.

| Key | UAT value | Why |
|---|---|---|
| `Collections__Enabled` | `true` | Opens the Collections routes (financial-read is checked per caller) |
| `CollectionsSource__EdsmProvider` | `Pact` | Real EDSM routes on the PACT base URL |
| `CollectionsSource__EdsmNumberCulture` | *(blank first; see §2)* | Amounts are read only once this is verified |
| `Pact__Provider` | `Http` | Real PACT contracts lookup (account confirmation) |
| `PactApi__BaseUrl` | the UAT PACT/EDSM base URL | Shared by PACT and EDSM |
| `PactApi__ApiKey` | *(secret store)* | Sent as `X-API-KEY` |

**Leave these at their defaults (off):**

| Key | Value |
|---|---|
| `Collections__Channels__VoiceBotEnabled` / `SmsEnabled` / `EmailEnabled` | `false` |
| `Collections__SchedulerOwner` | `None` |
| `Collections__BusinessRulesConfirmed` | `false` |
| `Collections__Authorization__IntegrationEmployeeIds` | empty (no outcome reporting) |
| `CollectionsSource__Provider` | `Unavailable` (the per-account CRM path stays off) |
| `CollectionsSource__DueInstallmentsEnabled` | `false` at first. It is company-wide and unpaged; enable it only for step 4f |

**Optional:** `CollectionsSource__TransactionsEnabled` (default `true`),
`PactMappingTtlMinutes` (30) and `PactMappingNegativeTtlMinutes` (5).
Startup refuses `Fixture` outside Development/Testing.

## 2. Verify `EdsmNumberCulture` against real responses

1. With the culture blank, open a PACT customer's Payment tab. Every amount
   should read *"Not read (EDSM number format not configured)"*, and the
   response's `fields[].raw` holds EDSM's exact strings. You can see them in
   the API response for `GET /api/collections/customers/by-key/{key}/payment-summary`.
2. Pick an **owned** account whose paid amount is at least 1,000, and a
   **rented** account with a negative due amount (if one exists). From `raw`, note:
   - the **thousands separator** (`,` `.` space or none);
   - the **decimal separator** and that there are **exactly two decimals**;
   - the **negative sign** (`-500.00` vs `(500.00)` vs `500.00-`).
3. Open the Payments list for the same account and note the **date** format.
   The month abbreviation shows the culture: `15-Mar-2026` is English;
   anything else is not.
4. Choose the .NET culture that formats `#,##0.00` and `dd-MMM-yyyy`
   identically, e.g. `en-US` gives `1,234.50` and `15-Mar-2026`. Ask the EDSM
   host owner to confirm the IIS app-pool culture, and record it.
5. Set `CollectionsSource__EdsmNumberCulture` and reload. Pass when:
   - no field shows "Not readable";
   - the values match `raw` exactly;
   - the dates are parsed (not shown as raw text).

   Any "Not readable" means the culture is wrong. Revert to blank.

## 3. Account confirmation and mapping (before any figures)

- **Confirmed through PACT.** The tab shows *Accounts confirmed …* with a
  time. On the first load the response's `mappingSource` is `PactLookup`;
  reloads within 30 minutes show `Cached`, and no new PACT contracts call is
  made. Check the PACT/EDSM request logs.
- **Unknown tenant.** EDSM's 200-with-zeros never makes an account appear.
  A customer whose tenant PACT no longer returns shows **"PACT returned no
  contracts for tenant …"**, not zeros.
- **CRM customers.** A customer identified by Tiger CRM (`crm:{id}`) shows
  **NotMapped**: *"identified by Tiger CRM … no verified mapping from a CRM
  customer to a PACT tenant exists."* No PACT or EDSM call is made. This is
  expected until a verified crosswalk exists.

## 4. Expected checks per account type

For each case, compare the tab against EDSM/PACT's own screens or the
Collections team's figures. Record the customer key, company, tenant and
retrieval time.

| # | Case | Expected on the tab |
|---|---|---|
| a | **Owned** (company 4 or 32) | Labels: Paid, Due, **Not yet due**, Late fines, Total; Total = Paid + Due + Not yet due (**late fines excluded**), with no "total does not add up" note; a blank late fine shows **"None above zero"** |
| b | **Rented** (company 25, 7 or 20) | Labels: Paid, Due, **Post-dated cheques**, Late fines, Total; late fines **"Not computed for rented companies"**; Due may be **negative**; the Payments and Due lists carry the refund and fee caveats |
| c | **Partial payment** (one instalment part-paid) | Owned Due / Not yet due show the **unpaid remainder** (Debit − Credit) of that instalment. Check against PACT. If a fully unpaid instalment is missing from Due, that is the UNVERIFIED "no credit recorded" exclusion (contract §3.4): report it with the instalment |
| d | **Multi-company** tenant | One block per company, each listing only that company's PACT contracts; figures are not combined across companies |
| e | **Parking** unit | Listed under its company with "(Parking)"; the figures come from the row's `tenantID`, never the Parking ContractID. Confirm the summary matches EDSM for that tenant/company |
| f | **Due-installments** (enable for this step only) | Rows only for this tenant and company, in the window; status shown raw; company 20 shows *"does not support"* with no call made. Do not treat any row as unpaid |
| g | **Freshness** | Post a test receipt in PACT UAT. Expect it to appear within about 20 minutes, not immediately; reloading does not speed it up. Record the actual delay (this confirms the deployed `CacheDuration`) |
| h | **Errors** | A wrong API key gives *"EDSM rejected TigerCS's credentials"* for that company and no figures; with EDSM stopped, *"EDSM is unavailable"* |

## 5. Sign-off record

Record the following for each case:
- the customer key;
- PASS or FAIL;
- the values seen next to the values expected;
- the screenshot reference.

Also record:
- the confirmed culture;
- the observed cache delay;
- whether EDSM's figures cover exactly the listed contracts.

Reminders stay disabled after UAT. Enabling them needs the fresh-data mechanism
in `Collections-Integration.md` §2.2.
