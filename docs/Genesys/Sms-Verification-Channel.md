# SMS as a verification channel (one-time code)

SMS is a **second delivery channel of the existing OTP flow** (`/api/genesys/verification/*`, `CustomerOtpAppService`). There is one
challenge model, one set of limits, one code hash, one verification path and one resulting session. A correct SMS code produces
the **same** OTP-verified `VerificationSession` an email code does — with `ProofChallengeId`, `CrmBuyerCustomerId`, `CrmBuyerLeadId` — so it
passes the unchanged `documents/send-copy` checks and releases the sale in `customers/unit-details`.

> **Verification status.** Everything here is verified **locally only**: unit tests, a real-SQLite persistence/race test, and in-process HTTP
> tests with a **fake SMS sender** and a **stub CRM**. **No real SMS has been sent and Broadnet has not been contacted.** Real delivery stays
> off until the provider settings at the end are confirmed and supplied. Nothing about Broadnet's contract was guessed.

## Contract changes (all additive — the email contract is unchanged)

| Route | Change |
|---|---|
| `POST /api/genesys/verification/buyer-lookup` | answer adds `availableChannels` (`Email` and/or `Sms`) and `maskedMobile`. `Sms` is listed only when SMS is switched on, the provider is configured **and** CRM holds a usable mobile. |
| `POST /api/genesys/verification/otp/send` | request adds optional `channel` (`Email` default, or `Sms`) and `language` (`en`/`ar`, SMS only). Answer adds `channel`. The destination is **never** a request field. |
| `POST /api/genesys/verification/otp/resend` | unchanged request; uses the challenge's own channel and language. |
| `POST /api/genesys/verification/otp/verify` | unchanged. |

New `status` / `code` values (existing ones are unchanged):

| Status | Code | HTTP | Meaning |
|---|---|---|---|
| `SmsNotConfigured` | `OTP_SMS_NOT_CONFIGURED` | 503 | SMS off (`CrmDocuments:OtpSmsEnabled`) or provider not configured. **No challenge is created**, CRM is not called. |
| `NoMobileOnRecord` | `NO_MOBILE_ON_RECORD` | 422 | CRM holds no usable mobile. The searched/caller number is **never** used as a fallback. |
| `DeliveryUnconfirmed` | `OTP_DELIVERY_UNCONFIRMED` | 504 | Timeout or unreadable provider answer. The SMS **may** have arrived; the challenge stays valid; carries `challengeId`. |
| `DeliveryFailed` | `OTP_DELIVERY_FAILED` | 502 | Provider refused or failed. No code reached the customer. |

`CodeSent` for SMS means the provider's response was **verified as acceptance** (not proof the handset received it).

## Rules

* **Destination:** the mobile CRM returns for the verified customer (international digits). A number written nationally (`05…`) is completed only with
  `CrmDocuments:OtpSmsDefaultCountryCode`; otherwise it is refused, not guessed. A resend is refused if CRM's mobile changed since the challenge was created.
* **No automatic resend after an ambiguous result.** After `DeliveryUnconfirmed`, a retried `send` for the same customer/unit/channel answers
  `DeliveryUnconfirmed` again **without** texting; only an explicit `otp/resend` (after the cooldown) issues a new code, which replaces the old one.
  A plain retried `send` after an accepted SMS inside the resend interval answers `AlreadySent` as for email.
* **Shared limits (existing `CrmDocuments:Otp*`):** 10-minute code, 5 wrong tries then lock, 3 sends per challenge, 60 s between sends, 5 challenges per CRM customer per hour
  **counted across email and SMS together**. An email challenge and an SMS challenge for the same unit are separate and verify independently.
* **Evidence recorded server-side**, bound at creation to: the calling account, the CRM customer, the selected unit and its Lead. Verification by another account,
  or replay of a spent code, is refused (`404 OTP_CHALLENGE_NOT_FOUND` / `409 OTP_ALREADY_USED`); two simultaneous verifications leave exactly one session (concurrency tokens).
* **Cached contact:** `CrmContactId = "{customerId}-{unitId}"` (unit-scoped: one customer on two units has two rows), `ContactType = Buyer` (never assumed `Owner`),
  `ContactChannel` = the mobile exactly as CRM holds it.
* **The code and the message text are never returned, logged or audited.** Logs carry only the outcome, HTTP status and a masked number; the Broadnet client is
  registered without HTTP request logging (which would print the URL, password included).

## Financial details need that evidence (`customers/unit-details`)

Sold price and registration cost are released only for a `verificationSessionId` that is **all** of: owned by the calling account, confirmed, unexpired, produced by a
**verified OTP challenge** (`ProofChallengeId`/`CrmBuyerCustomerId`/`CrmBuyerLeadId` present), bound to **this** CRM customer and **this** unit's Lead, for this unit — and whose
challenge row exists, was spent by the right code, and produced this very session. A session that merely *asserts* `verificationMethod` (including `AuthenticatedDigitalUser`
or `Otp` set by anyone) has none of that and releases nothing: `financialDetailsStatus: "VerificationFailed"`, and CRM is not asked for the sale.
Unit, project, completion and handover facts need no verification.

## Documents

Only the **code** can travel by SMS. `documents/send-copy` still delivers documents by **Email** (`deliveryChannel: Sms`/`WhatsApp` → `501`).

## Configuration

`CrmDocuments` (TigerCS API host; the existing OTP settings are reused, these are new):

| Key | Default | Meaning |
|---|---|---|
| `OtpSmsEnabled` | `false` | allows `channel: "Sms"`. Both this and a configured provider are required |
| `OtpSmsDefaultCountryCode` | *(empty)* | completes national-form mobiles; empty = refuse them |
| `OtpSmsDefaultLanguage` | `en` | SMS language when the request names none |
| `OtpSmsMessageEn` / `OtpSmsMessageAr` | templates | placeholders `{code}`, `{minutes}` |

`Sms` (TigerCS API host):

| Key | Meaning |
|---|---|
| `Provider` | `Disabled` (default) · `Broadnet` · `Fake` (Development/Testing only — the host refuses to start with it elsewhere) |
| `Broadnet:Endpoint` | **https** URL of the send action, no query string (plain http is refused) |
| `Broadnet:User`, `Password`, `SenderId` | sent as `user`, `pass`, `sid`. The password is a secret (`Sms__Broadnet__Password`) |
| `Broadnet:TypeEnglish`, `TypeArabic` | the provider's `type` value per language |
| `Broadnet:MobileFormat` | `InternationalDigits` (`9715…`) or `PlusInternational` (`+9715…`) |
| `Broadnet:SuccessBodyPattern` | regex a 2xx body must match to count as acceptance (optional named group `ref`) |
| `Broadnet:FailureBodyPattern` | optional regex marking a 2xx body as failure |
| `Broadnet:TimeoutSeconds` | default 10; on expiry the outcome is *unconfirmed* |
| `Fake:LogMessageText` | Development only: log the message including the code |

The Broadnet sender stays **inert** until the endpoint is https and `User`, `Password`, `SenderId`, both types, `MobileFormat` and `SuccessBodyPattern` are all set. Query parameter
names (`user`, `pass`, `sid`, `mno`, `type`, `text`) follow the integration snippet supplied; every value is percent-encoded. Response handling: 2xx matching the failure pattern →
`Rejected`; 2xx matching the success pattern → accepted; 2xx matching neither → **unconfirmed** (never success); 429/5xx/connection never made → `Failed`; other 4xx → `Rejected`;
timeout or a failure after the request left → `Unconfirmed`. Redirects are never followed. There is no retry in the adapter.

Database: migration `AddSmsChannelToCustomerOtp` (script `AddSmsChannelToCustomerOtp.sql`) adds `Channel` (default Email), `Language` (default `en`) and `DeliveryState` (default 0) to
`CustomerOtpChallenges`; existing rows read as Email.

## Local use

`Sms:Provider=Fake` (committed in Development appsettings): no SMS is sent; with `Sms:Fake:LogMessageText=true` the message (code included) is logged. Set the pepper outside the repo
(`CrmDocuments__OtpCodePepper`, or `dotnet user-secrets`).

## Real UAT (not yet done)

1. Supply the provider settings above (HTTPS endpoint, English/Arabic `type`, mobile format, success/failure response contract, approved sender id, credentials) and set
   `CrmDocuments__OtpSmsEnabled=true`, `Sms__Provider=Broadnet`.
2. `buyer-lookup` for a real UAT buyer → `availableChannels` contains `Sms`, `maskedMobile` matches CRM.
3. `otp/send {channel:"Sms", language:"en"}` → `CodeSent`; the SMS arrives on the **CRM** mobile with the code; repeat with `ar` and check the Arabic text renders.
4. Force each provider outcome (wrong password → `OTP_DELIVERY_FAILED`; an unreachable endpoint; a slow endpoint → `OTP_DELIVERY_UNCONFIRMED`) and confirm no second SMS is sent without `otp/resend`.
5. `otp/verify` → `Verified`; `customers/unit-details` with the session → sale for that unit's Lead; the other unit → `VerificationFailed`; `documents/send-copy` → `Sent`.
6. Through `https://tigergroup.ae/api/genesys/...` for every step (the website forwards raw bodies, so `channel`/`language` pass through).
