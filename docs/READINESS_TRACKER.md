# ApexERP readiness tracker

Maps every section of *APEXERP: Enterprise Readiness and Accounting Controls Enhancement Specification v1.0* to the repository, and says how each item was checked.

| Mark | Meaning |
|---|---|
| ✔ **Verified** | Executed in the build environment: unit tests, real PostgreSQL 16, shell scripts, or the frontend type-check, tests and production build |
| 🟢 **Built** | Implemented, wired into the API/UI, and type-checked against the real entity model. It runs for the first time on your machine (`dotnet build`, `dotnet test`, then use). It was **not executed** against EF Core and PostgreSQL here |
| ⛔ **Needs input** | Cannot be completed from here: it depends on something only you or a third party holds |

## Current branch verification (27 November 2026)

The following evidence supersedes older historical statements in this tracker where they conflict.

| Check | Current result |
|---|---|
| Backend build and unit/PostgreSQL integration tests | **Pass** on commit `f0c6672311776a03b9acc9da3382850a136b3951`, workflow [#46046590970](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46046590970). This includes the new payment retry concurrency and invalid-signature/provider-normalisation tests. |
| Frontend type-check, tests and production build | **Pass** on workflow #46049897283, including the FX-rate source UI and consolidation translation-evidence table. |
| EF Core migration drift, clean database install, hardening scripts and RLS runtime boot | **Pass** on workflow #46046590970. No manual migration generation is required for the current committed model. |
| PostgreSQL integration scenarios | **Executed in CI** against real PostgreSQL, including organisation hierarchy, public-sector fund/commitment scenarios, payment webhook concurrency and tenant evidence boundaries. |
| Dashboard operations-count change | **Pass** on workflow [#46046778338](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46046778338). |
| Current code verification | **Pass** on commit `4b63451f059b55a8ff758c934eb33e248a8990aa`, workflow [#46051768615](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46051768615): backend build and **298/298 unit/PostgreSQL tests**, frontend type-check/tests/build, migration drift, clean install, hardening, RLS runtime boot and restore checks all passed. Includes FX provenance/CTA and same-day override precedence; public-sector GL/lapse tests; atomic/concurrent GRN tests; AP invoice commitment realisation; payment retry concurrency; and bounded lifecycle dispatch retries. |
| Production Vercel deployment | **Not verified**. The earlier deployment attempt was rate-limited by the provider; retry is required after the provider limit expires. |
| Production backup, isolated restore, encryption/key custody and measured RPO/RTO | **Not verified**. CI restore checks are not evidence of the production environment's recovery objectives. |
| Live payment provider certification | **Not verified**. Provider-neutral signed webhook processing is implemented; provider-specific protocols, credentials and sandbox certification remain external prerequisites. |
| Approved jurisdiction statutory content | **Not verified**. The Zimbabwe 2026 baseline remains Draft / baseline-only and cannot be published until specialist legal review and complete schedules are supplied. |

## Part II: Accounting engine

| Spec | Item | Status | Where |
|---|---|---|---|
| GL-001, 003, 005, 006, 008 | Balanced, open period, numbering, immutability, atomic posting | ✔ existing + database triggers verified | `GlPostingService`, `001_append_only_guards.sql` |
| GL-002 | Postable accounts and per-source restrictions | 🟢 | `GlAccount.IsPostable/AllowedSourcesCsv`, `PUT gl/accounts/{id}/posting-rules` |
| GL-004 | Rate validation vs rate on file, max age, rounding-difference account | 🟢 (company settings `gl.fx.*`) | `GlPostingService.ApplyFxControlsAsync` |
| IAS 21 FX evidence | Effective direct/inverse rate, effective date, source and source reference retained in rate records; revaluation preview/journal and consolidation result expose applied quote evidence | ✔ 293 backend tests plus frontend and migration jobs pass in #46050356711, including PostgreSQL FX provenance, CTA balance and same-day manual-override timestamp precedence; period alignment and intercompany elimination acceptance remain open | `ExchangeRate`, `FxRevaluationService`, `ConsolidationService`, GL FX Rates UI |
| GL-007 | Idempotent posting | 🟢 `Idempotency-Key` header on any POST, stored in the same transaction; fingerprint ✔ | `IdempotencyFilter`, `IdempotencyRecord` |
| 4 | Accruals: create, approve, post, auto-reverse, match to invoice, ageing | 🟢 (+ integration tests) | `AccrualService`, Automation screen |
| 4.4 | Prepayments: schedule, approval, idempotent monthly posting | 🟢 (+ integration tests); maths ✔ | `PrepaymentService` |
| 5 | IFRS 15: contracts, price allocation, billing, contract liability/asset, point-in-time, modification | 🟢; allocation ✔ | `RevenueService` |
| 6 | IAS 36 impairment with evidence and approval; component link; capitalisation threshold setting | 🟢; calculation ✔ | `ImpairmentService`, `FixedAsset.ParentAssetId`, setting `assets.capitalisationThreshold` |

## Part III: Governance

| Spec | Item | Status | Where |
|---|---|---|---|
| 7 | Segregation of duties on journals and workflows | ✔ existing | |
| 7.3 | Conflict detection on role creation (blocks unless a justified exception is recorded), conflict report, tenant rules | 🟢; rules ✔ | `GovernanceService`, `AdminController`, Governance screen |
| 7.4 | Delegation, time-bound emergency access with independent review, recertification report | 🟢 | `ApprovalDelegation` |
| 8 | Approval matrix: limits by role, higher roles cover lower amounts, delegation honoured, applied to journals, accruals, prepayments, contracts, impairments, write-downs, credit notes, ECL, payment runs, petty cash | 🟢; matrix ✔ | `CheckApprovalAuthorityAsync` |
| 8.5 | Material-field change invalidates approval | ✔ rule; 🟢 used where re-checks occur (payment run release re-validates) | |
| 9 | Versioned, approved materiality; risk scoring; exception life-cycle with independent review and evidence for high/critical | 🟢; scoring and life-cycle rules ✔ | `ControlMonitoringService` |
| 15 | Continuous monitoring: 10 detectors, unique fingerprints, owners and due dates, nightly job | 🟢 | `RunDetectorsAsync`, `controls.detect` job |

## Part IV: Security and integrity

| Spec | Item | Status |
|---|---|---|
| 10 | Query filters on all tenant entities | ✔ existing (checked mechanically; new entities included) |
| 10.3 | Row-level security | ✔ SQL verified on PostgreSQL; 🟢 interceptor + cross-tenant scopes in code; opt-in |
| 11 | Account lockout with back-off | 🟢; policy ✔ |
| 11 | Rotating refresh tokens, reuse detection, revocation, logout, password change ends sessions, deactivation revokes sessions | 🟢 (+ integration tests) |
| 11 | MFA (TOTP, recovery codes, admin reset), encrypted seeds | 🟢; TOTP and encryption ✔ |
| 11 | HttpOnly refresh cookie, access token in memory only, no tokens in localStorage, CSRF header, silent renewal | 🟢 API; ✔ frontend tests |
| 11 | Rate limiting (sign-in, webhooks, global) and security headers (API and web) | ✔ pattern verified; 🟢 wired |
| 12 | Audit append-only at database level; client IP on every row; correlation id | ✔ triggers; 🟢 IP and correlation id |
| 12.3 | Evidence: upload with SHA-256, tamper warning on download, no delete | 🟢 `EvidenceService` |
| 13.4 | Statement-to-source drill-down (account postings, journal, source document, audit trail, evidence) | 🟢 Audit workspace |

## Part V: Reporting and intelligence

| Spec | Item | Status |
|---|---|---|
| 13 | Five-statement suite, integrity check | ✔ existing |
| 13.3 | Reconciliation of AP, AR, inventory, fixed assets, payroll, bank to the GL; explain and independent sign-off | 🟢 `ReconciliationService` |
| 13.5 | Accounting policy versions and policy stamp for reports | 🟢 |
| 14 | Executive dashboard: financial position (ageing, DSO, DPO), control health, period close, compliance, operations, with links | 🟢 |
| 24 | Reconciliation, period-close and audit workspaces; accessible tables, tabs and notices; CSV export with spreadsheet-injection protection | ✔ build and tests (export logic ✔) |

## Part VI: Modules

| Spec | Item | Status |
|---|---|---|
| 16 | Three-way match with tolerances; blocks posting and payment; override with authority and reason | 🟢; match ✔ |
| 16 | Duplicate invoices (database unique index, normalised numbers, amount+date detector) | 🟢 |
| 16 | Payment runs with independent authorisation, re-check at release | 🟢 |
| 16 | Supplier bank-detail change control with payment hold and independent verification | 🟢 |
| 16 | Supplier statement reconciliation; withholding-tax schedule | 🟢 |
| 16 | Create-supplier/customer endpoints no longer bind entities (over-posting fixed) | 🟢 |
| 17 | Credit limits (blocks posting, overrides with approval), credit notes with VAT, ECL matrix and provision with approval, customer statements | 🟢; credit and ECL maths ✔ |
| 18 | Bank reconciliation certification (preparer ≠ reviewer), petty cash (imprest, vouchers, replenishment), 13-week forecast | 🟢; forecast ✔ |
| 19 | IAS 2 NRV write-down, obsolescence, write-off with approval | 🟢; NRV ✔ |
| 20, 21 | Grants and public-sector budget rules | ✔ PostgreSQL tests cover fund hierarchy, atomic PO commitment, budget rejection/rollback, duplicate commitments, GRN item/quantity validation and atomic rollback, concurrent GRN over-receipt prevention, commitment remaining active after GRN, partial/final AP invoice realisation, over-realisation rejection, late bids, release, posted-GL actuals in the expenditure schedule, year-end carry-forward hierarchy and year-end lapse/repeat-close protection. Full government variance/reporting pack remains open. |

## Part VII: Architecture and database

| Spec | Item | Status |
|---|---|---|
| 22.3 | Correlation ids and structured request logging; API versioned (`/api/v1`) | 🟢 |
| 23.2 | Database constraints, append-only triggers, optional RLS | ✔ SQL |
| 23.3 | New entities and schema migrations | ✔ EF model snapshot, migration drift check and clean PostgreSQL install pass in CI; current FX provenance and lifecycle dispatch-attempt migrations are committed |
| 23.4 | Migration policy | 🟢 CI: drift check, clean install, hardening scripts twice, boot, backup round-trip. Stale `schema.sql` removed; `export-schema.sh` generates it |
| 26 | Background jobs | 🟢 `ScheduledJobs` (once per company per run key; retry ×3) |
| 27 | Pagination | 🟢 journals, AP and AR invoices (`page`/`pageSize`, old behaviour without them) |

## Part IX: Zimbabwe integrations

| Spec | Item | Status |
|---|---|---|
| 25.1 | Fiscal device key (persistent, loaded from `FDMS_DEVICE_KEY_PEM`), honest "not fiscalised" records when unset, verifiable signatures, gateway interface | 🟢 |
| 25.1 | Submission to ZIMRA | ⛔ needs the registered device, its key and ZIMRA's current API specification: implement `IFdmsGateway` |
| 25.2 | Statutory rates as effective-dated, approved configuration with source reference; payroll uses them (falls back to old defaults) | 🟢 |
| 25.3, 25.5 | Payment integration pipeline: HMAC/replay checks, duplicate-delivery protection, atomic receipt + GL + invoice allocation, retry claim/lease recovery, dead-letter queue and exception reconciliation | ✔ backend and PostgreSQL concurrency tests pass; provider-specific sandbox certification remains pending |
| 25.3 | Provider-specific formats (EcoCash, OneMoney, PayNow, ZimSwitch, bank APIs) and outbound payments | ⛔ provider-specific protocol adapters, credentials and sandbox tests still require each provider's specification; current signed-JSON adapter is a provider-neutral contract, not live certification |

## Part XI to XII: Testing and production

| Spec | Item | Status |
|---|---|---|
| 28.4 | Tests A to H as runtime self-tests (nightly) and in the integration suite | 🟢 |
| 28.5 | CI/CD: build, tests, vulnerability reports, audit gate, migration job, staged deploy with approval and automatic rollback | 🟢 workflows validated as YAML; need your GitHub Environments |
| 29 | Backup, encryption, retention, off-site hook, restore test, RPO/RTO evidence | ✔ CI backup/restore checks; ⛔ production encryption/key custody, isolated recovery and measured RPO/RTO remain deployment acceptance items |
| 29 | Production compose: health checks, limits, log rotation, database not published, backup service | 🟢 |
| 29 | Monitoring and runbooks | 🟢 `docs/OPERATIONS.md` |
| | npm advisories | Production: 1 high (PostCSS inside Next; fix is Next 16, a major upgrade). Dev only: Vitest chain (fix is Vitest 5, major). Left deliberately; upgrade and re-run tests |

## Acceptance (spec 31)

| ID | Status |
|---|---|
| AR-001 Balanced journals | 🟢 unit and integration tests exist; run them |
| AR-002 Atomic posting | 🟢 integration test exists; run it |
| AR-003 Duplicate prevention | 🟢 idempotency + duplicate invoice index |
| AR-004 Closed periods | 🟢 integration test exists; checklist added |
| AR-005 Tenant isolation | ✔ query filters/RLS checks and PostgreSQL evidence for company-scoped evidence and scheduled-job tenant context; broader files/exports/cache acceptance remains open |
| AR-006 Segregation of duties | 🟢 |
| AR-007 Approval integrity | 🟢 |
| AR-008 Statement reconciliation | 🟢 |
| AR-009 Sub-ledger reconciliation | 🟢 |
| AR-010 Audit evidence | 🟢 |
| AR-011 Database integrity | ✔ triggers, constraints; 🟢 migration job |
| AR-012 Automated schedules | 🟢 |
| AR-013 Backup and recovery | ✔ restore test; real RTO to be measured on your data |
| AR-014 Production integrations | ⛔ provider-specific sandbox certification, ZIMRA device registration and production SMTP/provider credentials remain external prerequisites |
| AR-015 Operational monitoring | 🟢 health, dashboard, correlation ids, runbooks |

**Classification (spec 32).** The consolidated branch has passing backend/PostgreSQL, frontend, migration and hardening CI evidence on the commits identified above. It is **not yet release-certified**: current-head CI must finish green, deployment-specific RPO/RTO and worker evidence must be supplied, provider-specific payment/fiscalisation integrations must be certified or explicitly scoped out, and jurisdiction packs require legal approval. Do not treat a green CI run as production deployment or statutory certification.
