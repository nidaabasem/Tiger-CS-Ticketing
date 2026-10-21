# Collections Campaigns: Approve & Send (one page)

Select Customers -> Approve & Send -> Genesys + Admin Email, on **Collections Campaigns**. The Receivables page stays the place for financial detail.

## Flow
1. **Filter and preview** as before (stage, preview date, project / tower, Due / Overdue, dates, minimum total, search). Due, Overdue, Total and unit columns are unchanged.
2. **Select.** Tick individual *Ready* customers (ticks survive paging), or choose *All eligible customers matching the filters* (every page).
3. **Approve & Send to Genesys.** The page re-resolves the exact selection against the stored, validated copy of the financial snapshot (the *approval data*). If that data is
   missing, failed or stale, a refresh starts and its progress (or its failure with a **Retry** button) is shown on the same page; nothing is approved from a guess.
4. **Confirm.** Shows the exact customer count, total amount, campaign stage and destination Genesys contact list. Confirming creates the approval record (who, when, exact list and
   fingerprint) and queues the job. Idempotency key + dispatch history make a second click or a second approval of the same customers a no-op.
5. **Send.** The job re-reads current balances (paid customers dropped, any other change stops the send), then uploads batches of at most 1,000 contacts. A customer is only
   *uploaded* once Genesys confirms; an unconfirmed batch is never resent and must be reconciled.
6. **Result on the same page:** Success, Partial failure or Failure, per-batch Genesys results (contact list, campaign id, batch id), and the administrator-email status.

Nothing here bypasses financial reconciliation (`Collections:Campaigns:FinancialSourceValidated`), the dispatch restrictions, or `Collections:GenesysOutbound:LiveCustomerDispatchEnabled`
(which stays off until someone enables it deliberately).

## Retrying
* **Genesys:** rejected or released customers are free again; preview the list and press Approve & Send (customers Genesys already accepted show as already sent and are left out).
  Unconfirmed batches: reconcile first.
* **Refresh of the approval data:** the Retry button on the Campaigns page.
* **Administrator email:** *Retry administrator email* resends the report only. The Genesys upload is never repeated.

## Administrator email
After a send finishes (Completed or Completed with errors) the administrator(s) receive the approved customer list and the actual Genesys results: campaign name, approval time, approving
employee, customer count, amounts, uploaded / rejected / unconfirmed / excluded counts, and the Genesys batch references (contact list, campaign id, batch id). The attachment is a CSV;
phone numbers and emails are masked, formula-looking text is defused, the body carries totals only, and nothing sensitive is logged.

```json
"Collections": { "Campaigns": { "AdminNotification": {
  "Enabled": true,
  "Recipients": [ "admin1@example.com", "admin2@example.com" ],
  "AttachCustomerList": true,
  "AttachmentFormat": "Csv"
} } }
```
* Recipients live only in configuration: `appsettings.json`, `appsettings.UAT.json` (committed with the UAT recipient), or environment variables
  (`Collections__Campaigns__AdminNotification__Recipients__0=...`; one entry may also hold several addresses separated by `;` or `,`). Restart the API to pick up a change; no rebuild.
* Enabled with no valid recipient, an unsupported `AttachmentFormat`, or a mail provider that only records (`EmailNotifications:Provider = Recording`) is shown as **Not configured / Failed**, never as sent.
* The result is stored on the dispatch (`AdminEmailStatus`: None, Sending, Sent, Failed, NotConfigured, Disabled) apart from the Genesys status. A mail failure never changes or resends the Genesys batch.
* The UAT customer-delivery protections (`VerificationTesting`: email / SMS override, block real customers) apply to customer messages and are untouched; the administrator report is an internal
  message sent through the configured `EmailNotifications` provider (`Smtp` requires `EmailNotifications:Enabled`, host, `FromEmail`, and the password via secret/environment).

## Database
Migration `20261021120000_AddAdminEmailToCollectionsDispatch` (script `AddAdminEmailToCollectionsDispatch.sql` in the repository root) adds five columns to `CollectionsDispatches`.
