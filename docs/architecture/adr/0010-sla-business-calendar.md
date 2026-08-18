# ADR-0010: SLA Business Calendar

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Critical-priority SLAs run 24/7; all other tiers run only during business hours and pause on non-business days, including UAE public holidays (which shift yearly and are confirmed close to the date). The exact operating week (Saturday–Thursday vs. Monday–Friday) was an open question (ISSUE-017), approved as Option A (Saturday–Thursday, Friday off, as documented) but explicitly still built as configurable data.

## Decision

Model the business calendar as two pieces of reference data — `BusinessCalendar` (working-day mask, business-day start/end times) and `Holiday` (individual dates, entered by System Administrator, confirmed by Customer Service/HR per ISSUE-012's split ownership) — consulted by the SLA due-date calculation whenever a non-Critical tier's clock is computed.

## Alternatives Considered

- **Hardcoded working-day and holiday logic in application code.**
- **A single flat "is business day" boolean per date**, with no distinction between weekly pattern and ad hoc holidays.
- **Separate `BusinessCalendar` + `Holiday` reference data, code-driven** (chosen).

## Advantages

- Confirmed working week (ISSUE-017, Option A) and future holiday dates can be corrected/entered without a code change or redeploy — critical since holidays are announced close to the date each year.
- Separating the weekly pattern from ad hoc holiday dates keeps each concern simple to reason about and independently maintainable by its respective owner (System Administrator vs. Customer Service/HR, per ISSUE-012).
- A pure function `IsBusinessMoment(dateTimeUtc)` consuming this reference data is trivially unit-testable against the worked examples in `SLA-Architecture.md`.

## Disadvantages

- Requires an annual process discipline (holiday entry/confirmation) that, if missed, silently produces incorrect SLA calculations around an unaccounted holiday.
- Reference-data-driven calendars are marginally more complex to query than a hardcoded check, though negligible at MVP scale.

## Consequences

Every non-Critical SLA due-date computation consults this calendar; Critical SLA computation explicitly bypasses it (ADR-0011 relies on this distinction for escalation windows too).

## Risks

- No automated feed of UAE public holidays exists at MVP; a missed manual entry is the most likely real-world failure mode, mitigated by the ISSUE-012 ownership split and an annual reminder process (operational, not architectural).
