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

The FAQ document was not available in this repository, so the items below
restate the legal stage only at the level the work request describes it.
Collections and Legal must supply the specifics.

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
