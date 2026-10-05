# Genesys Collections API

**Audience:** Genesys Cloud integration (data actions, Architect).
**Base path on TigerCS:** `/api/genesys/collections`.
**Contract source:** EDSM behaviour per [`EDSM_Collections_Contract.md`](EDSM_Collections_Contract.md) (static analysis of the EDSM source).

> **Read this first: status of every endpoint**
>
> - **Examples:** every example below is **generated from the implemented TigerCS
>   API** (an in-memory host). EDSM is the **Development/Testing Fixture**
>   provider (bodies in EDSM's documented wire shape) and PACT is the mock
>   gateway. **They are not real EDSM or UAT captures**, and the amounts are
>   fixture data. The `source` field in every financial response says
>   `"Fixture"`.
> - **Validation:** **no endpoint has been validated against a real EDSM or UAT.**
> - **Public URL:** **pending.** TigerGroupWeb (the proxy Genesys calls)
>   is not available to this work, so whether it forwards these routes is unverified (§9).

| Endpoint | Implemented in TigerCS | Publicly reachable via TigerGroupWeb | Real EDSM / UAT validated |
|---|---|---|---|
| `GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary` | ✅ (EDSM, PACT customers) | ⏳ pending (§9) | ❌ |
| `GET /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions` | ✅ (EDSM types 1–3) | ⏳ pending | ❌ |
| `GET /api/genesys/collections/reminders/candidates` | ✅ (CRM per-account source only) | ⏳ pending | ❌ — no real source: answers `503` |
| `POST /api/genesys/collections/reminders` | ✅ | ⏳ pending | ❌ — sending disabled |
| `POST /api/genesys/collections/reminders/{reminderId}/outcomes` | ✅ (ticket handling) | ⏳ pending | ❌ |
| `GET /api/genesys/collections/customers/{crmCustomerId}/reminders` | ✅ | ⏳ pending | ❌ |
| `GET /api/genesys/collections/customers/{crmCustomerId}/outstanding` / `payments` | ✅ (CRM per-account source) | ⏳ pending | ❌ — **no financial source**: answers `503 FinanceUnavailable` |

The two `by-key` routes are the ones that return **real EDSM data** (once
`EdsmProvider` is `Pact`). They use the same service as the TigerCS Payment tab
(`CollectionsPaymentSummaryAppService`).

The older `customers/{crmCustomerId}/outstanding` and `payments` routes are
kept for existing clients:
- They read the per-account source (`ICollectionsFinancialSource`), which has
  no real implementation (`CollectionsSource:Provider = "Unavailable"`).
- They answer `503 FinanceUnavailable` in every real environment.
- **Do not use them for figures.**

---

## 1. Authentication

The same boundary as every other `api/genesys` route
([`Genesys-API-Contracts.md`](../architecture/Genesys-API-Contracts.md)):

1. Genesys obtains a token from TigerGroupWeb:
   `POST https://{tigergroupweb-host}/api/genesys/oauth/token`
   (`grant_type=client_credentials`, `scope=ticketing.genesys`).
2. Every call carries `Authorization: Bearer {access_token}`.
3. TigerGroupWeb forwards the request to the same path on TigerCS, signed in as
   the TigerCS **CS Agent** service account. The bodies pass through unchanged.

**Permissions on TigerCS:**

| Operation | Requirement |
|---|---|
| `payment-summary`, `payment-transactions`, `customers/*/reminders` | Collections **financial-read** (CS Agent has it by default), plus visibility of the customer in the Customer Directory (CS Agent: all departments) |
| `reminders/candidates`, `POST reminders` (SMS/Email) | **reminder-send** (CS Supervisor/Manager, Collections dept, or integration account) |
| `POST reminders` with `VoiceBot`, `POST …/outcomes` | the service account listed in `Collections:Authorization:IntegrationEmployeeIds` |

`Collections:Enabled` must be `true`. Otherwise every route answers `503 CollectionsDisabled`.

### 1.1 Service-account configuration (TigerCS side)

There are two bearer hops:
1. **Genesys → TigerGroupWeb.** Genesys gets an OAuth `client_credentials`
   token for `scope=ticketing.genesys`.
2. **TigerGroupWeb → TigerCS.** TigerGroupWeb sends a TigerCS JWT for the
   service account, obtained with `POST /api/auth/login`.

TigerCS authenticates the second hop only. The tests below send that TigerCS
bearer token directly to `/api/genesys/collections/…`.

**Setting up the service account:**
1. Create one TigerCS user for TigerGroupWeb with the role **CS Agent**. This
   grants financial-read and cross-department Customer Directory visibility.
   Keep its credentials only in TigerGroupWeb's secret configuration.
2. Add that user's **employee ID** (GUID) to
   `Collections:Authorization:IntegrationEmployeeIds`, as an environment
   variable `Collections__Authorization__IntegrationEmployeeIds__0={employee-guid}`.
   - **Required for:** `POST …/outcomes` and VoiceBot queueing.
   - **Also grants:** reminder-send and financial-read.
   - **Not required for** the two read routes when the account is a CS Agent,
     but recommended so that one account serves every Collections route.
3. Leave `Collections:Authorization:FinancialReadRoles` at its default
   (CS Agent, CS Supervisor, CS Manager, General Manager, Chairman/CEO), or
   include the service account's role if you change it.

| Caller | Summary / history | Outcomes / VoiceBot |
|---|---|---|
| CS Agent, listed in `IntegrationEmployeeIds` (recommended) | ✅ 200 | ✅ |
| CS Agent, not listed | ✅ 200 | ❌ 403 |
| Role without financial-read (e.g. Reporting User), not listed | ❌ 403 `Forbidden` | ❌ 403 |
| No or invalid bearer token | ❌ 401 | ❌ 401 |

## 2. Customer identifier

| Key | Format | Financial data? |
|---|---|---|
| **PACT customer** | `ext:Pact:{tenantID}`, URL-encoded in the path: `ext%3APact%3A{tenantID}` | **Yes**, via EDSM |
| Tiger CRM customer | `crm:{customerId}` | **No.** Answers `NotMapped` (summary) / `422 CustomerNotMapped` (transactions). No verified CRM → PACT link exists |
| Phone-only / Tasleeh | `phone:…`, `ext:Tasleeh:…` | **No** (`NotMapped`) |

**Where Genesys gets the tenant ID.** Call
`GET /api/genesys/customers/lookup?phoneNumber=…`. When
`screenPop.verificationSource == "Pact"` and `screenPop.externalCustomerId`
is non-empty, the key is `ext:Pact:{screenPop.externalCustomerId}`.
If several PACT customers matched (`screenPop.matchedCustomerCount > 1`),
do not pick one automatically.

**Precondition.** TigerCS must already know the customer, meaning at least one
TigerCS ticket was verified through PACT for that tenant. Otherwise the answer
is `404 AccountNotFound`.

**How TigerCS confirms the account (never by EDSM alone):**
- EDSM answers an unknown tenant with `200` and zeros, so TigerCS asks EDSM
  only for the (`companyID`, `tenantID`) pairs that PACT's own contracts
  return for this tenant, under the customer's phone numbers.
- That mapping is reused for up to 30 minutes (`mappingSource: "Cached"`),
  and `mappingVerifiedAtUtc` says when PACT last confirmed it.
- Parking units: the tenant always comes from the PACT row's `tenantID`,
  never the Parking ContractID.

## 3. `GET payment-summary`

```
GET /api/genesys/collections/customers/by-key/{customerKey}/payment-summary[?includeTransactions=false]
Authorization: Bearer {access_token}
```

| Query | Default | Meaning |
|---|---|---|
| `includeTransactions` | `true` | Also return each company's read-only transaction lists (types 1–3). Use `false` for a light, IVR-friendly call and fetch history with §4 |

**Response, top level:**

| Field | Meaning |
|---|---|
| `mappingStatus` | `Mapped`, or `NotMapped` (with `mappingDetail`) |
| `pactTenantId` | The tenant asked about |
| `source` | `Pact` (real EDSM), `Fixture` or `Unavailable` |
| `retrievedAtUtc` | When TigerCS called EDSM. **Not** an EDSM as-of time |
| `sourceAsOfUtc` | Always `null`: EDSM returns no as-of time |
| `currency`, `currencySource` | `AED` / `Configured`. EDSM returns **no** currency, so TigerCS labels the configured one |
| `numberCulture` | The configured EDSM number culture; `null` means amounts are not parsed (`status: "FormatNotConfigured"`) |
| `sourceCacheMinutes`, `maxSourceDelayMinutes` | EDSM caches each layer about 10 minutes, so **a payment can take up to about 20 minutes to appear** |
| `mappingVerifiedAtUtc`, `mappingSource` | When PACT last confirmed the accounts; `PactLookup` or `Cached` |

**Each company:**

| Field | Meaning |
|---|---|
| `companyId`, `companyName` | The company |
| `businessModel` | `Owned` or `Rented` |
| `status` | `Available`, `NotSupported`, `BusinessRuleRejected`, `ValidationRejected`, `Unauthorized`, `InvalidResponse` or `Unavailable` |
| `contracts[]` | The PACT contracts that confirmed the company; `unitType` shows Parking |
| `fields[]` | `key`, `label`, `definition`, `status` (`Provided`, `Missing`, `Empty`, `Unreadable`, `FormatNotConfigured`), `value` (null unless Provided, **never a substituted 0**), `raw` (EDSM's exact string) and `meaning` |
| `totalCheck` | `Consistent`, `Inconsistent` or `NotChecked` |
| `allZero` | True when every value is zero |
| `notes` | Warnings, such as an inconsistent total |
| `transactions[]` | The read-only transaction lists (when included) |
| `dueInstallments` | Only when enabled; status raw |

**Use `raw` to read amounts out to a caller.** It is EDSM's own
`#,##0.00` string. `value` is for arithmetic only, and nothing should add the
fields together.

### 3.1 Field definitions: owned vs rented (VERIFIED in EDSM C#; SQL UNVERIFIED)

| `key` | Owned (company 4, 32) | Rented (company 25, 7, 20) |
|---|---|---|
| `paidAmount` | Σ credits > 0 | posted receipts (excl. JVP/JRN/JVA/PDPV) − refunds (IPV/PPV) + fees paid (JRN) + opening-balance credit |
| `dueAmount` | unpaid remainder (Debit − Credit) of instalments due on or before EDSM's server date; instalments with no credit recorded are excluded | bounced cheques due (net of adjustments) + unpaid fees + opening-balance debit; **can be negative** |
| `outstandingAmount` (label "Not yet due" / "Post-dated cheques") | the same remainder, for instalments due after the server date | post-dated cheques (status `pdc`) |
| `lateFines` | late fines, **only when > 0**; `""` gives `meaning: "ZeroOrLess"` | **never computed**; `""` gives `meaning: "NotComputedForRented"` |
| `totalAmount` | paid + due + outstanding, **excluding late fines** | paid + due + outstanding |

### 3.2 Example: PACT customer with an owned and a rented company (Fixture)

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary?includeTransactions=false
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "mappingStatus": "Mapped",
  "mappingDetail": null,
  "pactTenantId": "3001",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.3164055Z",
  "sourceAsOfUtc": null,
  "currency": "AED",
  "currencySource": "Configured",
  "numberCulture": "en-US",
  "sourceCacheMinutes": 10,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "PactLookup",
  "companies": [
    {
      "companyId": 4,
      "companyName": "Tiger Group Dubai",
      "businessModel": "Owned",
      "status": "Available",
      "statusDetail": null,
      "contracts": [
        {
          "contractNumber": "88001",
          "externalUnitId": "41230",
          "unitNumber": "0304",
          "projectName": "Tiger Marina Residences",
          "unitType": "Residential"
        }
      ],
      "fields": [
        {
          "key": "paidAmount",
          "label": "Paid",
          "definition": "Sum of all credits received (rows with Credit > 0).",
          "status": "Provided",
          "value": 812500.00,
          "raw": "812,500.00",
          "meaning": null
        },
        {
          "key": "dueAmount",
          "label": "Due",
          "definition": "Unpaid remainder (Debit − Credit) of instalments whose cheque due date is on or before EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.",
          "status": "Provided",
          "value": 62500.00,
          "raw": "62,500.00",
          "meaning": null
        },
        {
          "key": "outstandingAmount",
          "label": "Not yet due",
          "definition": "Unpaid remainder (Debit − Credit) of instalments due after EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.",
          "status": "Provided",
          "value": 375000.00,
          "raw": "375,000.00",
          "meaning": null
        },
        {
          "key": "lateFines",
          "label": "Late fines",
          "definition": "EDSM late fines (debits minus credits on rows whose description contains \"fine\"), shown only when above zero.",
          "status": "Provided",
          "value": 1500.00,
          "raw": "1,500.00",
          "meaning": null
        },
        {
          "key": "totalAmount",
          "label": "Total",
          "definition": "Paid + due + not yet due. Excludes late fines.",
          "status": "Provided",
          "value": 1250000.00,
          "raw": "1,250,000.00",
          "meaning": null
        }
      ],
      "totalCheck": "Consistent",
      "allZero": false,
      "notes": [],
      "transactions": [],
      "dueInstallments": {
        "status": "Disabled",
        "statusDetail": "Due-installments is off (CollectionsSource:DueInstallmentsEnabled).",
        "fromDate": "2026-09-04",
        "toDate": "2026-11-05",
        "items": []
      }
    },
    {
      "companyId": 25,
      "companyName": "Hirmas Dubai",
      "businessModel": "Rented",
      "status": "Available",
      "statusDetail": null,
      "contracts": [
        {
          "contractNumber": "99002",
          "externalUnitId": "51200",
          "unitNumber": "1101",
          "projectName": "Hirmas Residence",
          "unitType": "Residential"
        }
      ],
      "fields": [
        {
          "key": "paidAmount",
          "label": "Paid",
          "definition": "Posted receipts (excluding JVP, JRN, JVA and PDPV vouchers), minus refunds (IPV, PPV), plus fees paid (JRN), plus any opening-balance credit.",
          "status": "Provided",
          "value": 60000.00,
          "raw": "60,000.00",
          "meaning": null
        },
        {
          "key": "dueAmount",
          "label": "Due",
          "definition": "Bounced cheques due on or before EDSM's server date (net of adjustments), plus unpaid fees, plus any opening-balance debit. Negative when fee payments exceed fee charges.",
          "status": "Provided",
          "value": -500.00,
          "raw": "-500.00",
          "meaning": null
        },
        {
          "key": "outstandingAmount",
          "label": "Post-dated cheques",
          "definition": "Post-dated cheques held (status pdc).",
          "status": "Provided",
          "value": 25000.00,
          "raw": "25,000.00",
          "meaning": null
        },
        {
          "key": "lateFines",
          "label": "Late fines",
          "definition": "Not computed by EDSM for rented companies.",
          "status": "Empty",
          "value": null,
          "raw": "",
          "meaning": "NotComputedForRented"
        },
        {
          "key": "totalAmount",
          "label": "Total",
          "definition": "Paid + due + post-dated cheques.",
          "status": "Provided",
          "value": 84500.00,
          "raw": "84,500.00",
          "meaning": null
        }
      ],
      "totalCheck": "Consistent",
      "allZero": false,
      "notes": [],
      "transactions": [],
      "dueInstallments": {
        "status": "Disabled",
        "statusDetail": "Due-installments is off (CollectionsSource:DueInstallmentsEnabled).",
        "fromDate": "2026-09-04",
        "toDate": "2026-11-05",
        "items": []
      }
    }
  ],
  "contractsWithoutCompany": []
}
```

With `includeTransactions` omitted, each company also carries `transactions[]`
(three lists), with the same items as §4.

### 3.3 Example: one company refused by EDSM (Fixture, simulated EDSM `403 Unauthorized client.` for company 25)

Each company keeps its own status, and the other company's figures are still returned:

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary?includeTransactions=false
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "mappingStatus": "Mapped",
  "mappingDetail": null,
  "pactTenantId": "3001",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:39.3320474Z",
  "sourceAsOfUtc": null,
  "currency": "AED",
  "currencySource": "Configured",
  "numberCulture": "en-US",
  "sourceCacheMinutes": 10,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:39.3320314Z",
  "mappingSource": "PactLookup",
  "companies": [
    {
      "companyId": 4,
      "companyName": "Tiger Group Dubai",
      "businessModel": "Owned",
      "status": "Available",
      "statusDetail": null,
      "contracts": [
        {
          "contractNumber": "88001",
          "externalUnitId": "41230",
          "unitNumber": "0304",
          "projectName": "Tiger Marina Residences",
          "unitType": "Residential"
        }
      ],
      "fields": [
        {
          "key": "paidAmount",
          "label": "Paid",
          "definition": "Sum of all credits received (rows with Credit > 0).",
          "status": "Provided",
          "value": 812500.00,
          "raw": "812,500.00",
          "meaning": null
        },
        {
          "key": "dueAmount",
          "label": "Due",
          "definition": "Unpaid remainder (Debit − Credit) of instalments whose cheque due date is on or before EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.",
          "status": "Provided",
          "value": 62500.00,
          "raw": "62,500.00",
          "meaning": null
        },
        {
          "key": "outstandingAmount",
          "label": "Not yet due",
          "definition": "Unpaid remainder (Debit − Credit) of instalments due after EDSM's server date. Instalments with no credit recorded at all are excluded by EDSM.",
          "status": "Provided",
          "value": 375000.00,
          "raw": "375,000.00",
          "meaning": null
        },
        {
          "key": "lateFines",
          "label": "Late fines",
          "definition": "EDSM late fines (debits minus credits on rows whose description contains \"fine\"), shown only when above zero.",
          "status": "Provided",
          "value": 1500.00,
          "raw": "1,500.00",
          "meaning": null
        },
        {
          "key": "totalAmount",
          "label": "Total",
          "definition": "Paid + due + not yet due. Excludes late fines.",
          "status": "Provided",
          "value": 1250000.00,
          "raw": "1,250,000.00",
          "meaning": null
        }
      ],
      "totalCheck": "Consistent",
      "allZero": false,
      "notes": [],
      "transactions": [],
      "dueInstallments": {
        "status": "Disabled",
        "statusDetail": "Due-installments is off (CollectionsSource:DueInstallmentsEnabled).",
        "fromDate": "2026-09-04",
        "toDate": "2026-11-05",
        "items": []
      }
    },
    {
      "companyId": 25,
      "companyName": "Hirmas Dubai",
      "businessModel": "Rented",
      "status": "Unauthorized",
      "statusDetail": "EDSM rejected the configured API key.",
      "contracts": [
        {
          "contractNumber": "99002",
          "externalUnitId": "51200",
          "unitNumber": "1101",
          "projectName": "Hirmas Residence",
          "unitType": "Residential"
        }
      ],
      "fields": [],
      "totalCheck": "NotChecked",
      "allZero": false,
      "notes": [],
      "transactions": [],
      "dueInstallments": null
    }
  ],
  "contractsWithoutCompany": []
}
```

## 4. `GET payment-transactions` (payment history, read-only)

```
GET /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions?companyId={companyId}&type={type}
Authorization: Bearer {access_token}
```

| Query | Required | Values |
|---|---|---|
| `companyId` | yes | one of the `companies[].companyId` from the summary |
| `type` | yes | `Paid` / `1`, `Due` / `2`, `Outstanding` / `3` (case-insensitive) |

- **Type All is blocked.** `type=All` or `4` is **refused with 400**, before
  any PACT or EDSM call. For rented companies, EDSM writes to its databases on
  that path.
- **No total is returned.** For rented companies, EDSM's Paid list contains
  refunds as positive amounts, and its Due total has a fee-allocation defect;
  each case carries a `caveat`. Never derive a balance from these lists; use
  §3.
- **Item fields:**
  - `amount` is EDSM's raw number, rounded to 2 dp;
  - `formattedRaw` is EDSM's string (rented Due/Fees rows end in `" AED"`);
  - `date` is parsed from `dateRaw` (`dd-MMM-yyyy`), and is null for
    opening-balance and contract rows;
  - `paymentType` is `Cash`, `Cheque`, `Fees`, `Opening balance` or
    `Current contract amount` (EDSM `PaymentTypeEnum`), and is always null for owned companies;
  - `paymentTypeId` is EDSM's raw id (1–5), or null when EDSM sent none.

### 4.1 Owned: Paid / Due / Outstanding (Fixture)

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=4&type=Paid
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 4,
  "companyName": "Tiger Group Dubai",
  "businessModel": "Owned",
  "transactionType": "Paid",
  "transactionTypeId": 1,
  "caveat": null,
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.482472Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 500000,
      "formattedStatus": "Provided",
      "formattedRaw": "500,000.00",
      "date": "2026-01-15",
      "dateRaw": "15-Jan-2026",
      "chequeNumber": null,
      "paymentType": null,
      "paymentTypeId": null
    },
    {
      "amount": 312500,
      "formattedStatus": "Provided",
      "formattedRaw": "312,500.00",
      "date": "2026-06-15",
      "dateRaw": "15-Jun-2026",
      "chequeNumber": null,
      "paymentType": null,
      "paymentTypeId": null
    }
  ]
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=4&type=Due
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 4,
  "companyName": "Tiger Group Dubai",
  "businessModel": "Owned",
  "transactionType": "Due",
  "transactionTypeId": 2,
  "caveat": null,
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5495717Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 62500,
      "formattedStatus": "Provided",
      "formattedRaw": "62,500.00",
      "date": "2026-09-15",
      "dateRaw": "15-Sep-2026",
      "chequeNumber": "000412",
      "paymentType": null,
      "paymentTypeId": null
    }
  ]
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=4&type=Outstanding
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 4,
  "companyName": "Tiger Group Dubai",
  "businessModel": "Owned",
  "transactionType": "Outstanding",
  "transactionTypeId": 3,
  "caveat": null,
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5528581Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 187500,
      "formattedStatus": "Provided",
      "formattedRaw": "187,500.00",
      "date": "2026-12-15",
      "dateRaw": "15-Dec-2026",
      "chequeNumber": null,
      "paymentType": null,
      "paymentTypeId": null
    },
    {
      "amount": 187500,
      "formattedStatus": "Provided",
      "formattedRaw": "187,500.00",
      "date": "2027-03-15",
      "dateRaw": "15-Mar-2027",
      "chequeNumber": null,
      "paymentType": null,
      "paymentTypeId": null
    }
  ]
}
```

### 4.2 Rented: Paid / Due / Outstanding (Fixture)

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=25&type=Paid
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 25,
  "companyName": "Hirmas Dubai",
  "businessModel": "Rented",
  "transactionType": "Paid",
  "transactionTypeId": 1,
  "caveat": "Refunds appear in this list as positive payments (an EDSM defect), so the list is not a ledger of payments received.",
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5559371Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 60000,
      "formattedStatus": "Provided",
      "formattedRaw": "60,000.00",
      "date": "2026-02-01",
      "dateRaw": "01-Feb-2026",
      "chequeNumber": "100201",
      "paymentType": "Cheque",
      "paymentTypeId": 2
    }
  ]
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=25&type=Due
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 25,
  "companyName": "Hirmas Dubai",
  "businessModel": "Rented",
  "transactionType": "Due",
  "transactionTypeId": 2,
  "caveat": "EDSM's total for this list can differ from the summary's due amount (a fee-allocation defect). No total is taken from it.",
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5585794Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 1000,
      "formattedStatus": "Provided",
      "formattedRaw": "1,000.00 AED",
      "date": "2026-03-01",
      "dateRaw": "01-Mar-2026",
      "chequeNumber": null,
      "paymentType": "Fees",
      "paymentTypeId": 3
    }
  ]
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=25&type=3
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "ext:Pact:3001",
  "pactTenantId": "3001",
  "companyId": 25,
  "companyName": "Hirmas Dubai",
  "businessModel": "Rented",
  "transactionType": "Outstanding",
  "transactionTypeId": 3,
  "caveat": null,
  "currency": "AED",
  "currencySource": "Configured",
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5608998Z",
  "sourceAsOfUtc": null,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": "2026-10-05T16:52:38.3147927Z",
  "mappingSource": "Cached",
  "items": [
    {
      "amount": 25000,
      "formattedStatus": "Provided",
      "formattedRaw": "25,000.00",
      "date": "2026-12-01",
      "dateRaw": "01-Dec-2026",
      "chequeNumber": "100205",
      "paymentType": "Cheque",
      "paymentTypeId": 2
    }
  ]
}
```

## 5. Errors

Every error body is RFC 7807 ProblemDetails with `code` and `message`
extensions (`traceId` is omitted below).

| Case | Status | `code` |
|---|---|---|
| No or invalid bearer token | 401 | (no body) |
| No financial-read permission | 403 | `Forbidden` |
| Malformed `customerKey` | 400 | `InvalidRequest` |
| `type` missing, unknown, or `All`/`4`; `companyId` missing | 400 | `InvalidRequest` |
| Customer unknown to TigerCS or not visible | 404 | `AccountNotFound` |
| `companyId` not among the customer's PACT contracts | 404 | `AccountNotFound` |
| **Unmapped** customer (CRM, phone-only, no PACT contracts): summary | **200** | `mappingStatus: "NotMapped"` with `mappingDetail`, and no figures |
| **Unmapped** customer: transactions | **422** | `CustomerNotMapped` |
| PACT unreachable (the account cannot be confirmed) | 503 | `FinanceUnavailable` |
| **EDSM error** (invalid source response, key rejected, 500, unreachable): summary | 200 | the company's `status` (e.g. `Unauthorized`, `InvalidResponse`, `Unavailable`) with `statusDetail`, and no figures for that company |
| **EDSM error**: transactions | 503 | `FinanceUnavailable` (detail names the EDSM outcome) |
| Collections switched off | 503 | `CollectionsDisabled` |

### 5.1 Examples (Fixture)

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary
Authorization: Bearer {access_token}

--> 401
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary
Authorization: Bearer {access_token}

--> 403
{
  "type": "https://tigercs.internal/problems/collections/Forbidden",
  "title": "Forbidden",
  "status": 403,
  "detail": "Viewing customer payments requires the Collections financial-read permission.",
  "code": "Forbidden",
  "message": "Viewing customer payments requires the Collections financial-read permission."
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=25&type=All
Authorization: Bearer {access_token}

--> 400
{
  "type": "https://tigercs.internal/problems/collections/InvalidRequest",
  "title": "InvalidRequest",
  "status": 400,
  "detail": "type All (4) is not supported: for rented companies it writes to EDSM's databases. Use Paid (1), Due (2) or Outstanding (3).",
  "code": "InvalidRequest",
  "message": "type All (4) is not supported: for rented companies it writes to EDSM's databases. Use Paid (1), Due (2) or Outstanding (3)."
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=32&type=Paid
Authorization: Bearer {access_token}

--> 404
{
  "type": "https://tigercs.internal/problems/collections/AccountNotFound",
  "title": "AccountNotFound",
  "status": 404,
  "detail": "Company 32 is not among this customer's confirmed PACT contracts.",
  "code": "AccountNotFound",
  "message": "Company 32 is not among this customer's confirmed PACT contracts."
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A777777/payment-summary
Authorization: Bearer {access_token}

--> 404
{
  "type": "https://tigercs.internal/problems/collections/AccountNotFound",
  "title": "AccountNotFound",
  "status": 404,
  "detail": "Customer not found, or not visible to you.",
  "code": "AccountNotFound",
  "message": "Customer not found, or not visible to you."
}
```

**Unmapped CRM customer.** The summary returns no financial data:

```http
GET /api/genesys/collections/customers/by-key/crm%3A9001/payment-summary
Authorization: Bearer {access_token}

--> 200
{
  "customerKey": "crm:9001",
  "mappingStatus": "NotMapped",
  "mappingDetail": "This customer is identified by Tiger CRM (customerId 9001). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists.",
  "pactTenantId": null,
  "source": "Fixture",
  "retrievedAtUtc": "2026-10-05T16:52:38.5635109Z",
  "sourceAsOfUtc": null,
  "currency": "AED",
  "currencySource": "Configured",
  "numberCulture": "en-US",
  "sourceCacheMinutes": 10,
  "maxSourceDelayMinutes": 20,
  "mappingVerifiedAtUtc": null,
  "mappingSource": null,
  "companies": [],
  "contractsWithoutCompany": []
}
```

```http
GET /api/genesys/collections/customers/by-key/crm%3A9001/payment-transactions?companyId=4&type=Paid
Authorization: Bearer {access_token}

--> 422
{
  "type": "https://tigercs.internal/problems/collections/CustomerNotMapped",
  "title": "CustomerNotMapped",
  "status": 422,
  "detail": "This customer is identified by Tiger CRM (customerId 9001). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists.",
  "code": "CustomerNotMapped",
  "message": "This customer is identified by Tiger CRM (customerId 9001). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists."
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A5005/payment-summary
Authorization: Bearer {access_token}

--> 503
{
  "type": "https://tigercs.internal/problems/collections/FinanceUnavailable",
  "title": "FinanceUnavailable",
  "status": 503,
  "detail": "PACT could not be reached to confirm the customer's accounts, so EDSM figures are unavailable.",
  "code": "FinanceUnavailable",
  "message": "PACT could not be reached to confirm the customer's accounts, so EDSM figures are unavailable."
}
```

**Invalid source (simulated EDSM HTTP 500 for company 25):**

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-transactions?companyId=25&type=Paid
Authorization: Bearer {access_token}

--> 503
{
  "type": "https://tigercs.internal/problems/collections/FinanceUnavailable",
  "title": "FinanceUnavailable",
  "status": 503,
  "detail": "EDSM payment-transactions failed (Unavailable): EDSM returned HTTP 500 (server error, no envelope).",
  "code": "FinanceUnavailable",
  "message": "EDSM payment-transactions failed (Unavailable): EDSM returned HTTP 500 (server error, no envelope)."
}
```

```http
GET /api/genesys/collections/customers/by-key/ext%3APact%3A3001/payment-summary
Authorization: Bearer {access_token}

--> 503
{
  "type": "https://tigercs.internal/problems/collections/CollectionsDisabled",
  "title": "CollectionsDisabled",
  "status": 503,
  "detail": "Collections is not enabled (Collections:Enabled is false).",
  "code": "CollectionsDisabled",
  "message": "Collections is not enabled (Collections:Enabled is false)."
}
```

## 6. Currency and freshness

- **Currency.** EDSM returns no currency field. TigerCS labels amounts with
  `CollectionsSource:Currency` (`AED`) and says so (`currencySource:
  "Configured"`). Say "dirhams" only because it is configured, never because
  EDSM said so.
- **Freshness.** EDSM caches in memory (about 10 minutes per layer, nested),
  so a payment posted in PACT can take **up to about 20 minutes** to appear.
  Re-calling does not bypass the cache, and there is no as-of time.
  - Do not tell a caller a payment "has not been received" based on a figure
    retrieved within 20 minutes of the payment.

## 7. Reminder APIs (separate from the financial reads)

**Two different things are implemented here.** Keep them apart:

| Area | Status |
|---|---|
| **Ticket-response handling:** `POST …/reminders/{reminderId}/outcomes` records delivery and customer responses, creates or reuses a ticket by `conversationId`, raises a verification follow-up for "already paid" and human handoff for "requested human" or "AI disconnected", and retries durably | **Implemented and tested** (fixture). Never posts a payment, never closes a ticket |
| **Financial reminder eligibility:** `reminders/candidates`, `POST reminders` | Implemented over the **CRM per-account source only**, which has **no real implementation**. Candidates answer `503 FinanceUnavailable` in real environments |
| **Automatic sending** | **Disabled**: `SchedulerOwner: "None"`, `BusinessRulesConfirmed: false`, all channels `false` |

**EDSM does not feed reminder eligibility**
([`Collections-Integration.md`](Collections-Integration.md) §2.2):
- the summary has no due dates;
- the status values from due-installments are unverified;
- EDSM data can be up to about 20 minutes stale, with no cache bypass.

**Routes, in order of use:**

| # | Method | Route | Auth | Purpose |
|---|---|---|---|---|
| 1 | GET | `reminders/candidates?reminderType=OverdueMonthly\|CurrentMonth\|MonthEndFollowUp&businessDate=&crmCustomerId=&accountId=&cursor=&pageSize=` | reminder-send | eligible accounts for an open window, each with a `candidateId` (valid 15 minutes) |
| 2 | POST | `reminders` (header `Idempotency-Key`) | reminder-send; VoiceBot integration only | queue a reminder for a candidate. **202** queued, **200** replay, **409** `CandidateChanged` (with `replacementCandidate`) / `IdempotencyConflict`, **422** `ChannelNotEnabled` / `NoEligibleContact` |
| 3 | POST | `reminders/{reminderId}/outcomes` | integration account | delivery status and customer response; **200**, or **202** while the ticket is pending |
| 4 | GET | `customers/{crmCustomerId}/reminders` | financial-read | reminder history with linked tickets |

The examples below were **captured earlier from the implemented API with the
CRM per-account Fixture source** (customer 9001). They show the contract.
In real environments step 1 answers `503` until a per-account source exists.

### 7.1 Candidates (Fixture)

```http
GET /api/genesys/collections/reminders/candidates?reminderType=OverdueMonthly&businessDate=2026-10-02&pageSize=50

--> 200
{
  "reminderType": "OverdueMonthly",
  "cycleKey": "2026-10:OverdueMonthly",
  "businessDate": "2026-10-02",
  "timeZone": "Asia/Dubai",
  "windowOpen": true,
  "items": [
    {
      "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
      "crmCustomerId": 9001,
      "accountId": "ACC-9001-1204",
      "unitId": 9200,
      "towerName": "Tiger Tower A",
      "unitNumber": "1204",
      "currency": "AED",
      "reminderAmount": 4000.00,
      "amountBasis": "UnpaidPrincipalOlderThanOneCalendarMonth",
      "instalmentIds": [
        "INS-1204-02"
      ],
      "oldestUnpaidDueDate": "2026-08-10",
      "availableChannels": [
        "VoiceBot",
        "Email"
      ],
      "asOfUtc": "2026-10-02T06:00:00Z",
      "expiresAtUtc": "2026-10-02T06:15:00Z"
    }
  ],
  "nextCursor": null
}
```

### 7.2 Queue (Fixture; VoiceBot by the integration account)

```http
POST /api/genesys/collections/reminders
Idempotency-Key: collection-oct-2026-acc9001-1204-overdue
{
  "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
  "channels": [
    "VoiceBot",
    "Email"
  ],
  "language": "en"
}
--> 202
{
  "reminderId": "REM-1",
  "crmCustomerId": 9001,
  "accountId": "ACC-9001-1204",
  "reminderType": "OverdueMonthly",
  "cycleKey": "2026-10:OverdueMonthly",
  "currency": "AED",
  "reminderAmount": 4000.00,
  "amountBasis": "UnpaidPrincipalOlderThanOneCalendarMonth",
  "instalmentIds": [
    "INS-1204-02"
  ],
  "queuedAtUtc": "2026-10-02T06:00:00Z",
  "status": "Queued",
  "channels": [
    {
      "channel": "VoiceBot",
      "status": "Queued",
      "lastEventAtUtc": null,
      "attempts": 1,
      "statusReason": null
    },
    {
      "channel": "Email",
      "status": "Queued",
      "lastEventAtUtc": null,
      "attempts": 1,
      "statusReason": null
    }
  ]
}
```

```http
POST /api/genesys/collections/reminders
Idempotency-Key: collection-oct-2026-acc9001-1204-overdue
{
  "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
  "channels": [
    "VoiceBot",
    "Email"
  ],
  "language": "en"
}
--> 200
{
  "reminderId": "REM-1",
  "crmCustomerId": 9001,
  "accountId": "ACC-9001-1204",
  "reminderType": "OverdueMonthly",
  "cycleKey": "2026-10:OverdueMonthly",
  "currency": "AED",
  "reminderAmount": 4000.00,
  "amountBasis": "UnpaidPrincipalOlderThanOneCalendarMonth",
  "instalmentIds": [
    "INS-1204-02"
  ],
  "queuedAtUtc": "2026-10-02T06:00:00Z",
  "status": "Queued",
  "channels": [
    {
      "channel": "VoiceBot",
      "status": "Queued",
      "lastEventAtUtc": null,
      "attempts": 1,
      "statusReason": null
    },
    {
      "channel": "Email",
      "status": "Queued",
      "lastEventAtUtc": null,
      "attempts": 1,
      "statusReason": null
    }
  ]
}
```

```http
POST /api/genesys/collections/reminders
Idempotency-Key: another-key
{
  "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
  "channels": [
    "Email"
  ],
  "language": "en"
}
--> 409
{
  "type": "https://tigercs.internal/problems/collections/CandidateChanged",
  "title": "CandidateChanged",
  "status": 409,
  "detail": "One or more requested channels were already used for this account in this cycle.",
  "code": "CandidateChanged",
  "message": "One or more requested channels were already used for this account in this cycle.",
  "replacementCandidate": {
    "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
    "crmCustomerId": 9001,
    "accountId": "ACC-9001-1204",
    "unitId": 9200,
    "towerName": "Tiger Tower A",
    "unitNumber": "1204",
    "currency": "AED",
    "reminderAmount": 4000.00,
    "amountBasis": "UnpaidPrincipalOlderThanOneCalendarMonth",
    "instalmentIds": [
      "INS-1204-02"
    ],
    "oldestUnpaidDueDate": "2026-08-10",
    "availableChannels": [],
    "asOfUtc": "2026-10-02T06:00:00Z",
    "expiresAtUtc": "2026-10-02T06:15:00Z"
  }
}
```

```http
POST /api/genesys/collections/reminders
{
  "candidateId": "CAND-eyJDcm1DdXN0b21lcklkIjo5MDAxLCJBY2NvdW50SWQiOiJBQ0MtOTAwMS0xMjA0IiwiVHlwZSI6MSwiQ3ljbGVLZXkiOiIyMDI2LTEwOk92ZXJkdWVNb250aGx5IiwiQW1vdW50Ijo0MDAwLCJDdXJyZW5jeSI6IkFFRCIsIkluc3RhbG1lbnRJZHMiOlsiSU5TLTEyMDQtMDIiXSwiRXhwaXJlc0F0VXRjIjoiMjAyNi0xMC0wMlQwNjoxNTowMFoifQ",
  "channels": [
    "Sms"
  ]
}
--> 422
{
  "type": "https://tigercs.internal/problems/collections/ChannelNotEnabled",
  "title": "ChannelNotEnabled",
  "status": 422,
  "detail": "Reminder channel(s) not enabled: Sms.",
  "code": "ChannelNotEnabled",
  "message": "Reminder channel(s) not enabled: Sms."
}
```

### 7.3 Outcomes: ticket-response handling (Fixture)

```http
POST /api/genesys/collections/reminders/REM-1/outcomes
Idempotency-Key: genesys-event-evt90001
{
  "eventId": "EVT-90001",
  "channel": "VoiceBot",
  "providerMessageId": "GEN-90001",
  "conversationId": "11111111-2222-3333-4444-555555555555",
  "occurredAtUtc": "2026-10-02T06:05:00Z",
  "deliveryStatus": "Answered",
  "customerResponded": true,
  "customerIntent": "AlreadyPaid",
  "requiresHumanFollowUp": true
}
--> 200
{
  "reminderId": "REM-1",
  "eventId": "EVT-90001",
  "result": "Recorded",
  "deliveryStatus": "Answered",
  "channelStatus": "Answered",
  "ticketId": 1,
  "ticketNumber": "TG-C38EF3B-20261005-0001",
  "ticketResult": "Created",
  "followUpRequired": true,
  "replayed": false
}
```

```http
POST /api/genesys/collections/reminders/REM-1/outcomes
Idempotency-Key: genesys-event-evt90001
{
  "eventId": "EVT-90001",
  "channel": "VoiceBot",
  "providerMessageId": "GEN-90001",
  "conversationId": "11111111-2222-3333-4444-555555555555",
  "occurredAtUtc": "2026-10-02T06:05:00Z",
  "deliveryStatus": "Answered",
  "customerResponded": true,
  "customerIntent": "AlreadyPaid",
  "requiresHumanFollowUp": true
}
--> 200
{
  "reminderId": "REM-1",
  "eventId": "EVT-90001",
  "result": "Recorded",
  "deliveryStatus": "Answered",
  "channelStatus": "Answered",
  "ticketId": 1,
  "ticketNumber": "TG-C38EF3B-20261005-0001",
  "ticketResult": "Created",
  "followUpRequired": true,
  "replayed": true
}
```

```http
POST /api/genesys/collections/reminders/REM-1/outcomes
{
  "eventId": "EVT-90002",
  "channel": "VoiceBot",
  "providerMessageId": "GEN-90002",
  "occurredAtUtc": "2026-10-02T06:04:00Z",
  "deliveryStatus": "NoAnswer"
}
--> 200
{
  "reminderId": "REM-1",
  "eventId": "EVT-90002",
  "result": "Recorded",
  "deliveryStatus": "NoAnswer",
  "channelStatus": "Answered",
  "ticketId": null,
  "ticketNumber": null,
  "ticketResult": "NotRequired",
  "followUpRequired": false,
  "replayed": false
}
```

```http
POST /api/genesys/collections/reminders/REM-1/outcomes
{
  "eventId": "EVT-90003",
  "channel": "VoiceBot",
  "providerMessageId": "GEN-90003",
  "conversationId": "22222222-3333-4444-5555-666666666666",
  "occurredAtUtc": "2026-10-02T06:09:00Z",
  "customerResponded": true,
  "customerIntent": "PromiseToPay"
}
--> 202
{
  "reminderId": "REM-1",
  "eventId": "EVT-90003",
  "result": "Recorded",
  "deliveryStatus": null,
  "channelStatus": "Answered",
  "ticketId": null,
  "ticketNumber": null,
  "ticketResult": "Pending",
  "followUpRequired": false,
  "replayed": false
}
```

### 7.4 Reminder history (Fixture)

```http
GET /api/genesys/collections/customers/9001/reminders?accountId=ACC-9001-1204&pageSize=50

--> 200
{
  "crmCustomerId": 9001,
  "accountId": "ACC-9001-1204",
  "items": [
    {
      "reminderId": "REM-1",
      "accountId": "ACC-9001-1204",
      "reminderType": "OverdueMonthly",
      "cycleKey": "2026-10:OverdueMonthly",
      "currency": "AED",
      "reminderAmount": 4000.00,
      "amountBasis": "UnpaidPrincipalOlderThanOneCalendarMonth",
      "queuedAtUtc": "2026-10-02T06:00:00Z",
      "trigger": "Integration",
      "channels": [
        {
          "channel": "VoiceBot",
          "status": "Answered",
          "lastEventAtUtc": "2026-10-02T06:05:00Z",
          "attempts": 1,
          "statusReason": null
        },
        {
          "channel": "Email",
          "status": "Sent",
          "lastEventAtUtc": "2026-10-02T06:00:00Z",
          "attempts": 1,
          "statusReason": null
        }
      ],
      "customerIntent": "PromiseToPay",
      "ticketId": 2,
      "ticketNumber": "TG-C38EF3B-20261005-0002",
      "responses": [
        {
          "eventId": "EVT-90001",
          "channel": "VoiceBot",
          "customerIntent": "AlreadyPaid",
          "occurredAtUtc": "2026-10-02T06:05:00Z",
          "followUpRequired": true,
          "verificationFollowUpRequired": true,
          "ticketResult": "Created",
          "ticketId": 1,
          "ticketNumber": "TG-C38EF3B-20261005-0001"
        },
        {
          "eventId": "EVT-90003",
          "channel": "VoiceBot",
          "customerIntent": "PromiseToPay",
          "occurredAtUtc": "2026-10-02T06:09:00Z",
          "followUpRequired": false,
          "verificationFollowUpRequired": false,
          "ticketResult": "Created",
          "ticketId": 2,
          "ticketNumber": "TG-C38EF3B-20261005-0002"
        }
      ]
    }
  ],
  "nextCursor": null
}
```

## 7a. Tests through the Genesys route (Bearer)

`src/TigerCS.Tests/Collections/Edsm/EdsmPaymentSummaryApiTests.cs` runs the
real TigerCS host with:
- authentication, the controllers and DI;
- the Fixture EDSM provider and the mock PACT gateway;
- a stand-in Customer Directory read, so no tickets need seeding.

Each request carries `Authorization: Bearer {TigerCS JWT}`:

| Test | Asserts |
|---|---|
| `Genesys_IntegrationServiceAccount_WithBearer_ReadsSummaryAndHistory_AndHoldsTheIntegrationGrant` | A CS Agent listed in `IntegrationEmployeeIds` gets 200 on both routes and holds the integration grant |
| `Genesys_PaymentSummary_IsTheSameEdsmServiceAsThePaymentTab` | The Genesys and Web prefixes return the same EDSM figures and share one PACT mapping |
| `Genesys_PaymentTransactions_ReadOnlyTypes` | `Paid`, `2` and `outstanding` all return 200 |
| `Genesys_PaymentTransactions_TypeAllAndUnknownTypes_Are400_WithoutAnyLookup` | `All`, `4`, empty and `5` return 400, and PACT is never called |
| `Genesys_PaymentTransactions_ForACompanyOutsideThePactContracts_Is404` | A company outside the tenant's contracts returns 404 |
| `Genesys_UnmappedCrmCustomer_GetsNoFinancialData` | `crm:9001` gets a summary of `NotMapped`, history 422 `CustomerNotMapped`, and no PACT call |
| `Genesys_WithoutAToken_Is401_AndWithoutFinancialRead_Is403`, `Genesys_AnInvalidBearerToken_Is401_OnBothRoutes`, `Genesys_AnUnlistedAccountWithoutFinancialRead_Is403_WithTheCode` | 401 and 403 |

These tests prove TigerCS's routes and contracts. They do **not** prove
TigerGroupWeb's forwarding, or anything about a real EDSM or UAT data.

## 8. TigerCS configuration (financial reads only)

| Key | Value |
|---|---|
| `Collections:Enabled` | `true` |
| `CollectionsSource:EdsmProvider` | `Pact` (`Fixture` is refused outside Development/Testing) |
| `CollectionsSource:EdsmNumberCulture` | the verified EDSM host culture (e.g. `en-US`); blank means no amounts are parsed |
| `CollectionsSource:Currency` | `AED` |
| `Pact:Provider` | `Http` |
| `PactApi:BaseUrl`, `PactApi:ApiKey` | the PACT/EDSM base URL; the key from the secret store |

**Keep these off:**
- `Collections:Channels:*` = `false`
- `Collections:SchedulerOwner` = `None`
- `Collections:BusinessRulesConfirmed` = `false`

The UAT steps are in [`EDSM-UAT-Guide.md`](EDSM-UAT-Guide.md).

## 9. TigerGroupWeb forwarding (pending)

TigerGroupWeb's source is not available to this work, so whether it forwards
these routes is **unverified**, and public URLs are **pending**.

It must forward, as it does the other `api/genesys` routes:

| Method | Path (forward unchanged to TigerCS) |
|---|---|
| GET | `/api/genesys/collections/customers/by-key/{customerKey}/payment-summary` |
| GET | `/api/genesys/collections/customers/by-key/{customerKey}/payment-transactions` |
| GET | `/api/genesys/collections/reminders/candidates` |
| POST | `/api/genesys/collections/reminders` |
| POST | `/api/genesys/collections/reminders/{reminderId}/outcomes` |
| GET | `/api/genesys/collections/customers/{crmCustomerId}/reminders` |
| GET | `/api/genesys/collections/customers/{crmCustomerId}/outstanding`, `/payments` (existing clients only) |

**Requirements:**
- **Path and query.** Keep them byte-for-byte, including the **encoded**
  `customerKey` (`ext%3APact%3A3001`). Do not decode `%3A` before forwarding.
- **Headers.** Forward `Idempotency-Key` on `POST reminders` and
  `POST …/outcomes`, and keep the `Content-Type: application/json` request
  body unchanged.
- **Authentication.** Authenticate to TigerCS as the CS Agent service account.
  For outcomes and VoiceBot queueing, that account's employee ID must be in
  `Collections:Authorization:IntegrationEmployeeIds`.
- **Responses.** Pass status codes and bodies through unchanged, including
  ProblemDetails (`400`, `403`, `404`, `409`, `422`, `503`) and `202`.
- **Timeout.** Allow at least **35 s** for the financial reads. TigerCS's
  EDSM client times out at 30 s, and a summary call makes one PACT lookup
  per phone on a cache miss plus one EDSM call per company.
