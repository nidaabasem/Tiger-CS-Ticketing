# One-time codes by SMS (Genesys verification)

Adds **SMS** as a delivery channel to TigerCS verification. A correct code produces the
**existing** `VerificationSession` (method `Otp`, owned by the calling service account, bound to the
customer's unit, 30-minute lifetime) — the same proof `customers/unit-details` and `documents/send-copy`
already accept. There is no second verification system. Before this change TigerCS had no code that issued,
resent or checked an OTP; the three routes below are those, and the website already forwards to them.

> **Verification status.** Everything below is verified **locally only**: unit and in-process HTTP tests with
> a fake SMS sender. **No real SMS has been sent and Broadnet has not been contacted.** Real delivery stays
> off until the provider settings listed at the end are confirmed and supplied.

## Routes (all `POST`, service-account bearer token, policy `CustomerVerification`, also via TigerGroupWeb)

| Route | Body | Success |
|---|---|---|
| `/api/genesys/verification/otp/send` | `customerReference`, `phoneNumber`, `unitId`, optional `channel` (`Sms`), `language` (`en`/`ar`) | `200` `status: "Sent"`, `otpChallengeId`, `channel`, `maskedDestination`, `expiresAtUtc`, `resendAvailableAtUtc`, `sendsRemaining` |
| `/api/genesys/verification/otp/resend` | `otpChallengeId`, optional `language` | same as send |
| `/api/genesys/verification/otp/verify` | `otpChallengeId`, `code` | `200` `status: "Verified"`, `verificationSessionId`, `verificationSessionExpiresAtUtc`, `unitId`, `customerReference` |

Flow: lookup → pick unit → **send** → customer reads the SMS → **verify** → send `verificationSessionId` with
`customers/unit-details` (sale released) or `documents/send-copy`.

**Who and what:** ownership uses the same rule as unit-details (the named customer must be the one CRM resolves for the
number, and the unit one of theirs; another customer's unit gets `UNIT_NOT_ELIGIBLE`). The SMS goes to the mobile
**CRM holds** for that customer — never to a number the caller supplies. The code is never returned, logged or audited,
and only an HMAC-SHA-256 of it (keyed by `Otp:Pepper`) is stored. Only the account that sent a code can resend or verify
it (`OTP_CHALLENGE_NOT_FOUND` otherwise).

## Delivery is reported honestly

| Provider result | API answer | Challenge |
|---|---|---|
| Response verified as acceptance | `200 Sent` (provider acceptance; handset delivery is not confirmed) | valid |
| Explicit refusal (bad credentials/sender/number, failure body, non-retryable 4xx) | `502 OTP_DELIVERY_REJECTED` | closed |
| Provider unreachable / 5xx / 429 / connection never made | `502 OTP_DELIVERY_FAILED` (retryable) | closed |
| **Timeout, 2xx with an unrecognised body, or a failure after the request left** | **`504 OTP_DELIVERY_UNCONFIRMED`** (carries `otpChallengeId`) | **stays valid** — the SMS may have arrived |

**Nothing is ever resent automatically.** After an unconfirmed send the bot should ask the customer whether a code
arrived; only an explicit `resend` (after the cooldown) issues a new one, which replaces the old code.

## Errors

`OTP_DISABLED` 503 · `OTP_SMS_NOT_CONFIGURED` 503 · `OTP_CHANNEL_NOT_INTEGRATED` 501 · `OTP_INVALID_REQUEST` 400 ·
`CUSTOMER_NOT_VERIFIED` / `UNIT_NOT_ELIGIBLE` 403 · `CUSTOMER_AMBIGUOUS` 409 · `CRM_UNAVAILABLE` 502 ·
`OTP_DESTINATION_UNAVAILABLE` 422 (no usable mobile in CRM) · `OTP_RATE_LIMITED` 429 · `OTP_RESEND_TOO_SOON` 429 (+`Retry-After`) ·
`OTP_RESEND_LIMIT` 429 · `OTP_DELIVERY_REJECTED|FAILED` 502 · `OTP_DELIVERY_UNCONFIRMED` 504 · `OTP_CHALLENGE_NOT_FOUND` 404 ·
`OTP_CHALLENGE_NOT_ACTIVE` 409 · `OTP_CONFLICT` 409 · `OTP_EXPIRED` 410 · `OTP_INVALID_CODE` 400 (+`attemptsRemaining`) · `OTP_LOCKED` 429.
Errors are ProblemDetails with `code`; challenge-related ones also carry `otpChallengeId`.

## Configuration (none of the Otp values existed before; all defaults are proposals — change them in configuration)

`Otp` (TigerCS API host):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | master switch |
| `Pepper` | *(empty)* | HMAC key for the stored code hash. **Secret**; env var `Otp__Pepper`. Changing it invalidates outstanding codes |
| `CodeLength` | 6 | digits (4–9) |
| `CodeLifetimeMinutes` | 5 | code validity |
| `MaxVerifyAttempts` | 5 | wrong codes per issued code before lock |
| `MaxSendsPerChallenge` | 3 | first send + resends |
| `ResendCooldownSeconds` | 60 | minimum gap between sends |
| `MaxChallengesPerCustomerPerHour` | 5 | per CRM customer |
| `MaxChallengeAgeMinutes` | 30 | a challenge older than this cannot be resent/verified |
| `DefaultCountryCode` | *(empty)* | completes a CRM mobile written as `05…`; empty = such numbers are refused, not guessed |
| `DefaultLanguage` / `MessageEn` / `MessageAr` | `en` / templates | placeholders `{code}`, `{minutes}` |

The verification session created on success uses the existing 30-minute `VerificationSessionAppService.SessionLifetime`;
the document/unit-details acceptance policy is the existing `CrmDocuments:AcceptedVerificationMethods`.

`Sms`:

| Key | Meaning |
|---|---|
| `Provider` | `Disabled` (default) · `Broadnet` · `Fake` (Development/Testing only — the host refuses to start with it elsewhere) |
| `Broadnet:Endpoint` | **https** URL of the send action, no query string (plain http is refused) |
| `Broadnet:User`, `Password`, `SenderId` | sent as `user`, `pass`, `sid`. Password is a secret (`Sms__Broadnet__Password`) |
| `Broadnet:TypeEnglish`, `TypeArabic` | the provider's `type` values |
| `Broadnet:MobileFormat` | `InternationalDigits` (`9715…`) or `PlusInternational` (`+9715…`) |
| `Broadnet:SuccessBodyPattern` | regex a 2xx body must match to count as acceptance (optional named group `ref`) |
| `Broadnet:FailureBodyPattern` | optional regex marking a 2xx body as failure |
| `Broadnet:TimeoutSeconds` | default 10; on expiry the outcome is *unconfirmed* |
| `Fake:LogMessageText` | Development only: log the full text including the code |

The sender stays **inert** (`OTP_SMS_NOT_CONFIGURED`, no challenge created) until the endpoint is https and
`User`, `Password`, `SenderId`, both types, `MobileFormat` and `SuccessBodyPattern` are all set. Query parameter names
(`user`, `pass`, `sid`, `mno`, `type`, `text`) follow the supplied integration snippet. Every value is
percent-encoded; the URL, text, credentials and provider response body are never logged (only outcome, HTTP status and a
masked number), and the HTTP client is registered without the framework's URL logging.

Database: table `OtpChallenges` (migration `AddOtpChallenges`; script `AddOtpChallenges.sql` at the repository root).

## Local / development use

`Sms:Provider=Fake` (Development appsettings): no SMS is sent; with `Sms:Fake:LogMessageText=true` the message
(including the code) is written to the log. Set the pepper outside the repo: `dotnet user-secrets set Otp:Pepper "<random>"`.

## Still to do before real delivery

See the provider settings above: HTTPS endpoint, English/Arabic `type` values, mobile format, success/failure response
contract, approved sender id and the credentials. Nothing was guessed; the Genesys Data Actions for these three routes
are not written yet.
