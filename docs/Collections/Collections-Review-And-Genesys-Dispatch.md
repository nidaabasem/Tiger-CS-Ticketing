# Collections review, approval and Genesys outbound dispatch

> **Later round:** UAT readiness, template/export findings, V2 deployment, paid-after-upload suppression, the repeated-call block and the live-dispatch gate are in [Collections-UAT-Readiness.md](Collections-UAT-Readiness.md). Where this page differs (the shared-phone acknowledgement no longer exists; dispatch needs `LiveCustomerDispatchEnabled` and `SuppressionEnabled`), that page is current.

Page: `/Collections/Review` (Web) · API: `/api/collections/review/*` · migration `AddCollectionsReviewAndGenesysDispatch`.
Nothing here uploads real customer data or activates a campaign by default: `Collections:GenesysOutbound:Enabled` is `false`, and no
credentials are committed.

## How it works

```
 PACT (read-only)        explicit job                  stored review data                 approval + job
 ───────────────  ──►  Refresh review data  ──►  CollectionsReviewRuns/Records  ──►  Confirm  ──►  Dispatch job ──► Genesys
                       (progress shown)           (filters, counts, paging in SQL)    (exact list)    (revalidate, batch, upload)
```

* **Refresh** (`POST /refresh`, Hangfire job): one read of the PACT report, validation of every unit × reminder type, bulk save, then
  publish as the *current* run. Single-flight (a second click joins the run in progress). A failed refresh keeps the previous data.
  Browsing, filtering, counting and paging **never call PACT**; they are SQL over the stored run.
* **Review** (`GET /records`): filters company, project, unit, customer name/phone, year-month, custom due range, payment status
  (default *Unpaid or partially paid*; All / Unpaid / Partially paid / Paid / Unknown), minimum remaining (default AED 100, editable) and
  optional maximum, reminder type, validation status, failed-validation reason. Counts Ready / Needs Review / Excluded / Already Sent
  follow every filter except the status choice. Each row shows source, last refresh time and reasons in plain language.
* **Select** (`POST /selection/summary`): *ticked records* (kept across pages) or *every Ready record matching the filters, on all
  pages*. Only **Ready** records can ever be selected. The summary shows the exact count, totals by currency, count per reminder type,
  the contact list (paged) and a fingerprint of the list.
* **Confirm** (`POST /dispatches`, needs the reminder-send grant): re-resolves the list; refuses if count/fingerprint differ from what was
  shown (`409 ReviewRequired`); requires an idempotency key and an acknowledgement that an active Genesys campaign may start dialling. Records sharing a phone, or one unit with several reminder types, are held back and cannot be approved. Persists the frozen list with the initiating employee and queues a job.
* **Dispatch job**: takes a lease; re-reads current balances; records paid since approval are **excluded**, any other change (amount, due date,
  contact, validation, month rollover, disabled integration) **stops the whole dispatch for review** and releases the records; then uploads one batch
  per reminder type, ≤ 1,000 contacts each, persisting every result.

### Vocabulary
`UploadedToGenesys` means the contact is in the contact list. The system never records a call or message as delivered or "contacted" from an
upload; call and message results are separate Genesys events (the existing outcome endpoint). An active campaign may call uploaded contacts at once.

## The three flags: causes found, and what changed

Diagnosed from the code, the PACT procedure review (`pact-sql/Receivables-Source-Review.md`) and the existing tests. **No real PACT data was
available in this environment**, so each cause is established by reasoning, not measured; the refresh logs how many float-noise amounts were
normalized so the first UAT run confirms or corrects it.

| Flag | Actual cause | Change |
| --- | --- | --- |
| `SourceReconciliationRequired` | Not a bug: `Collections:Campaigns:FinancialSourceValidated` is `false` because the deployed procedures have the proven defects D1–D8 (invoice fan-out, same-date double counting, float arithmetic …) and nobody has reconciled amounts against PACT accounts. | **Not cleared.** The sign-off is now bound to the procedure it covered (`ValidatedProcedureSuffix`): switching from the original procedures to V2 invalidates it. `ReconciliationReference` records the evidence. The review header states whether the source is reconciled. |
| `AmountPrecisionNeedsReview` | The check was `amount != Round(amount, 2)` on a value converted from a **float** column. A settled instalment can come out as 1E-12 (a "positive balance" with a precision flag), and float arithmetic leaves noise on others. | The reader records whether the column is floating point. Float values within AED 0.0001 of a fils amount are snapped (a remainder that snaps to 0 is a settled instalment, not a receivable). **Anything else with more than two decimals stays flagged**, is never rounded for display, carries no quoted amount (`RemainingAmount = null`) and keeps the raw source value. |
| `NoValidContact` | The phone normalizer only accepted `+…`, `00…`, `05…` (10 chars) and `971…` (12 chars), and counted an **email** as a valid contact although the campaign is voice. Rejected: `501234567` (leading zero lost), `971 0 50…`, numbers with `.` `/` `\`, `04…` landlines, `(0)`. | New `PhoneNormalizer` repairs those UAE forms, accepts valid international numbers, and **never guesses** between two different numbers in one field. A voice campaign needs a callable phone; an email alone no longer qualifies. |

## Validation rules (stored reasons)

Needs review: `SourceReconciliationRequired`, `AmountPrecisionNeedsReview`, `NoValidContact`, `PaymentStatusUnknown`, `StaleSource`,
`MissingUnitIdentity`, `MissingCustomerIdentity`, `UnitAllocationNeedsReview`, `AmbiguousInstalments`, `DuplicateSourceRecord`,
`ConflictingContactDetails`, `ContradictoryPaymentStatus`, `CurrencyNeedsReview`. Excluded: `OutsideSchedule`, `LegalNoticeReleaseRequired`,
`LegalCaseNotApproved`. Warning (does not block): `SharedPhoneMultipleUnits`. *Already Sent* comes from the dispatch table (a live record in a
pending, uploaded or unconfirmed dispatch).

* **Payment status** comes from source fields only. The deployed report returns `Paid`/`Installment`, which cannot separate unpaid from partially
  paid, so with the deployed procedures **every open balance is `Unknown`**: visible, never sendable. The reviewed V2 procedure also returns
  `PlanAmount`; remaining = plan → Unpaid, 0 < remaining < plan → PartiallyPaid. A due date never decides it. `SourceStatusMap` can only add a
  mapping that has been verified, and can never declare a balance paid.
* **Reminder type** follows the existing approved campaign policy unchanged (`CollectionsCampaignPolicy`): Overdue day 1, Current Month day 14,
  Follow Up day 28, Legal Notice days 12/14 (previous month's balance **strictly above AED 1,500**), Legal Case day 30 (balances older than three calendar
  months **strictly above AED 20,000**). Age alone never creates a Legal Notice/Case.
* **Money** is `decimal` end to end; `AmountDue` is formatted with the invariant culture, two decimals, no thousands separator.
* **Duplicates**: identical instalment rows (same voucher, cheque, date, amount) → `DuplicateSourceRecord`, never merged; different instalments on one
  date → `AmbiguousInstalments`. Re-sending is blocked by a unique index over live dispatch items.

## Multiple units / instalments per customer

One record per **unit × reminder type**; instalments inside a unit are summed only when no date is ambiguous; balances of different units are never
merged. If the same phone appears on several records that can be sent today, each is flagged and they are held back (Excluded) and cannot be approved.
**Missing business decision:** one call per unit (what is implemented), one consolidated call, or a priority (e.g. Legal Notice over Current Month, which
are both scheduled on day 14). Until decided, such records are blocked; no acknowledgement releases them.

## Genesys

* Token: `POST {LoginBaseUrl}/oauth/token`, `Authorization: Basic base64(ClientId:ClientSecret)`, `grant_type=client_credentials`, server-side only,
  cached until `expires_in − 60 s`, one shared request for concurrent callers, invalidated on `401` (one retry). Secrets/tokens are never logged or put in errors.
* Upload: `POST {ApiBaseUrl}/api/v2/outbound/contactlists/{id}/contacts`; body `[ { "contactListId": "<same id>", "data": { "Phone", "CustomerName",
  "Email Address", "ReminderType", "AmountDue", "DueDate" }, "callable": true } ]`, all data values strings, `DueDate` `yyyy-MM-dd`. One request = one list, ≤ 1,000 contacts.

| Reminder type | `ReminderType` text | Contact list id |
| --- | --- | --- |
| Current Month | Current Month | `79e5ae74-ea6e-4941-b76d-45ddf487d8d1` |
| Follow Up | Follow Up | `3a91c06e-47ab-4a5e-a720-004bd5cf5bba` |
| Legal Case | Legal Case | `41178d2a-af65-45ae-9a33-b50260dc1b9b` |
| Legal Notice | Legal Notice | `372d81d7-6d2d-4b9e-8f09-cf2262aecfdf` |
| Overdue | Overdue | `d5f4d808-2e12-410e-8fe6-b810cca1ef4c` |

* **Outcomes**: accepted (contact ids stored); *rejected* (4xx, 429, auth, could not connect: certainly nothing created); *unknown* (timeout, dropped
  connection, 5xx, worker died mid-request). An unknown batch is **never resent**: its records stay blocked, and a person records what they found in
  Genesys (`ConfirmedUploaded` / `ConfirmedNotUploaded`, with a note and their identity). The six `data` fields are fixed, so there is no correlation key
  to look contacts up automatically; reconciliation is manual by design.
* Partial failure: a rejected batch fails only its records (they return to Ready after the next refresh); other batches stand.
* Duplicate prevention: idempotency key (replay), unique index on live records, atomic batch claim, execution lease, check-before-send.

## Blockers and decisions still open

1. **The attached CSV files were not in the repository or session.** Nothing was validated against them; please supply them for UAT field-by-field checks.
2. **Payment status** cannot be confirmed with the deployed PACT procedures (see above). Deploy the reviewed V2 procedures (or supply a verified status mapping) before anything can be Ready.
3. **Source reconciliation** against real PACT accounts has not been done (needs PACT access); `FinancialSourceValidated` must stay `false` until it is.
4. **Legal Notice / Legal Case to customers** conflicts with `Collections-Legal-Requirements.md` (human approver, templates, delivery evidence). Both are blocked by
   `LegalNoticeExportEnabled` and `LegalCaseDispatchApproved` (default `false`). Needs a Legal/Collections decision.
5. **Multi-unit policy** (above) and **Day-14 overlap** (Current Month + Legal Notice) need a business decision.
6. **`ReminderType` text** follows the names in the contact-list table; the supplied templates were not available to verify. Override with `ReminderTypeLabels` if they differ.
7. **Suppression after upload** (a customer pays after the contact is uploaded and before a campaign dials) is not implemented; it needs the Genesys contact update API and a decision on
   whether campaigns run continuously. Until then, keep campaigns inactive or time uploads close to the dialling window.
8. Revalidation re-reads the company's PACT window (the procedures cannot filter by customer): about a minute per company, in the background job.

## Configuration

Non-secret keys are in `appsettings.json`; supply secrets by user-secrets/environment only.

| Key | Default | Notes |
| --- | --- | --- |
| `Collections:Enabled`, `CollectionsSource:PactReceivables:Enabled` | `true` / `false` | existing |
| `CollectionsSource:PactReceivables:ProcedureSuffix` | `""` | `V2` once the reviewed procedures are deployed |
| `Collections:Campaigns:FinancialSourceValidated` | `false` | only after reconciliation |
| `Collections:Campaigns:ValidatedProcedureSuffix` | `""` | procedure the reconciliation covered |
| `Collections:Campaigns:ReconciliationReference` | – | evidence reference |
| `Collections:Campaigns:LegalNoticeExportEnabled` / `LegalCaseDispatchApproved` | `false` / `false` | see blocker 4 |
| `Collections:Review:DefaultMinimumRemaining` | `100` | AED |
| `Collections:Review:MaxSelection` | `20000` | one approval |
| `Collections:Review:FloatNoiseTolerance` | `0.0001` | AED |
| `Collections:GenesysOutbound:Enabled` | `false` | master switch |
| `Collections:GenesysOutbound:LoginBaseUrl` / `ApiBaseUrl` | `https://login.mypurecloud.de` / `https://api.mypurecloud.de` | region |
| `Collections:GenesysOutbound:ClientId` | *secret* | `Collections__GenesysOutbound__ClientId=<client-id-placeholder>` |
| `Collections:GenesysOutbound:ClientSecret` | *secret* | `Collections__GenesysOutbound__ClientSecret=<client-secret-placeholder>` |
| `Collections:GenesysOutbound:ContactListIds:<ReminderType>` | table above | |
| `Collections:GenesysOutbound:ReminderTypeLabels:<ReminderType>` | – | optional text override |
| `Collections:GenesysOutbound:BatchSize` | `1000` | clamped to 1–1000 |
| `BackgroundJobs:Enabled` | `true` | with `false`, refresh and dispatch jobs stay *Queued* and are shown as such (cancel a queued dispatch to release its records) |

```
dotnet user-secrets set "Collections:GenesysOutbound:ClientId" "<client-id-placeholder>"   --project src/TigerCS.Api
dotnet user-secrets set "Collections:GenesysOutbound:ClientSecret" "<client-secret-placeholder>" --project src/TigerCS.Api
```

## Database

Migration `20261009065134_AddCollectionsReviewAndGenesysDispatch` adds `CollectionsReviewRuns`, `CollectionsReviewRecords`, `CollectionsDispatches`,
`CollectionsDispatchItems`, `CollectionsGenesysBatches` (only new tables). Idempotent script for SQL Server: `AddCollectionsReviewAndGenesysDispatch.sql` in the repository root
(guarded by `__EFMigrationsHistory`, safe to run twice). Or `dotnet ef database update --project src/TigerCS.Infrastructure`.
Important constraints: `UX_CollectionsDispatchItems_LiveRecord` (filtered unique), `UX_CollectionsDispatches_IdempotencyKey`, `UX_CollectionsReviewRuns_Active/Current`.

## Authorization

Browse/refresh: Collections financial-read grant. Select, confirm, cancel, reconcile: the reminder-send grant (CS Supervisor, CS Manager, Collections department members,
configured integration accounts, System Administrator override). Authorization runs before any data is read.

## UAT

1. With the committed defaults open *Collections → Campaigns → Review & approve*. Click **Refresh review data**: progress bar; the page keeps working; a second click joins the run.
2. Check source, last-refresh time, counts. Expect everything *Needs Review* (`SourceReconciliationRequired`, `PaymentStatusUnknown`) until V2 + reconciliation are in place. Filter by each reason.
3. In UAT only: deploy V2, run the read-only reconciliation SQL, set `FinancialSourceValidated=true`, `ValidatedProcedureSuffix=V2`, `ReconciliationReference`; refresh; Ready rows appear on schedule days.
4. Filter (company, project, unit, customer name / phone fragment, month, due range, payment status, min/max, reminder type, validation status, reason); compare counts with PACT.
5. Tick records on two pages, *Review selection*: exact count, totals by currency, contacts, shared-phone warning. Try *all matching filters*.
6. In UAT with `GenesysOutbound:Enabled=true`, a **test contact list and no active campaign**: confirm a handful; verify the contacts, field values, list routing and `callable`.
7. Mark a unit paid in PACT after approval: it is excluded at dispatch; change an amount instead: the whole dispatch stops for review.
8. Double-click Confirm; open the page in two tabs and confirm the same list: one dispatch only. Simulate a Genesys timeout: batch shows *Unconfirmed*; reconcile it.
9. Confirm that no row ever says "contacted" or "delivered".

## Verification

`dotnet build src/TigerCS.slnx -c Release` → 0 warnings, 0 errors. `dotnet test` → 3,931 passed, 0 failed (152 new: domain rules, validation, filters/paging,
dispatch/authorization/duplicates/concurrency on a real SQLite database, Genesys HTTP/token behaviour against a mocked handler, page rendering, endpoint authorization).
Not exercised: real PACT, real Genesys, SQL Server (the migration and filtered indexes were generated for SQL Server; tests run on SQLite).
