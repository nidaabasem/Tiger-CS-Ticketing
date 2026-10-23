# Leasing Inquiry request type (Genesys → LCS)

Genesys sends `requestType: "Leasing Inquiry"` (or its `requestTypeId`) for a leasing request. It is a normal request type of **Leasing Customer Services (LCS)**:
active, a Published workflow (`LCS-INQ-001`: Ticket Created → Leasing CS Queue → Agent → Review → Resolve → Close), default priority **Normal** (= the Medium tier).
Routing, department transfer, auto-assignment and SLA follow [Request-Type-Routing-And-Default-Priority.md](Request-Type-Routing-And-Default-Priority.md).

Example (ids are the caller's):

```json
{ "conversationId": "C-1", "serviceRequestId": "leasing-inquiry-001", "requestType": "Leasing Inquiry", "humanFollowUpRequired": true }
```

A different `serviceRequestId` in the same conversation is a separate ticket; `humanFollowUpRequired: true` keeps it out of every automatic closer
([Multiple-Service-Requests-Per-Conversation.md](Multiple-Service-Requests-Per-Conversation.md)). The old name `Leasing` does **not** resolve (422).

## Creating it

* **Development / test databases:** `LeasingInquiryRequestTypeImporter` runs after the workbook import in the development seed. It is additive and idempotent:
  an LCS request type with that name is reused untouched, a second run creates nothing, it never creates the department and never writes an assignment rule or SLA row.
* **UAT / production:** create it from Administration (Request Types → New): department LCS, name `Leasing Inquiry`, an existing workflow with a Published version
  (or the `LCS-INQ-001` workflow), default priority Medium, Active. Administration refuses a duplicate name in the department.
* **Assignment (must be configured by an administrator):** add a rule on the request type naming a verified, active LCS member (Specific employee, or Team with a primary).
  No rule, or an assignee who is not an active LCS member, leaves the ticket unassigned in the LCS queue (audited as `AutoAssign … DepartmentQueue`). No employee is ever guessed.

Verify on the database with the read-only `uat/verify-leasing-routing.sql`.
