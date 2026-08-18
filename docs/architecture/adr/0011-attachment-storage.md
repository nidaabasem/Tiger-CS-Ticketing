# ADR-0011: Attachment Storage

**Status:** Accepted
**Date:** 2026-08-17

## Context

Tickets may carry up to 10 attachments (images, PDFs, videos), which must be virus-scanned before being considered available, must not be stored as inline database blobs at scale, and must respect Tiger Group's stated data-ownership requirement (Geyness may not use ticket/customer data outside the engagement).

## Decision

Store attachment binary content in dedicated file/object storage (e.g., Azure Blob Storage or an equivalent), referenced from the `Attachment` table only by a `StorageReference` (a signed URL or blob key) — never storing file bytes directly in SQL Server. Enforce the 10-attachment-per-ticket and (assumed) 25MB-per-file limits at the application layer, and require a mandatory virus-scan step before an attachment's `VirusScanStatus` becomes `Clean` and it is made available to anyone.

## Alternatives Considered

- **Store attachment bytes directly** in a SQL Server `varbinary(max)`/FILESTREAM column.
- **Store files on the application server's local filesystem.**
- **Dedicated object/blob storage, referenced by the database** (chosen).

## Advantages

- Keeps the primary database focused on transactional/relational data, avoiding bloated backups and slower queries that large binary blobs in SQL Server would cause.
- Object storage scales capacity independently of the database, and typically offers built-in redundancy/durability guarantees appropriate to a 7-year retention requirement.
- A distinct `StorageReference` plus `VirusScanStatus` gate cleanly enforces "no attachment is available until scanned," without bespoke logic inside the database layer.

## Disadvantages

- Introduces an additional infrastructure dependency (the object storage service) with its own access-control and lifecycle configuration, rather than everything living in one datastore.
- Requires careful orchestration so an attachment's database row and its actual blob upload stay consistent — a failed upload must not leave an orphaned `Attachment` row referencing nothing — handled via the same Outbox/idempotency discipline as other cross-boundary effects (ADR-0008).
- The rejected local-filesystem alternative would have been simpler to stand up for a small MVP, at the cost of much weaker durability and scaling guarantees appropriate to a 7-year retention commitment.

## Consequences

`Attachment.StorageReference`, `SizeBytes`, and `VirusScanStatus` in the schema design directly reflect this decision. The specific object-storage provider is an infrastructure choice to be finalized in Phase 3, not decided by this ADR.
