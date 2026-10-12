# Screenshots rendered with SYNTHETIC TEST DATA (not production)

These three pages were rendered by the real Razor pages (Receivables, Campaigns) against a **fake API with invented rows** (names such as "Lagoon Buyer", towers "Lagoon View" / "Blue Wave",
the units TP119-501 and TP115-1921 with a +966 mobile). They show how the application displays the cases of this change; they are **not** production data and prove nothing about the real CRM or PACT.
No screenshot with real data was taken: the CRM, PACT and the production database are not reachable from the build environment.

| File | Shows |
| --- | --- |
| `receivables-tower-names-and-crm-contacts.png` | Newest tower first (TP140 ... TP115); tower names from the source (Lagoon View, Blue Wave); a tower with no known name shows its number (118); a +966 CRM mobile kept as +966; a unit both companies hold shows "needs review". |
| `campaigns-tower-names-and-crm-contacts.png` | The same on Campaigns, CRM healthy. |
| `receivables-crm-unavailable.png` | CRM contact data could not be read: the page says so (banner + "CRM unavailable: contact unconfirmed") instead of reading as "no mobile". |
