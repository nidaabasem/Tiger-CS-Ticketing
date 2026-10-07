# Next payment via the chatbot — investigation (separate from the other items)

**Status: root cause is established from code and shipped configuration; it is NOT yet validated against the reported
failing conversation** — the original symptom report, a conversation id or Genesys flow log was not available in this
session. Treat it as the leading explanation until reproduced on UAT.

## What the code does

* The only route that returns a **next payment** is `GET /api/genesys/collections/customers/{crmCustomerId}/outstanding`
  (`nextPayment: { instalmentId, dueDate, remainingAmount }`, from `AccountBalanceCalculator`, earliest unpaid instalment due today or later).
* That route reads `ICollectionsFinancialSource`. Shipped `appsettings.json` sets `CollectionsSource:Provider = "Unavailable"`
  — "the only value for real environments today" — so in UAT the route answers **503 `FinanceUnavailable`**, never a next payment.
* The route the chatbot can actually call for real figures is the EDSM **payment summary**
  (`…/customers/by-key/{customerKey}/payment-summary`, data action `08`). It exposes paid/due/outstanding/total only —
  **no due dates and no next-payment field** — and the Genesys data actions shipped are 08 and 09 (summary, transactions);
  there is no data action for `/outstanding`.
* EDSM `due-installments` (the only EDSM source of dates) ships **off** (`DueInstallmentsEnabled:false`), is windowed to ±31 days,
  and its paid/unpaid status is documented as UNVERIFIED, so no next payment may be inferred from it
  (`Collections-Integration.md` §2).
* CRM-identified customers answer `NotMapped` on the EDSM routes (PACT identity only).

## Root cause (leading)

There is no live data source behind "next payment" in UAT, and no chatbot-facing contract that exposes one: the figure exists
only on a route backed by a source that is deliberately switched to `Unavailable`. A bot asked "when is my next payment?"
can only get a 503, or the summary, which has no date.

## What would fix it (decisions needed, none made here)

1. A real `ICollectionsFinancialSource` (the authoritative instalment schedule), **or** EDSM confirming unpaid-status semantics so
   due-installments can be trusted and enabled.
2. A Genesys data action for `/outstanding` (or a dedicated `next-payment` field) and bot wording for the 503/NotMapped cases.

## Validation done

Code and configuration read only; the existing Collections tests exercise `AccountBalanceCalculator`'s next-payment selection
against fixtures. **Not done:** reproduction on UAT, and confirmation with the reporter that this is the failing path.
