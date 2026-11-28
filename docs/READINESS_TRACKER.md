# ApexERP readiness tracker

Maps every section of *APEXERP: Enterprise Readiness and Accounting Controls Enhancement Specification v1.0* to the repository, and says how each item was checked.

## Zimbabwe public procurement regulatory update (28 November 2026)

- Added `docs/ZIMBABWE_PUBLIC_PROCUREMENT_REQUIREMENTS.md`, mapping the PPDPA Act [Chapter 22:23] and PRAZ tendering guidance to the existing tender workflow.
- Implemented immutable audit events for tender creation, bid submission (including late rejection), evaluation start, bid scoring (before/after scores and weights), and approved award finalisation (selected bid and approval-workflow reference).
- Added PostgreSQL regression assertions for evaluation/award audit records, bid score before/after values, and late-bid submission evidence. CI verification is pending for this change.
- Remaining statutory controls include national/international minimum advertisement periods (20/40 days per PRAZ guidance), working-day calculation using an approved public-holiday calendar, public bid opening records, committee appointment/composition, proposed-award notifications and the 14-day standstill before contract signing, contract/performance-security records, award publication within one month, section 73 challenges, complete procurement-file retention and PRAZ e-GP integration. The Act defines a day as excluding weekends and public holidays; do not enforce a calendar-day approximation.
- The Act's requirements for proposed-award notification and 14-day standstill before contract signature are distinct from ApexERP's current internal award status. No e-GP integration or statutory publication is claimed.

## Zimbabwe IPSAS regulatory update (28 November 2026)

- Zimbabwe's current prescription framework is SI 137 of 2026, which replaces SI 41 of 2019. The 2026 regulations include IPSAS in the prescribed standards; IFAC still describes public-sector accrual IPSAS implementation as phased / partially adopted. See [IFAC's Zimbabwe jurisdiction profile](https://www.ifac.org/about-ifac/membership/profile/zimbabwe).
- The [IPSASB 2026 Handbook](https://www.ipsasb.org/publications/2026-handbook-international-public-sector-accounting-standards-board-pronouncements) is now the current international handbook reference (standards published through 31 January 2026).
- Added `docs/ZIMBABWE_IPSAS_REPORTING_REQUIREMENTS.md` to map the Treasury Circular No. 3 of 2025 reporting set and supporting schedules to existing ApexERP capabilities, with explicit implementation gaps. The circular is for the 2024 reporting year and is not asserted to be the current 2026 submission instruction.
- The Zimbabwe 2026 statutory-pack baseline already contains ZIMRA's published ZWG monthly PAYE bands. Added a PostgreSQL regression that pins all six monthly thresholds, rates and deductions to the official ZIMRA source URL. The pack remains Draft/baseline-only; this test does not constitute legal approval or authorise activation.
- Extended the same PostgreSQL regression to pin all six 2026 USD monthly PAYE thresholds, rates and deductions to ZIMRA's USD table as well. ZIMRA separately states that mixed ZWG/USD remuneration uses the USD tax tables and tax due is apportioned; the exact conversion/apportionment policy remains an explicit unimplemented statutory gap, not an assumed calculation. See [ZIMRA PAYE guidance](https://zimra.co.zw/domestic-taxes/individual/pay-as-you-earn-paye).
- IPSAS 24 first implementation slice added: versioned original/revised budgets, revenue and expense budget lines, independent approval, authority/amendment metadata, original/final/actual comparison endpoint, Public Sector budget-entry/statement UI and CSV export. Accrual GL actuals are only labelled comparable when reporting metadata matches; cash-basis budgets do not reuse accrual actuals. See `docs/IPSAS24_IMPLEMENTATION.md`. Approved reconciliation-row entry/independent approval is now implemented for basis, timing, entity and presentation differences. Still outstanding: cash/modified-cash comparable actual capture, completeness certification for Fund Centre tagging/entity scope, material variance explanation sign-off, and official Treasury classification/template configuration. This is not yet a complete statutory IPSAS 24 implementation.

| Mark | Meaning |
|---|---|
| ✔ **Verified** | Executed in the build environment: unit tests, real PostgreSQL 16, shell scripts, or the frontend type-check, tests and production build |
| 🟢 **Built** | Implemented, wired into the API/UI, and type-checked against the real entity model. It runs for the first time on your machine (`dotnet build`, `dotnet test`, then use). It was **not executed** against EF Core and PostgreSQL here |
| ⛔ **Needs input** | Cannot be completed from here: it depends on something only you or a third party holds |

## Current branch verification (28 November 2026)

The following evidence supersedes older historical statements in this tracker where they conflict.

| Check | Current result |
|---|---|
| Backend build and unit/PostgreSQL integration tests | **Pass** on commit `f0c6672311776a03b9acc9da3382850a136b3951`, workflow [#46046590970](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46046590970). This includes the new payment retry concurrency and invalid-signature/provider-normalisation tests. |
| Frontend type-check, tests and production build | **Pass** on workflow #46049897283, including the FX-rate source UI and consolidation translation-evidence table. |
| EF Core migration drift, clean database install, hardening scripts and RLS runtime boot | **Pass** on workflow #46046590970. No manual migration generation is required for the current committed model. |
| PostgreSQL integration scenarios | **Executed in CI** against real PostgreSQL, including organisation hierarchy, public-sector fund/commitment scenarios, payment webhook concurrency and tenant evidence boundaries. |
| Dashboard operations-count change | **Pass** on workflow [#46046778338](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46046778338). |
| Current code verification | **Pass** on code commit `0c9705de4ee3da36e03ca098585e83ca86f0fb9e`, workflow [#46109169762](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46109169762): backend build and **326/326 unit/PostgreSQL integration tests**, frontend type-check/tests/production build, EF migration drift, clean PostgreSQL install, hardening scripts, least-privilege RLS runtime boot and backup/restore checks all passed. Regression coverage now also verifies tender creation/submission/evaluation/scoring/award audit events and source-pinned 2026 USD/ZWG PAYE monthly bands. |
| Production Vercel deployment | **Not verified**. The earlier deployment attempt was rate-limited by the provider; retry is required after the provider limit expires. |
| Production backup, isolated restore, encryption/key custody and measured RPO/RTO | **Not verified**. CI restore checks are not evidence of the production environment's recovery objectives. |
| Live payment provider certification | **Not verified**. Provider-neutral signed webhook processing is implemented; provider-specific protocols, credentials and sandbox certification remain external prerequisites. |
| Approved jurisdiction statutory content | **Not verified**. The Zimbabwe 2026 baseline remains Draft / baseline-only and cannot be published until specialist legal review and complete schedules are supplied. Publication for non-Zimbabwe jurisdictions is routed through a country-specific activation-adapter contract. The Zimbabwe adapter is implemented; no other country's production adapter or legally approved content is supplied yet, so those jurisdictions remain blocked unless a separately registered and tested adapter is provided. |

## Part II: Accounting engine

| Spec | Item | Status | Where |
|---|---|---|---|
| GL-001, 003, 005, 006, 008 | Balanced, open period, numbering, immutability, atomic posting | ✔ existing + database triggers verified | `GlPostingService`, `001_append_only_guards.sql` |
| GL-002 | Postable accounts and per-source restrictions | 🟢 | `GlAccount.IsPostable/AllowedSourcesCsv`, `PUT gl/accounts/{id}/posting-rules` |
| GL-004 | Rate validation vs rate on file, max age, rounding-difference account | 🟢 (company settings `gl.fx.*`) | `GlPostingService.ApplyFxControlsAsync` |
| IAS 21 FX evidence | Effective direct/inverse rate, effective date, source and source reference retained in rate records; revaluation preview/journal and consolidation result expose applied quote evidence | ✔ workflow #46102023511 (320 backend tests) verifies FX provenance, CTA balance, same-day override precedence, strict group-period enforcement, cross-currency balance elimination, linked P&L elimination, mismatch exceptions and orphaned posted intercompany journal detection with affected balances retained | `ExchangeRate`, `FxRevaluationService`, `ConsolidationService`, GL FX Rates UI |
| Indirect non-controlling interest | NCI must compound ownership percentages through the full group chain and reconcile to consolidated net assets | ✔ PostgreSQL regression verifies 80% × 70% effective ownership, 44% indirect NCI, 56% group equity and the consolidated accounting equation in #46052352753 | `ConsolidationService`, `ConsolidationFxIntegrationTests.cs` |
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
| 12.3 | Evidence: upload with SHA-256, tamper warning on download, no delete; cross-company attach/list/download isolation exercised against PostgreSQL | ✔ workflow #46104593988 | `EvidenceService` |
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
| 20, 21 | Grants and public-sector budget rules | ✔ PostgreSQL tests cover fund hierarchy, atomic PO commitment, budget rejection/rollback, duplicate commitments, GRN item/quantity validation and atomic rollback, concurrent GRN over-receipt prevention, commitment remaining active after GRN, partial/final AP invoice realisation, over-realisation rejection, late bids, release, posted-GL actuals, year-end carry-forward/lapse and closed-budget controls. The Vote/Programme/Sub-Programme schedule reports appropriation, carry-forward, commitments, realisation, encumbrances, posted GL actuals and fund-centre variance. **New:** controlled allocations by expense GL account and optional cost centre; only approved allocations appear in the dimensional variance report; approval requires an independent workflow and cannot exceed the fund-centre appropriation plus carry-forward. PostgreSQL regression verifies that a cost-centre line reports only its own posted actuals and excludes pending allocations. **New:** a classification schedule reports approved allocations and posted actuals across Vote/Programme/Sub-Programme, tenant-defined GL account SubType economic classifications and cost-centre objective dimensions. PostgreSQL regression verifies these dimensions. Official economic/objective code dictionaries, programme-performance measures, additional government schedules and statutory forms remain dependent on an approved jurisdictional reporting standard. |

## Part VII: Architecture and database

| Spec | Item | Status |
|---|---|---|
| 22.3 | Correlation ids and structured request logging; API versioned (`/api/v1`) | 🟢 |
| 23.2 | Database constraints, append-only triggers, optional RLS | ✔ SQL |
| 23.3 | New entities and schema migrations | ✔ EF model snapshot, migration drift check and clean PostgreSQL install pass in CI; public-sector dimensional budget allocation migration `20261128020000_AddPublicSectorBudgetAllocations` is included and applied in workflow #46104593988 |
| 23.4 | Migration policy | 🟢 CI: drift check, clean install, hardening scripts twice, least-privilege RLS boot, backup round-trip; latest workflow #46104593988. Stale `schema.sql` removed; `export-schema.sh` generates it |
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
| AR-005 Tenant isolation | ✔ query filters/RLS checks; PostgreSQL evidence covers company-scoped evidence, scheduled-job tenant context and financial-report source isolation. CSV exports are client-side serialisations of the authenticated report response, not a separate server data source. No shared application cache or asynchronous export worker is currently present; future queued payloads must carry and validate company/tenant identity. |
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

**Classification (spec 32).** The consolidated branch has passing backend/PostgreSQL, frontend, migration and hardening CI evidence on the latest verified commit above. It is **not yet release-certified**: deployment-specific RPO/RTO and worker evidence must be supplied, provider-specific payment/fiscalisation integrations must be certified or explicitly scoped out, and jurisdiction packs require legal approval. Do not treat a green CI run as production deployment or statutory certification.
