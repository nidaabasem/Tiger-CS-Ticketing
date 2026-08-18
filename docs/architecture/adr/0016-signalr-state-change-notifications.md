# ADR-0016: SignalR State-Change Notifications

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

Agents and management need live visibility into ticket state and SLA countdowns without manual refreshing. Broadcasting a per-second countdown from the server does not scale with concurrent connections and is redundant, since a client can compute the same countdown locally from a due timestamp it already has.

## Decision

Use **SignalR** to publish discrete **state and deadline-change events** only — `TicketStatusChanged`, `SlaDueTimestampChanged`, `EscalationLevelChanged`, `VerificationStatusChanged`, and (new for Genesys) `GenesysInteractionLinked` — never a per-second countdown tick. Clients compute and render the visible countdown locally against the due timestamp they receive.

## Alternatives Considered

- **Server-side per-second countdown broadcast** to every connected client.
- **Polling** — clients periodically call a REST endpoint.
- **SignalR, change-events only** (chosen).

## Advantages

- Drastically lower server and network load than a per-second broadcast, as concurrent connections grow.
- Avoids clock-skew/jitter artifacts a server-pushed countdown would exhibit.
- Discrete events map cleanly onto the same domain events flowing through the Outbox (ADR-0013), keeping the real-time layer consistent with the rest of the reliability architecture.

## Disadvantages

- Client code must implement its own local countdown timer.
- Still requires persistent-connection infrastructure, more operationally involved than plain REST, even used sparingly.

## Consequences

Any future feature needing "live" behavior defaults to this same change-event pattern. The screen-pop notification for an incoming Genesys call (ADR-0019) uses this same hub.

## Risks

- Low for pilot scale; SignalR connection-scaling considerations become more relevant only if concurrent agent count grows well beyond pilot volume.
