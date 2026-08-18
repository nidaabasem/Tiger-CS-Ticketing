# ADR-0002: ASP.NET Core on .NET 8

**Status:** Accepted
**Date:** 2026-08-17
**Review date:** 2026-09-07 (3-week pilot retrospective)

## Context

A concrete web framework and runtime are required for the Web API, the internal Razor Pages/MVC dashboard, and the background-job runtime, within a 3-week pilot timeline and a multi-year data-retention commitment (7 years).

## Decision

Use ASP.NET Core on **.NET 8** (a Long-Term Support release) for both the Web API and the internal dashboard, within the single solution described in ADR-0001.

## Alternatives Considered

- **A non-.NET stack** (e.g., Node.js/Express, Java/Spring Boot).
- **A non-LTS .NET release**, for earlier access to newer language features.
- **.NET 8 LTS** (chosen).

## Advantages

- LTS support (3 years) matters given the multi-year engagement and 7-year retention horizon.
- First-class, mature support for every other required building block (Identity, EF Core, SignalR, Hangfire) with no bridging layers — important for hitting a 3-week pilot.
- Cross-platform hosting flexibility (Windows, Linux, containers).
- Strongly typed C# suits the many enum-like state dimensions in the ticket domain, catching invalid-state errors at compile time — valuable when development is AI-assisted with human review, since the compiler is an additional reviewer.

## Disadvantages

- Ties the team to the .NET ecosystem's release cadence and skill-set requirements.
- LTS means some newer language features are unavailable until a deliberate future upgrade.

## Consequences

Every subsequent technology ADR (EF Core, Identity, Hangfire, SignalR) assumes this runtime.

## Risks

- Low — this is a mainstream, well-supported platform choice with no material risk to the pilot timeline.
