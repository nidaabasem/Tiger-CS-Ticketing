# ADR-0002: ASP.NET Core on .NET 8

**Status:** Accepted
**Date:** 2026-08-17

## Context

A concrete web framework and runtime must be selected to build the Web API, the Razor Pages/MVC dashboard, and the background-job runtime. Tiger Group's engagement spans multiple years with a 7-year data-retention commitment, so the platform needs long-term support and stability, not just short-term convenience. The other required building blocks — Identity, EF Core, SignalR, Hangfire — all need first-class support on whatever runtime is chosen.

## Decision

Use ASP.NET Core on **.NET 8** (a Long-Term Support release) for both the Web API and the Razor Pages/MVC front end, within the single solution described in ADR-0001.

## Alternatives Considered

- **A non-.NET stack** (e.g., Node.js/Express, Java/Spring Boot).
- **ASP.NET Core on a non-LTS .NET release** (a newer Standard-Term-Support version, for access to the newest language features sooner).
- **ASP.NET Core on .NET 8 LTS** (chosen).

## Advantages

- .NET 8 is an LTS release (three years of support), which matters given the multi-year engagement and 7-year retention horizon — the platform will remain supported well past MVP go-live.
- Mature, first-party support for every other required building block (ASP.NET Core Identity, EF Core, SignalR, Hangfire) with no bridging or compatibility layers needed.
- Cross-platform (Windows, Linux, containers), giving hosting flexibility rather than locking into one operating environment.
- A strongly typed, compiled language (C#) suits the many enum-like state dimensions (`TicketStatus`, `EscalationLevel`, `VerificationStatus`, `SlaState`, `ResolutionOutcome`) with compile-time safety, catching a whole class of invalid-state bugs before runtime.

## Disadvantages

- Ties the team to the .NET ecosystem's release cadence and skill-set requirements.
- Being on an LTS release means some newer C#/.NET language features are unavailable until a deliberate future upgrade.

## Consequences

Every subsequent technology ADR in this log (EF Core, Identity, Hangfire, SignalR) assumes this runtime. Any move beyond .NET 8 in the future is a deliberate, separately-recorded decision, not an incidental side effect of a package upgrade.
