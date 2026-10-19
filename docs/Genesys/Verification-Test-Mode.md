# Verification Test Mode (Local / UAT)

Lets the Genesys OTP and Send-Documents flows run against **real CRM customer data** without any message reaching the real
customer. Identity, CRM records, unit ownership, the OTP challenge and the verification session are untouched; only the
**outbound message** is redirected.

## Configuration

```json
"VerificationTesting": {
  "Enabled": true,
  "SmsOverride": "971564114806",
  "EmailOverride": "nidaa.basim@tigergroup.ae",
  "BlockRealCustomerDelivery": true
}
```

* Committed: `appsettings.json` = **disabled**; `appsettings.Development.json` and `appsettings.UAT.json` = enabled as above.
  Production has no override file, so it stays disabled. Environment variables work too
  (`VerificationTesting__Enabled=true`, `VerificationTesting__SmsOverride=…`).
* `SmsOverride`: international digits, 8–15, no `+`. `EmailOverride`: a valid address.
* **Fail closed.** If `Enabled` is true and the environment is not `Local`/`Development`/`UAT`/`Testing`, or an override is
  missing/invalid, or `BlockRealCustomerDelivery` is not true:
  * the host **refuses to start** (`Program.cs`), and
  * should it be reached anyway, OTP send/resend return `503 OTP_TEST_MODE_MISCONFIGURED` and document sends return
    `DOCUMENT_TEST_MODE_MISCONFIGURED` — before any challenge is created, record claimed or document fetched. There is never
    a fallback to the customer's real address.
* Verify (`otp/verify`) is not a delivery and is unaffected.

## Where it is applied

| Message | Layer |
|---|---|
| OTP email (send, resend, retried send) | `CustomerOtpAppService.DeliverAsync` |
| OTP SMS (send, resend, retried send) | `CustomerOtpAppService.DeliverSmsAsync` |
| Document email — Genesys AI **and** Agent Desk | `EmailDocumentDeliveryChannelSender` (the single sender both flows use via `CrmDocumentCopyAppService`) |

Policy lives in `VerificationTestingPolicy` (`TigerCS.Application/Modules/CrmDocuments/VerificationTesting.cs`).

The customer's real (masked) destination stays on the challenge, the delivery request and the responses; the audit entry adds
`TestRedirect=true`. A redirected document email has subject prefix `[UAT TEST]` and a body note naming only the *masked*
intended recipient. OTP codes are never logged.

## Send Documents flow

1. Genesys AI: `buyer-lookup` → `otp/send` (code to CRM's email/mobile) → `otp/verify` → OTP-verified session.
2. `POST /api/genesys/documents/send-copy` with `verificationSessionId` + `Idempotency-Key`.
   `CrmDocumentCopyAppService.SendAsync` checks: feature enabled → session is OTP-verified with server proof and not expired →
   caller owns the session → CRM customer/lead resolved → record belongs to that customer/unit → destination = CRM email →
   duplicate/idempotency checks → claim → download from CRM → size/record check → `IDocumentDeliveryChannelSender.SendAsync`.
3. Agent Desk: `CustomerDocumentsAppService` (role + customer/lead authorisation) → `CrmDocumentCopyAppService.SendToCustomerAsync`
   → same steps from "record belongs" onward → same sender.

## Testing end to end (local / UAT)

1. Run in `Development` or `UAT` with the config above, `CrmDocuments:Enabled=true`, a real `CrmDocuments:OtpCodePepper`,
   `EmailNotifications` on a real SMTP provider (so the test mailbox receives mail) and, for SMS, `Sms:Provider=Broadnet` +
   `CrmDocuments:OtpSmsEnabled=true`.
2. `POST /api/genesys/verification/buyer-lookup` with a real customer's phone → `otp/send` (`channel` Email or Sms).
3. The code arrives at `nidaa.basim@tigergroup.ae` / `971564114806`, **not** the customer. `otp/resend` likewise.
4. `otp/verify` with that code → verified session.
5. `send-copy` with the session → the document email arrives at `nidaa.basim@tigergroup.ae`.
6. Misconfigure (blank `EmailOverride`) → startup fails; Production + `Enabled=true` → startup fails.

Automated: `VerificationTestModeTests` (fakes only — nothing is sent) and `StartupValidationTests`.
