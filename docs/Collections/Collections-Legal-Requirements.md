# Collections: legal notices and legal referrals (requirements only)

The Collections FAQ (Genesys, 29-09-2026) describes a legal stage after
ordinary payment reminders: a formal legal notice, and referral of the account
to Legal. This document records that stage as **requirements to confirm**.
**Nothing in TigerCS implements or can trigger it.**

## Why it is separate from reminder dispatch

- A legal notice has contractual and regulatory consequences (UAE property and
  escrow regulation, the sale and purchase agreement's default clauses). It is
  not a "louder reminder".
- Ordinary reminders are automated, revalidated against the source, and sent by
  voice bot, SMS or email. A legal step needs human review and an approver, and
  often a prescribed form and delivery method (registered courier, notary, the
  Oqood/DLD process).
- So, in code: `ReminderType` has **no legal member** (enforced by
  `CollectionsReminderDomainTests.ThereIsNoLegalReminderType`).
  `POST /api/genesys/collections/reminders` rejects any other type with `400`.
  The scheduler, the candidates list and the voice bot can never produce one.

## The legal stage as the specification states it

`TigerCS_Collections_API_Specification.md` (§ legal stage, from the FAQ)
lists two steps. It marks them as *internal workflow requirements from the
supplied document, not independently verified legal requirements*:

| Step | Window | Condition | Channel / owner |
|---|---|---|---|
| Legal notice without action | 12th to 14th | Previous month's outstanding amount exceeding AED 1,500 | Call and email |
| New legal case referral | 28th to 30th | Outstanding amount exceeding AED 20,000 **and** overdue more than three months | Legal department |

**Neither is implemented.** The specification itself requires a separately
approved workflow, templates and permissions, and says an ordinary reminder
request must not open a legal case or imply management approval. Points the
specification leaves open:

- whether "outstanding" means principal only or includes penalties and fees,
  and as of which date (these figures would come from EDSM, which is not yet
  connected);
- how the 28th–30th window behaves in February (no 29th/30th, and no 30th in
  leap years);
- whether "overdue more than three months" uses calendar months (like the
  ordinary overdue reminder) or a day count;
- whether "notice without action" is a scripted call by the voice bot or by a
  person, and who approves the email.

## Requirements to confirm before anything is built

1. **Trigger:** what makes an account eligible for a legal notice (months
   overdue, amount threshold, number of ignored reminders, a broken promise to
   pay), and who decides (Collections Head, Legal, or both).
2. **Approval:** a human approval step before any notice is issued. The
   existing approval workflow (`TicketApprovals`, Accounting/Department
   approvals) is the natural vehicle: a *Legal Notice* request type in the
   Collections department with a Legal approval requirement.
3. **Content and form:** the approved template(s), language (Arabic/English),
   the amounts it states (principal, fines, fees, as of when), and whether the
   figures must come from a source-issued statement of account (a verified
   document API, which does not exist today).
4. **Delivery and evidence:** the legally valid delivery channel(s) and the
   proof of delivery TigerCS must retain, plus the retention period (see
   Executive Decision ISSUE-016, 7-year interim retention).
5. **Referral to Legal:** what a referral hands over (account, schedule,
   payment and reminder history, call recordings/transcripts, customer
   responses), to which team or queue, and how its outcome comes back.
6. **Suppression:** whether ordinary reminders stop once an account is in the
   legal stage, and whether a payment or a promise to pay reverses it.
7. **Customer contact:** whether the voice bot may still call an account in
   the legal stage, and what it may say.
8. **Audit:** every legal step needs an audit entry naming the approver; no
   step may be automatic.

## Proposed shape (not implemented)

A Collections-department **request type** ("Legal notice review"), created by
a Collections user from the Payment tab or the ticket, with a required Legal
approval. Only after approval would a separate, human-initiated action record
the notice as issued. It would reuse the reminder history and responses
already stored in `CollectionsReminders` / `CollectionsReminderEvents` as
evidence. No reminder code path would be extended to send it.
