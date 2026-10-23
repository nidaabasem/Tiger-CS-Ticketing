# Ticket Details improvements — final verification

## 1–2. Why Call Center has no classification categories, and where categories come from
`Categories` rows are created in exactly three places, none of which involves Call Center:
1. `DevSeedData` (development databases only): "General Inquiry" (Customer Service) and "Corrective Maintenance" (Facilities).
2. `NewRequestTypesImporter` (fresh dev databases) and `ImportNewRequestTypes_UAT.sql` (existing UAT databases): one category per **active request type**, same department, same name, inserted only if missing.
3. Nothing else. There is no Administration screen for categories (Administration has Users, Departments, Request Types, Workflows, Channels).

Call Center (`CC`) is seeded as a department but has **no request types** ("Not seeded on purpose" — `Workflow-SLA-Configuration-Phase1.md`), so the import never creates a category for it. The Genesys queue→department mapping places tickets in `CC`; nothing maps queues to categories by design (`AdminGenesysRoutingAppService`). Whether the live UAT database has extra hand-made `CC` rows could not be checked from here (no database access); the code and the documented seed produce none.

## 3. Is "same name" the right relationship?
No explicit relationship exists. `RequestType` has no `CategoryId`; `Category` has no request-type link; `Tickets` stores `CategoryId` and `RequestTypeId` independently; `Category.ParentCategoryId` exists but is unused. `Workflow-SLA-Configuration-Phase1.md` §3.1 records the Category↔RequestType wiring as an open "phase-2 decision". Matching by exact name is only a convention introduced by the New Ticket picker and the import script, and the UAT import guide itself warns that a renamed request type needs a re-matching category. It is the only existing rule, so Classify follows it; it is fragile. Options needing a business/architecture decision: (a) add an explicit `RequestTypes.CategoryId` (schema change), or (b) stop pairing them in Classify. Not done.

## 5. Intermittent full-suite failures
Cause: **environmental** — the Linux `inotify` instance limit (`fs.inotify.max_user_instances` = 128 in this sandbox). Every failure in a failing run (338/338 analysed) carried `System.IO.IOException: The configured user limit … on the number of inotify instances has been reached`; many in-process `WebApplicationFactory` hosts each start file watchers. The failing set changes run to run, and the untouched baseline fails the same way (214 failures). With `DOTNET_USE_POLLING_FILE_WATCHER=1` the full suite passed twice in a row: 5,688 passed, 0 failed, 51 skipped (the same 51 skips as baseline). No actual regression was found.

## 6. Real local application
Real Api + real Web on Kestrel (EF in-memory database), tickets created through the real Genesys endpoints, real login: Call Center AI ticket shows the explanation and no form; an administrator gets the Request Types link; a Customer Service ticket was classified end to end (category and request type shown by name, workflow pinned); the CRM/human ticket renders Conversation History (two interactions, duplicate "Hello"s kept, system event apart, agent labelled) and Customer Satisfaction (4/5, feedback, UAE date, Test badge); no horizontal overflow on mobile (390px). Screenshots: `docs/ticket-details-screenshots/live/`. Harness caveat: the in-memory database does not generate SQL Server row versions, so they were stamped manually; this is a test-database artifact, not an application defect.

## 7. Interactions tab — information parity
Preserved in each Conversation History group: channel, originating marker, start/end (UAE), "not ended", handled-by (agent named only for human handling), customer, phone, interaction status and end reason, handoff presence/status, initiator, follow-up mode, reason, outcome note, full transcript with sender and time, and the empty/unavailable states. Differences: the tab's count is now in the section title (when more than one); the interaction's raw end reason is shown as before; message times are UAE `HH:mm` with a date heading when the day changes. Nothing found lost.
