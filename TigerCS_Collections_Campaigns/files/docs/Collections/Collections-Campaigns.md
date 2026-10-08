# Collections campaigns preview and CSV export

Collections staff can preview one communication stage per apartment at `/Collections/Campaigns`, reached from
`/Collections/Receivables` through the Campaigns link. The page uses the direct PACT receivables source for companies 4 and 32.
It does not add CRM or other-company coverage. It creates no campaign, sends no message, schedules no job, creates no ticket,
and opens no legal case. The existing reminder dispatcher and its rule configuration are unchanged.

## Confirmed policy decisions

The user selected the **schedule table** in Collections Communication Script Final, dated 7 October 2026, and **per-unit**
thresholds on 8 October 2026. These choices resolve the table/body date differences for this preview.

| Stage | Schedule in Asia/Dubai | Qualifying remaining principal per unit |
| --- | --- | --- |
| OverdueReminder | Day 1 | Instalments due strictly before the preview date minus one calendar month |
| CurrentMonthReminder | Day 14 | Positive remaining balances with due dates anywhere in the preview month, including upcoming dates |
| FollowUpReminder | Day 28 | Positive remaining balances with due dates anywhere in the preview month |
| LegalNotice | Days 12 and 14 | Previous calendar month's remaining balances, summing to strictly more than AED 1,500 |
| LegalReferral | Day 30, or the last day in February | Remaining balances due strictly more than three calendar months before the preview date, summing to strictly more than AED 20,000 |

Settled rows are removed before evaluating eligibility. Partly paid rows contribute only their reported remaining amount.
Calendar months, rather than fixed 30/90 days, are the implementation convention; referral thresholds apply to the qualifying
aged balance itself, rather than adding newer arrears to an old debt. Fees and penalties are not added. Confirm these amount
conventions against Collections' records during UAT before releasing campaign exports.

A selected past or future preview date changes eligibility against **live** remaining balances; it is not a historical
statement and cannot authorize a Genesys export. Both CurrentMonthReminder and LegalNotice can qualify on day 14; the preview
does not choose a priority or coordinate attempts across stages. Campaign coordination remains part of the Genesys integration.

## Data validation and export modes

Preview requires the existing Collections financial-read grant. Both export modes require the existing reminder-send grant
(CS Supervisor, CS Manager, members holding a configured Collections department role, configured integration accounts,
or the System Administrator central override). Authorization runs before external reads.

* **Review CSV** exports all matching records across pages, with their status/reasons and `Use=InternalReviewOnly`.
  Channel eligibility flags are false. This is a staff review file, not a customer campaign file. Spreadsheet formula
  prefixes are neutralized, including the leading plus sign in phone numbers. Do not import this file into Genesys.
* **Genesys CSV** re-reads PACT and requires every matching record to be Ready, the selected date to be today in Dubai and
  scheduled for the chosen stage, and at least one record. It refuses the whole export if a selected record needs review;
  it does not silently omit unresolved units. `LegalReferral` can never be exported for a customer campaign.
* Exports are bounded by `Collections:Campaigns:MaxExportRows` (default 5,000). Exceeding the limit returns an explicit error,
  with no truncated file. Filter by company or customer to reduce the selection.

`Collections:Enabled=true` and `CollectionsSource:PactReceivables:Enabled=true` expose preview/review using the existing
PACTRPT configuration. Two additional switches default to false:

```json
{
  "Collections": {
    "Campaigns": {
      "FinancialSourceValidated": false,
      "LegalNoticeExportEnabled": false,
      "MaxExportRows": 5000
    }
  }
}
```

`FinancialSourceValidated` must stay false until **remaining amounts and unit allocation** have been reconciled against
actual PACT accounts, including multi-unit customers and partial payments. The original procedures have proven invoice
fan-out and allocation defects, described in `pact-sql/Receivables-Source-Review.md`. Selecting a procedure named V2 does not
establish that reconciliation passed. Ordinary campaign exports become available only after the source is reconciled.
`LegalNoticeExportEnabled` additionally controls release of client-facing legal-notice files.

Even with source validation enabled, rows remain NeedsReview for ambiguous same-date instalments, allocations appearing under
different units of the same customer/date, conflicting unit ids/codes or contact details, invalid/zero unit identity,
positive balances labelled Paid, amounts with more than two decimal places, a non-AED currency, stale/invalid read timestamps,
or no valid phone/email. Same-date rows across units may be legitimate; they are held for review because this source contract
does not expose enough instalment identity to prove that. The page preserves these records instead of deduplicating or guessing.

## API and CSV contract

These are **internal TigerCS API routes**, called server-side by Web as the signed-in user. They are not yet published through
TigerGroupWeb's `/api/genesys` proxy.

```text
GET /api/collections/campaigns/preview?stage=CurrentMonthReminder&businessDate=2026-10-14&companyId=4&page=1&pageSize=25
GET /api/collections/campaigns/export?stage=CurrentMonthReminder&businessDate=2026-10-14&companyId=4&mode=review
```

`businessDate`, `companyId` and `search` are optional. The default date is today in Dubai. Stage names are required and numeric
enum values are rejected. Preview pages accept 1–100 records; company filters accept only 4 or 32. Search is limited to 200
characters. Export modes are `review` and `genesys`; the API returns `{ fileName, csv, rowCount }`. Web serves the CSV as a
UTF-8 attachment with BOM. Financial responses and downloads set `Cache-Control: no-store`.

| CSV columns | Meaning |
| --- | --- |
| RecordId | SHA-256 of company, tenant, unit id, unit code and monthly stage cycle; stable across re-reads in that cycle |
| CustomerKey, CompanyId, TenantId | `ext:Pact:{tenantId}` plus company id; a tenant id alone is not an account identity |
| CustomerName, Phone, Email | Name and validated contacts; Genesys mode preserves E.164 phone strings |
| UnitId, UnitCode, ProjectCode | Source unit/project identifiers; ProjectCode is not necessarily a project name |
| Amount, Currency, DueDate | Qualifying remaining principal, AED, and earliest qualifying due date |
| Stage, CycleKey, ReadAtUtc | Communication stage, monthly cycle, and latest report-read timestamp |
| Status, Reason, Use | Ready/review/preview/internal state, reasons, and intended use of the export |
| VoiceEligible, SmsEligible, EmailEligible | Custom campaign columns to map/filter in Genesys; not claims that Genesys configured contactability |

Numeric amount strings have at least two decimal places with no thousands separators for machine parsing; the display uses
thousands separators. Preview may show source precision for review but cannot export it as Ready. If a unit has several
qualifying due dates, Amount is their sum and DueDate is the earliest; the bot must not describe the sum as one instalment due
on that date. Project names and per-instalment due-date narratives require additional mapping/data before using every template.

For Genesys release, agree the contact-list column mapping, contactListIds, authentication/region, update/upsert semantics,
contactability per channel, handling of several units with the same phone, and priority/attempt limits across overlapping stages.
RecordId is an external stable key, **not** a Genesys-generated contact id and **not** durable delivery deduplication.
Use=InternalReviewOnly and eligibility=false must not be overridden into a live campaign.

## Next integration stage

The proposed integration will synchronize records to Genesys, suppress/update them after payment and return channel results.
The CSV read verifies payment state at export time only. It cannot cancel a queued contact after a later payment; revalidation
before contact and suppression in Genesys must be implemented before automated live campaigns. A CSV download is not recorded
as a sent reminder, and downloading it twice does not prevent two manual imports.

Response tickets must be linked to the customer/unit/campaign and routed to Collections only on actual customer response,
using the existing ingestion/outcome workflow where appropriate. The new preview/export does not create them.

## UAT checks

1. Open Campaigns as a Collections sender and verify preview plus review export; a financial-read-only agent can preview but
   cannot export. Anonymous and Reporting User calls must fail before PACT is read.
2. Verify two different units of one customer and the same tenant id in companies 4 and 32 stay separate.
3. Reconcile a partial payment, a settled unit, old arrears and upcoming month instalments against PACT. Confirm exact
   one-/three-month age boundaries and exact AED 1,500/20,000 balances do not meet strict thresholds.
4. Verify ambiguous allocations, unit 0 and stale reads are visible for review and cannot enter a campaign file.
5. Download a multi-page review, pay/remove an account before a second export, and confirm the fresh file excludes it.
6. Confirm a February referral uses the last day, follow-up stays on day 28, and LegalReferral remains internal.
7. Only after source reconciliation, test Genesys export on an actual scheduled Dubai date; unresolved rows, different dates,
   source failures and over-limit selections must fail explicitly rather than produce a partial campaign.
