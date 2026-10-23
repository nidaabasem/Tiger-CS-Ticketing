# Ticket Details — Classify fix and Conversation History in the Overview

## 1. Root cause of the empty Classify dropdowns

Traced through the Api, configuration and Web code (no code path in the Web app drops valid options):

| Layer | Finding |
|---|---|
| `GET /api/categories?departmentId=` | Returns active categories of that department only. Correct. |
| `GET /api/request-types?departmentId=` | Returns active request types of that department; the page keeps those with a published workflow. Correct. |
| Business rule | `TicketClassificationAppService` refuses a category or request type of another department (`CategoryDepartmentMismatch` / `NotAllowedForRequestType`): classifying names the request, it never re-routes the ticket. The Web form applies the same scope. |
| Call Center configuration | `Call Center` (`CC`) is seeded as a department, but **no request types and no categories exist for it** (`docs/Workflow-SLA-Configuration-Phase1.md`: "Not seeded on purpose: Call Center request types"; the reference seed and the UAT import only create categories for departments that own request types). A Genesys ticket routed to `CC` is created Unclassified in a department with nothing to classify under. |
| Permissions | Not a factor: the viewer was offered the form (`CanClassify`), then got empty lists. |

**Conclusion:** the options are genuinely missing from configuration for that department; they are not filtered away by a defect. The defect was the UX: the page still recommended "Classify" and rendered empty selects. Nothing was seeded or re-routed.

## 2. What changed

* **Classify** is category → request type dependent (a request type sits under the same-named category of the same department, the rule New Ticket's picker already uses). A category with no request type (e.g. General Inquiry) classifies on its own, as the Api allows. The pair is re-validated on POST. Names only, no ids. Priority-downgrade approval rules are untouched.
* With no category for the department the form is replaced by an explanation; the attention card no longer recommends Classify; a System Administrator gets a link to **Administration → Request Types** (the matching routing category is added by the documented request-type import), others are told to ask one, and transfer-capable roles are reminded they can transfer. A failed read is reported as a failure, not as missing configuration.
* **Conversation History** is in the Overview below Request Details as a chat (customer right; bot and people left, people named; system events apart; UAE time; handled-by and handoff chips; per-interaction groups with the latest open; scrollable). It reads the same interactions endpoint and never sorts or de-duplicates (only the Api does, by source message id). The Interactions tab is removed; its details are in each group.
* Overview order: header/actions, attention, Customer Information, Request Details, Conversation History, Customer Satisfaction (always visible), Latest Activity, then collapsed Verification/SLA details. Quick Facts stays operational; duplicated rows removed; category shown by name.

No change to lifecycle, SLA, authorization, Genesys processing or the database schema.

## 3. Screenshots

`docs/ticket-details-screenshots/`: Chromium captures of the real Razor output (`TicketDetailsOverviewRenderTests` with `TIGERCS_RENDER_DUMP_DIR` set, served with the app's own CSS/JS).
