# Embedding Tiger Ticketing in Genesys Cloud (iframe)

Status: code change ready for review. **Not deployed to Production** — see §6 for the order of operations.

## 1. Why it said "ticketing.tigergroup.ae refused to connect"

That message is the browser refusing a frame. Tiger Ticketing (TigerCS.Web) sent
`Content-Security-Policy: … frame-ancestors 'none'`, so no site — including
`https://apps.mypurecloud.de` — could frame it. (The app set no `X-Frame-Options`; if the browser still
refuses after this change, IIS or a reverse proxy is adding one — §4.)

Two further problems would have hit as soon as the frame was allowed:

| Problem | Effect inside the Genesys iframe |
|---|---|
| Auth cookie `TigerCS.Web.Auth` was `SameSite=Strict` | Browser never sends/stores it in a cross-site frame → login loops back to `/Login`; Screen Pop signs in, redirects, and lands unauthenticated. |
| Antiforgery cookie defaulted to `SameSite=Strict` | Login form POST fails with HTTP 400. |

## 2. What changed (TigerCS.Web)

* `Embedding:AllowedFrameAncestors` (appsettings) — exact `https://host[:port]` origins. Shipped value:
  `["https://apps.mypurecloud.de"]`. Empty array ⇒ framing fully denied again.
* Startup **rejects** wildcards, `http`, paths, credentials, multiple values per entry, `'self'`, `data:` — the list cannot
  silently widen.
* CSP becomes `frame-ancestors https://apps.mypurecloud.de`. All other CSP directives are unchanged
  (no `unsafe-inline`/`unsafe-eval`).
* `X-Frame-Options: DENY` is sent only when embedding is **off**. When on it is omitted (its only origin-specific form,
  `ALLOW-FROM`, is obsolete and ignored; `SAMEORIGIN`/`DENY` would block Genesys). CSP is the sole control.
* When embedding is on: auth + antiforgery cookies are `SameSite=None; Secure` (always Secure) and `Partitioned` (CHIPS),
  so they survive browsers restricting third-party cookies. HttpOnly is kept. When off, cookies stay `Strict`.
* Antiforgery cookie renamed `TigerCS.Web.Antiforgery`; existing antiforgery tokens are simply reissued.

Not changed: authentication requirements, authorization policies, the Screen Pop token flow, HSTS, HTTPS redirect.

### Residual risk of `SameSite=None`
Cross-site requests from *any* site will now carry the cookie. CSRF protection therefore rests on ASP.NET Core
antiforgery (automatic for Razor Page POSTs, tied to the same-name cookie) plus `form-action 'self'`; the Api itself uses
bearer JWTs, not cookies. Clickjacking stays blocked for every origin except the approved one.

## 3. Screen Pop inside an embedded app

Flow: Genesys opens `/ScreenPop?token=…` → page redeems the one-time token with TigerCS.Api → `SignInAsync` →
`LocalRedirect(targetPath)`. All of it is same-frame navigation, which is fine in an iframe; the previous failure was only the
Strict cookie. Verified by reading the code: no `top.location`/frame-busting, redirects are local-only (`Url.IsLocalUrl`),
`Cache-Control: no-store` and `Referrer-Policy: no-referrer` still apply. Behavior to know:

* The Screen Pop **replaces** any session in that browser (by design). In the iframe the session is partitioned to the
  Genesys top-level site, so it does not touch an agent's separate direct login in another tab.
* Session lifetime is the JWT's (no sliding); expired → redirect to `/Login` inside the frame. Re-launch the Screen Pop
  from the interaction for a fresh token.
* The Customers/Tickets "back-link context" cookies remain `Strict`; inside the frame the breadcrumb falls back to its default.
  Cosmetic only; left unchanged to keep this minimal.
* Safari (ITP) blocks third-party cookies outright; embedded use needs Chrome/Edge (Genesys' supported browsers).

## 4. IIS / reverse proxy requirements

1. **No framing headers from IIS.** Check `web.config` (`<system.webServer><httpProtocol><customHeaders>`) and IIS Manager →
   *HTTP Response Headers* for `X-Frame-Options` or a `Content-Security-Policy` with `frame-ancestors`. Remove them from the
   `TigerCS-Web` site; two CSPs are *both* enforced, so the stricter wins. Same for any F5/NGINX/WAF in front of
   `ticketing.tigergroup.ae` (e.g. NGINX `add_header X-Frame-Options`, `proxy_hide_header X-Frame-Options`).
2. **Pass through** `Set-Cookie` unchanged (do not strip `SameSite`, `Secure` or `Partitioned`; no `proxy_cookie_flags … samesite=strict`).
3. **HTTPS end-to-end for the browser**: the public site must be HTTPS (it is, `ticketing.tigergroup.ae`); `Secure` cookies are
   emitted regardless of the hop between proxy and Kestrel/IIS.
4. Per-environment override without a code change: set `Embedding__AllowedFrameAncestors__0` as an environment variable on the
   app pool / `web.config` `<environmentVariables>`; blank it to disable embedding.
5. In Genesys Cloud, the app's URL (Admin → Integrations → Custom Client Application / Interaction Widget) must be
   `https://ticketing.tigergroup.ae/…`; add `allow-same-origin allow-scripts allow-forms allow-popups` (+ `allow-storage-access-by-user-activation`) to the sandbox attributes so cookies and forms work.
6. The Genesys region host is `apps.mypurecloud.de` (Frankfurt). Other regions need their own explicit entry — do not use wildcards.

## 5. Tests

`src/TigerCS.Tests/Web/EmbeddingPolicyTests.cs`: default deny, scoped origin, wildcard/http/path/credential rejection,
cookie SameSite/Secure/Partitioned, shipped config, and real-host response headers. The existing CSP test in
`DashboardUiTests` was updated to require the configured directive. *(Authored in an environment without the .NET SDK — run
`dotnet test src/TigerCS.Tests --filter "FullyQualifiedName~EmbeddingPolicyTests|FullyQualifiedName~DashboardUiTests"` before merging.)*

## 6. Verification before any Production deploy

1. `dotnet test` (above) green.
2. Deploy to **UAT/staging only**. Then:
   ```bash
   curl -sI https://<uat-host>/Login | grep -iE "content-security-policy|x-frame-options|set-cookie"
   ```
   Expect `frame-ancestors https://apps.mypurecloud.de`, **no** `X-Frame-Options`, and (after a login POST) `TigerCS.Web.Auth=…; secure; samesite=none; httponly; partitioned`.
3. In Genesys Cloud (Chrome), open the embedded app: login page renders, login succeeds and stays signed in after navigating.
4. Launch a real Screen Pop from an interaction: lands on the target page, signed in as the mapped agent.
5. Negative checks: a test page on any other origin framing the site is blocked; DevTools console shows the
   `frame-ancestors` violation.
6. Only then schedule the Production deploy through the normal process in `docs/Production-Deployment.md`.

Rollback: set `Embedding__AllowedFrameAncestors__0` to blank (restores `'none'`, `DENY`, Strict cookies) and recycle `TigerCS-Web`.
