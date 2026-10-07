# Customer cards and payments after PR #70

This follow-up is based on main commit `9f12f67a9fc2538a0c8201c726373162f081ce54` (merged PR #70), rather than the earlier pre-merge patch.

## Review findings

PR #70 retains expired-only PACT customers in Customer Workspace, but its intake lookup removes expired contracts before linking the New Ticket card. That can lose a CRM customer's historical PACT identity and tickets. Its payment panels call directory-backed Collections routes, which cannot resolve a customer with no persisted ticket/profile. The search card also lacks a Payments & Fines action. The date parser's `Math.Abs(long.MinValue)` can throw despite its tolerant-reader contract.

## Follow-up behavior

- Both lookup paths retain PACT contracts and company IDs, marking expiry. Only the New Ticket selection projection excludes contracts ending before Dubai's current calendar date. A renewal remains selectable because filtering precedes unit deduplication. Contracts ending today or without readable dates remain selectable.
- A CRM/PACT association requires matching normalized source phones, matching customer name or email, and matching project plus unit number. Phone alone, a raw cross-system unit ID, or a shared unit without matching customer details cannot merge identities. Ambiguity keeps cards separate.
- New Ticket keeps both stable identities and its existing deduplicated combined history. Customer Workspace uses the same association and a server-side SQL union with department visibility, counts before limits, and no repeated ticket rows. View all tickets on a merged card opens this combined workspace.
- Payments & Fines is available directly on a search card before unit selection or ticket creation, via `/Customers/Payments`. Account changes and retry retain the selected customer and phone.
- `/api/collections/customer-lookup/payment-summary` first checks the financial-read grant, then freshly verifies the selected identity. A CRM selection receives PACT/EDSM data only through the same verified association. Caller-supplied tenant/company mappings are not accepted. The existing summary service independently confirms all tenant/company pairs from unfiltered PACT contracts, including expired ones.
- Existing EDSM UI shows source amounts, transactions and fines, including rented fines as **Not computed** and unavailable values as unavailable rather than zero. Inline wizard PACT panels use the pre-ticket endpoint; CRM panels can fall back to verified EDSM when no directory-backed finance account is available.
- The parser handles the minimum signed epoch value without throwing. No schema migration or persistent identity crosswalk is introduced.

## Verification priority

The user clarified the priority on 6 October 2026: **PACT first, then Tiger CRM**. New Ticket sorts PACT candidates first, shows a verified merged card and summary as `PACT · Tiger CRM`, and offers current PACT units before additional CRM units. A shared physical unit appears once through its current PACT contract; if no current PACT contract exists for that unit, the independently eligible CRM unit remains available. Selecting the PACT row uses the existing PACT ticket-identity path. Both source identities and historical ticket reads remain available.

For an existing CRM unit selection under a verified combined card, the primary inline payment panel reads the verified PACT tenant/company mapping; CRM finance appears afterward under its own customer ID. A customer without verified PACT linkage keeps its CRM verification. Displaying PACT never invents a mapping from the phone alone. The card's internal selection token remains stable for existing navigation.

## Verification

The merged PR #70 was verified through actual GitHub Actions logs: [CI run 37444409152](https://github.com/nidaabasem/Tiger-CS-Ticketing/actions/runs/37444409152). Release build succeeded with **0 warnings, 0 errors**. Tests: **2790 passed, 0 failed, 0 skipped**.

Those results apply to the merged main commit, **not this follow-up**. This follow-up adds regression coverage for missing/ambiguous identity evidence, historical contracts and companies, renewal selection, zero selectable units, pre-ticket authorized/forbidden/disabled reads, unknown tenants, rented fines, SQL history visibility/counts, and actual Razor card/payment rendering. Existing scope tests now assert retained lookup evidence; selection tests assert expired units are excluded.

`git diff --check` passes. Local `dotnet build src/TigerCS.slnx --configuration Release` and `dotnet test src/TigerCS.slnx --configuration Release` both stop with `dotnet: command not found` (exit 127). This follow-up has not yet been compiled or tested. Real CRM/PACT/EDSM UAT has not been performed.

The user authorized publication of this follow-up on 6 October 2026. Publication remains blocked: CLI push has no GitHub credentials, and the connected GitHub integration rejects creating a Git tree with HTTP 403, `Resource not accessible by integration`. No remote follow-up branch or draft PR has been created, and follow-up CI has not started. Local branch: `fix/merged-customer-lookup`. Publication consent remains valid; repository content-write access is the remaining blocker.

## Request category correction from the screenshots

On 6 October 2026, the user requested the bold department group names as
**Request Category**, with the remaining child entries as **Request Type**.
New Ticket now has exactly those two dependent pickers. It retains legacy
routing-only entries and configured workflow types, combines exact duplicate
names within a department once, and resolves both IDs from fresh catalogs on
Review/Create. Cross-category choices, unpublished workflows and missing or
ambiguous routing cannot create a ticket. Category changes retain entered text,
priority and the selected customer/unit, while clearing an invalid old child.
The summary and review use the same category/type terminology.

The importer and UAT SQL now add an exact missing routing row per active
configured type, keeping all existing category IDs, inactive rows and historical
tickets. An existing UAT database needs the updated import rerun before the new
configured choices can be created. See `New-Request-Types-UAT-Import.md`.

Additional regression coverage exercises the combined catalog, legacy fallback,
same-name types in different categories, unpublished workflows, missing/ambiguous
routing, reset on category change, hidden-ID tampering and real HTTP
antiforgery/model-binding through refresh, review and creation. The importer
regression checks idempotence and retained inactive/historical categories.
Build and test attempts for this update also exit 127 because `dotnet` is absent;
these tests are added but unrun. GitHub publication remains blocked as recorded
above.
