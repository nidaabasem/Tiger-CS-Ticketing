# Customer notification mode and satisfaction reviews

Builds on `docs/Customer-Email-Notifications.md` (same Outbox → handler → `ICustomerEmailSender` pipeline; no second mechanism).

## Configuration (`CustomerNotifications`)
| Key | Default | Notes |
|---|---|---|
| `Mode` | `Test` (shipped) | `Test` or `Production`. Missing/invalid ⇒ no customer message is sent. |
| `TestEmail` | empty | Test mode only. Missing/invalid ⇒ nothing sent (never falls back to the customer). |
| `PublicBaseUrl` | empty | Public Ticketing **Web** URL; used for review links and the logo. Empty ⇒ emails carry no review section. |
| `ReviewLinkLifetimeDays` | 14 | |
| `Branding:LogoUrl / WebsiteUrl / ContactEmail / ContactPhone` | logo = `{PublicBaseUrl}/img/tiger-logo.png`, site `https://tigergroup.ae`, contact blank | Blank contact lines are omitted. |

`EmailNotifications:Enabled` stays the master switch (default `false`).

## Behaviour
- **Test**: Created/Reopened and all other customer email/SMS suppressed; Resolved/Closed go to `TestEmail` only, subject `[TEST] …`, banner in body; test reviews flagged `IsTest` and excluded from production figures. Staff mail unaffected.
- **Production**: unchanged routing; Resolved/Closed now use the new template and carry the review invitation.
- Enforcement is at the final boundary: `IEmailSender`/`ISmsSender` are wrapped by `CustomerDeliveryGuard*Sender`. `EmailMessage.Audience` defaults to `Customer`, so any caller that forgets to classify is guarded. OTP/document mail redirected by `VerificationTesting` is allowed only to that configured inbox/number.

## Reviews
Tables `TicketReviewInvitations` (SHA-256 of a random 256-bit token, expiry, ticket + reopen cycle, `IsTest`) and `TicketReviews` (1–5, comment ≤1000, unique per ticket cycle + IsTest). Page `GET /Review?t=&r=` only shows the form; the review is saved by the antiforgery-protected POST. Anonymous API: `POST /api/public/ticket-reviews/lookup|submit` (token in body). Closed email skips the invitation if a review exists.

## Deploy
1. Apply `AddCustomerSatisfactionReviews.sql` (migration `20261022081031_AddCustomerSatisfactionReviews`).
2. Set per environment: `CustomerNotifications__Mode`, `__TestEmail` (UAT), `__PublicBaseUrl`, optional `__Branding__ContactEmail`.
3. Production customer delivery stays off until `Mode=Production` **and** `EmailNotifications__Enabled=true` are set deliberately.

Previews: `docs/email-previews/`.
