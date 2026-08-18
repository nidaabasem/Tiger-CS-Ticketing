# ADR-0010: SignalR Real-Time Updates

**Status:** Accepted
**Date:** 2026-08-17

## Context

Agents and management need live visibility into ticket state and SLA countdowns without manually refreshing. Broadcasting a live, per-second countdown from the server does not scale with concurrent connections and is redundant, since a client can compute the same countdown locally from a due timestamp it already has.

## Decision

Use **SignalR** for real-time updates, but restrict its use to publishing discrete **state and deadline-change events** (`TicketStatusChanged`, `SlaDueTimestampChanged`, `EscalationLevelChanged`, `VerificationStatusChanged`) — never a per-second countdown tick. Clients compute and render the visible countdown locally against the due timestamp they receive.

## Alternatives Considered

- **Server-side per-second countdown broadcast** to every connected client viewing a ticket.
- **Polling** — the client periodically calls a REST endpoint instead of any push mechanism.
- **SignalR, change-events only** (chosen).

## Advantages

- Drastically lower server and network load than a per-second broadcast, especially as concurrent agent/dashboard connections grow.
- Avoids clock-skew and network-jitter artifacts a server-pushed countdown would exhibit; a client-side timer against a fixed due timestamp is simpler and more robust.
- Discrete, named events map cleanly onto the same domain events already flowing through the Outbox (ADR-0008), keeping the real-time layer consistent with the reliability architecture rather than introducing a separate, parallel mechanism.

## Disadvantages

- Client code must implement its own local countdown timer logic, rather than simply displaying a server-provided value.
- If a client's local clock is significantly wrong, its computed countdown will visibly disagree with the server's, even though the underlying due timestamp is correct — a rare edge case, mitigated in practice by NTP-synced client devices.
- Still requires persistent-connection (SignalR) infrastructure, which is more operationally involved than plain request/response REST, even though it is used sparingly here.

## Consequences

The API contract sketch's SignalR hub section reflects this design directly. Any future feature needing "live" behavior should default to this same change-event pattern rather than reintroducing a server-side ticking broadcast.
