# ADR-0017: File Attachment Storage

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Tickets may carry up to 10 attachments (images, PDFs, videos), which must be virus-scanned before being considered available, must not be stored as inline database blobs at scale, and must respect Tiger Group's data-ownership requirement.

## Decision

Store attachment binary content in dedicated file/object storage, referenced from the `TicketAttachment` entity only by a `StorageReference` — never storing file bytes in SQL Server. Enforce the 10-attachment-per-ticket and (assumed, [ASSUMPTION]) 25MB-per-file limits at the application layer, and require a mandatory virus-scan gate before an attachment is made available.

## Alternatives Considered

- **Store bytes directly** in a SQL Server `varbinary(max)`/FILESTREAM column.
- **Local application-server filesystem storage.**
- **Dedicated object/blob storage, referenced by the database** (chosen).

## Advantages

- Keeps the primary database focused on transactional/relational data, avoiding bloated backups.
- Object storage scales independently of the database and offers durability guarantees appropriate to the 7-year retention commitment.
- A distinct `StorageReference` plus scan-status gate cleanly enforces "no attachment is available until scanned."

## Disadvantages

- Introduces an additional infrastructure dependency with its own access-control configuration.
- Requires orchestration so an attachment's database row and its blob upload stay consistent — handled via the same Outbox/idempotency discipline as other cross-boundary effects.

## Consequences

`TicketAttachment.StorageReference`, `SizeBytes`, and `VirusScanStatus` in `Domain-Model.md` reflect this decision directly. The specific object-storage provider is an infrastructure choice for Phase 3, not decided here.

## Risks

- The 25MB-per-file limit is an [ASSUMPTION], not sourced from a management decision; should be confirmed before Phase 3 implementation locks it in.
