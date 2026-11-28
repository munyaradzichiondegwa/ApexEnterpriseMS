# ApexERP product architecture and consolidation acceptance register

**Controlling product specification:** Product Architecture & Roadmap Specification — *ApexERP: Multi-Tenant Enterprise Architecture & Product Expansion* supplied by the product owner on 27 November 2026.

**Implementation branch:** `feature/apexerp-consolidated-enterprise`  
**Base:** verified `main` lineage; production/main are not modified by this work.  
**Status:** requirements adopted; implementation and release verification remain in progress. This document is not a declaration that every capability is complete or production-ready.

## 1. Product position and customer segments

ApexERP is one configurable product core. It must support:

- Private companies and SMEs.
- Government ministries, departments and agencies (MDAs).
- State-owned enterprises (SOEs).
- NGOs and development organisations.
- Multinationals and corporate groups.
- Multi-entity and multi-jurisdiction organisations.

Sector/deployment profiles are configuration presets, not separate code forks. A profile must not silently invent statutory rates, legal obligations, accounting policy, or country-specific compliance content. Such content requires an approved, effective-dated jurisdiction pack and a named review authority.

## 2. Non-negotiable domain hierarchy

```text
ApexERP platform
├── Platform control plane (separate identity and authorisation boundary)
└── Customer organisation / Tenant (customer data-isolation boundary)
    ├── Legal entity / Company
    │   ├── Branches
    │   ├── Departments
    │   ├── Cost centres
    │   └── Users, memberships and company-specific roles
    └── Other legal entities / subsidiaries / international operations
```

Definitions and invariants:

1. **Tenant** is the independent customer organisation and the principal customer data-isolation boundary.
2. **Company** is a legal entity owned by exactly one tenant. A tenant may own multiple companies.
3. **Branch, department and cost centre** are subordinate operating structures; none creates a tenant.
4. **Membership** grants a user access to a particular company within a tenant and carries the applicable company-specific role and validity state.
5. **Platform administration** is a separate control plane. A tenant administrator, company administrator, or tenant `SUPER_ADMIN` must never gain platform authority by role-name equivalence.
6. A government MDA may be a tenant in its own right or a company/entity within a larger government tenant, according to the actual customer and legal operating model.
7. Tenant isolation and company/group consolidation are different boundaries. Group links, shared users, similar names, common domains, or commercial relationships must not be used to infer common tenant ownership.

## 3. Nine architectural pillars and acceptance criteria

| Pillar | Required acceptance evidence |
|---|---|
| Multi-tenancy | Tenant ID is enforced in API authorization, EF query filters, database constraints/RLS, background jobs, exports/files and cache keys. Missing tenant context fails closed. Automated cross-tenant read/write tests prove isolation. |
| Multi-company | One tenant can own multiple legal companies. Company-specific membership and roles are enforced on every request and on company switching. Cross-company access is not inferred from tenant membership alone. |
| Multi-branch | Branches support parent/child hierarchy within a company. Departments and cost centres can be assigned to the correct company/branch and cannot be referenced across tenants or companies improperly. |
| Multi-currency | Local and foreign currency transactions, functional currency, rate source/effective date, revaluation, realised/unrealised FX and reporting treatment are explicit and tested. |
| Multi-country | Jurisdiction is configurable per legal entity. Tax/statutory packs are versioned, effective-dated, approved and independently testable; defaults are not legal advice or a substitute for an approved pack. |
| Consolidation | Group reporting supports controlled entity inclusion, reporting periods, currency translation, intercompany matching/elimination, audit traceability and prevention of duplicate or unsupported eliminations. |
| Access control | Deny-by-default RBAC with tenant/company scope, role-specific permissions, privileged-operation checks, segregation of duties and auditable grants/revocations. |
| Public sector | MDA/SOE profiles support vote/budget/fund hierarchy, commitment accounting, encumbrance controls, appropriations, approvals and government reporting without breaking commercial accounting. |
| Compliance | Immutable/auditable event history, evidence retention, multi-level approvals, SoD conflict detection, exception handling and independent review are enforced at service and database boundaries. |

## 4. Six implementation milestones

### Milestone 1 — Tenant identity and data isolation

- Establish tenant identity and immutable tenant/company claims in access tokens.
- Enforce tenant scope consistently across database entities, API routes, authentication, company switching, background processing, files/exports and operational telemetry.
- Keep platform control-plane data and tenant business data logically and authoritatively separated.
- Use PostgreSQL RLS as defence in depth. Runtime roles must not own tables or possess a bypass capability.
- Test missing tenant context, forged tenant/company claims, cross-tenant IDs, membership revocation, inactive tenants, RLS WITH CHECK, rollback and pooled-connection context reset.
- When a user accesses a second company, the Users RLS policy may expose only that authenticated user's own identity row after validating their active membership; it must not expose other users who happen to have memberships in that company. System-access bypass must remain explicit and work for tenant-and-company-scoped tables.
- Ensure privileged cross-tenant/system scopes are narrow, reason-coded, audited, disposed reliably and unavailable to ordinary tenant request paths.

### Milestone 2 — Organisation registration and onboarding

- Support self-service registration and platform-assisted provisioning.
- Provision tenant, first legal company, administrator invitation, initial membership, subscription/trial and onboarding state transactionally.
- Store invitation secrets only as one-way hashes; enforce expiry, one-time use, email binding, replay resistance and seat/subscription checks.
- Provide an onboarding wizard for company details, fiscal calendar, currency, jurisdiction, sector profile, users/roles and completion validation.
- Ensure SMTP delivery failure is reported honestly; do not represent an invitation as delivered unless delivery is confirmed.

### Milestone 3 — Multiple legal entities and group management

- Support multiple legal entities and explicit parent/subsidiary relationships within a tenant.
- Support branches, departments and cost centres with validated hierarchy and ownership.
- Support company-specific memberships, invitations, role assignment, deactivation, expiry and secure company switching.
- Ensure a user can only switch to a company with a valid, active membership in the same tenant.
- Preserve entity-level accounting configuration and support multi-company reporting without weakening tenant boundaries.

### Milestone 4 — Subscription and licensing

- Support configurable plans, module entitlements, seat/company limits, trial, active, suspended, cancelled, renewal and plan-change states.
- Enforce entitlements server-side on API operations; hiding a menu or route in the UI is not enforcement.
- Check seat/company limits transactionally during registration, invitation acceptance, membership activation and company creation to prevent race-condition over-allocation.
- Preserve a controlled remediation path for over-limit/suspended tenants without granting access to restricted business modules.
- Define billing-provider interfaces, webhook authentication/idempotency and reconciliation. Do not claim payment collection works until a provider is configured and end-to-end tested.

### Milestone 5 — Platform administrator console

- Provide a separate platform identity issuer and platform-only authentication scheme.
- Manage customers, tenant lifecycle, plans, subscriptions, jurisdiction catalogue, deployment profiles, operational checks and platform health.
- Record platform audit events for privileged actions, with actor, target, reason, outcome and correlation.
- Require time-limited, reason-coded, independently approved support grants. Support diagnostics must be scoped, audited, revocable and expire automatically.
- Tenant administrators must not be able to issue platform tokens, grant platform roles or call platform APIs.

### Milestone 6 — Customer deployment and support

- Standardise tenant provisioning, implementation playbooks, diagnostics, export, backup, restore and offboarding.
- Treat backup payloads and infrastructure credentials as operations-worker/provider responsibilities, not application metadata.
- Authenticate operations-worker dispatch and callbacks, enforce replay protection/idempotency, and retain job outcomes. Dispatch attempts are capped at eight; only transient HTTP failures retry, exhausted jobs fail closed, and the HMAC signature/retry policy has automated tests. External worker deployment and callback acceptance remain unverified.
- Provide SLA policies, incident tracking, monitoring evidence and operational reporting.
- Verify restore procedures, RPO/RTO targets, encryption, retention and tenant-specific recovery boundaries with real deployment infrastructure.

## 5. Five-stage productisation roadmap

| Stage | Product outcome | Gate |
|---|---|---|
| 1. Multi-tenant architecture and onboarding | Tenant identity, tenant isolation and assisted/self-service onboarding | Cross-tenant security and provisioning tests pass |
| 2. Subscription, licensing and customer administration | Plans, seats, module entitlements, billing lifecycle and customer governance | Server-side entitlement and concurrency tests pass; billing boundary documented |
| 3. Enterprise configuration and jurisdiction | Configurable COA, tax rules, currencies, fiscal calendars and statutory packs | Approved jurisdiction content and entity-level configuration tests |
| 4. Public-sector and multinational profiles | MDA/SOE fund and budget controls; corporate group consolidation and multi-country operations | Profile acceptance tests and consolidated reporting controls pass |
| 5. Implementation, support and DR | Playbooks, automated tenant operations, SLA monitoring, backup and disaster recovery | Operational worker, monitoring and restore evidence available |

## 6. Required product and security test matrix

The consolidated candidate is not release-ready until CI includes executable tests for all of the following:

- Two or more independent tenants with overlapping company/user names and IDs where feasible; prove no cross-tenant reads, writes, joins, exports or background-job leakage.
- One tenant with at least two legal companies; prove permitted group membership and prohibited company access without membership.
- Parent/child branches and department/cost-centre assignments; reject cross-company and cross-tenant references.
- Tenant and company claims are derived from validated membership, not trusted request payloads.
- Company switching checks active membership, tenant status, subscription policy and target-company role; revoked or expired membership is rejected.
- Refresh-token rotation, reuse detection, logout, password change, account deactivation and MFA changes revoke the correct session families without crossing tenant boundaries.
- Registration and invitation acceptance are atomic, one-time and safe under concurrent requests.
- Seat/company entitlements cannot be exceeded by concurrent provisioning.
- Module entitlements are enforced at API/service level for every protected module route.
- Platform JWTs require the separate issuer, audience, signature and platform-admin claim; tenant tokens cannot access platform routes.
- Support grant approval requires a different platform operator; expiry/revocation prevents further diagnostic access and all access is audited.
- Jurisdiction and statutory configuration are company-specific, versioned and review-gated.
- Multi-currency calculations and group consolidation include exchange-rate provenance, intercompany eliminations and reconciliation evidence.
- Public-sector fund/budget/commitment controls are tested independently from commercial workflows.
- RLS enable/disable scripts are repeatable; rollback drops dependent policies before helper functions; non-superuser runtime-role tests verify fail-closed behaviour.
- Clean database migration, upgrade from supported prior schema, migration drift, backup/restore and migration rollback are verified against real PostgreSQL.
- Frontend typecheck, unit tests and production build pass; frontend permissions mirror but never replace backend enforcement.

## 6A. Public-sector and multinational acceptance detail

The product-owner's 27 November specification makes public-sector and multinational capability part of the same configurable product core. The following scenarios are mandatory acceptance evidence, not optional enhancements:

| Area | Required executable evidence | Current review disposition |
|---|---|---|
| MDA/SOE fund hierarchy | Create Vote > Program > Sub-Program; reject skipped levels, cross-company parents, mismatched financial years, duplicate codes and invalid root levels. | PostgreSQL tests now cover the real hierarchy, invalid levels/years and cross-company/dependency controls; continue to expand MDA/SOE scenario coverage. |
| Appropriation and budget control | Verify approved appropriation plus carry-forward, active commitments and posted actuals reconcile to available budget; prevent negative availability unless an authorised, reason-coded override is used. | PostgreSQL tests cover budget availability, over-budget rejection/rollback and independently approved override. Expenditure schedule separates outstanding encumbrances from posted GL actuals; non-zero GL reconciliation acceptance remains open. |
| Commitment accounting | PO approval and commitment creation are one atomic transaction; reject duplicate commitments, wrong fund centre/company, unapproved PO and amount mismatch. Keep encumbrances active through GRN; realise on posted AP invoice and cancel on PO cancellation. | PostgreSQL tests cover atomic PO approval/commitment creation, duplicate rejection, rollback, GRN item/quantity matching, atomic rollback of later invalid GRN lines, concurrent GRN over-receipt prevention, commitment remaining active after GRN, partial/final AP realisation, and repeated release. Release/cancel and no-op attempts are audit logged under per-commitment transaction locks. |
| Commitment realisation | Reconcile the commitment amount, realised amount, released balance and posted GL actuals without double counting; enforce 0 <= realised <= commitment and preserve an audit trail. | PostgreSQL end-to-end AP tests post partial and final invoices against a GRN-received PO, verify the commitment realises only after GL posting, remains active after partial realisation, and releases only when fully realised. Non-zero actuals reconciliation remains open beyond the tested Vote/Programme schedule. |
| Budget overrides and SoD | Only the designated permission can override; justification, actor, approver, source document and before/after budget values are audited; preparer/approver separation is enforced where required. | PostgreSQL tests cover independently approved budget overrides and rejection without approval; broader role/SoD matrix acceptance remains open. |
| Public procurement and tenders | Enforce tender publication/open/close/award lifecycle, server-time deadline, immutable bid submission, vendor eligibility, scoring ranges/weight totals, award justification and separation of duties. | PostgreSQL tests cover vendor eligibility, scoring/weight validation, late-bid rejection, duplicate submission protection, GRN item/quantity matching, atomic receipt rollback and concurrent receipt serialization. Full lifecycle, concurrent award and complete SoD acceptance remain open. |
| Government reporting | Produce vote/program/fund-level budget, commitments, actual expenditure, variances and year-end carry-forward/lapse reports; reconcile totals to the posted GL and retain period/audit provenance. | Vote/Programme/Sub-Programme expenditure schedule now reports appropriation, carry-forward, commitments, realisation, outstanding encumbrances, posted GL actuals, available and lapsed balances. PostgreSQL tests now verify a non-zero posted GL actual against available budget and verify year-end lapse persistence/repeat-close protection. The schedule now also reports budget-to-actual variance (appropriation plus carry-forward less posted expense actuals; positive is favourable) and variance percentage, separately from commitment-adjusted available budget. PostgreSQL tests verify the calculation. Controlled appropriation allocations by expense GL account and optional cost centre are now implemented. Allocation drafts require the existing independent approval workflow; only an actual workflow approver may finalise approval, approved totals cannot exceed appropriation plus carry-forward, approval/create events are audited, and the dimensional variance report matches posted expense actuals to the exact GL-account/cost-centre dimension while excluding pending allocations. PostgreSQL regression coverage verifies these controls. A complete government reporting pack, additional statutory schedules and broader approved classifications (including economic/objective dimensions where required) remain open; jurisdiction-specific codes must not be invented. |
| Multi-currency and multi-country groups | Translate entity results using approved effective-dated rates; document CTA/FX treatment, reporting period alignment, intercompany matching and elimination pairs, ownership/NCI and consolidation audit trail. | FX rates retain source and source-reference metadata; revaluation previews/journal narratives and consolidation results expose selected quote, direction, effective date and provenance. PostgreSQL tests verify cross-currency rate provenance, CTA balance, same-day override precedence and 44% indirect NCI through an 80% × 70% ownership chain. Workflow #46102023511 passes 320 backend tests plus frontend and migration gates. Consolidation surfaces period coverage, period-end alignment and whether each company's reporting window matches the parent. Reciprocal and cross-currency intercompany balances are eliminated; linked source/mirror income and expense journals are eliminated at posting-date group rates, and translated mismatches are reported without elimination. PostgreSQL acceptance covers these cases. The service now enforces a strict aligned-period policy: every group entity must have exactly one period matching the parent start/end dates and ending on the consolidation date. It blocks misaligned periods rather than silently adjusting dates or posting journals; approved entity adjustments must be posted in the ledgers first. Automated period-adjustment workflows remain out of scope; entity adjustments must be approved and posted in the entity ledgers. Orphaned posted intercompany journal activity is now detected by reconciling classified intercompany journal lines against source/mirror journal references in the transaction register. Affected account balances are retained rather than eliminated. PostgreSQL tests verify orphan exceptions and non-elimination; workflow #46102023511 passes 320 backend tests plus frontend and migration gates. Public-sector expenditure actuals now include only posted expense-account lines, and the PostgreSQL schedule test confirms tagged income and balance-sheet lines do not distort expenditure actuals. |
| Statutory jurisdiction packs | Demonstrate approved jurisdiction owner, official source, version, effective dates, legal review and repeatable tests before enabling a statutory pack. | Zimbabwe 2026 baseline remains Draft/baseline-only. Only the Zimbabwe activation adapter is currently implemented; publication for other jurisdiction codes is explicitly blocked so Zimbabwe PAYE/NSSA/AIDS Levy/VAT/corporate-tax mappings cannot be applied to another country. Each additional jurisdiction requires its own validated adapter, approved source content and specialist tests. |

**Latest branch verification (28 November 2026):** workflow [#46107705973](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46107705973) passed backend build and **326/326 unit/PostgreSQL integration tests**, frontend type-check/tests/production build, migration drift, clean PostgreSQL install, hardening, least-privilege RLS runtime boot and backup/restore checks on code commit `44d0d114422581affc2ce05b4f0449a661a2921e`. New coverage verifies company-isolated financial report queries, public-sector classification schedule dimensions, and pluggable jurisdiction-specific statutory adapter validation/activation dispatch. Non-Zimbabwe jurisdictions still require their own registered, tested adapter and legally approved statutory content before production activation.

**PostgreSQL evidence added:** `PublicSectorIntegrationTests.cs` covers the fund hierarchy, invalid level/year rejection, atomic PO approval plus commitment creation, over-budget rollback, duplicate commitments, budget availability, partial/cumulative commitment realisation, over-realisation rejection, posted AP invoice realisation, GRN item/quantity validation, atomic rollback on an invalid later GRN line, concurrent GRN over-receipt prevention, late-bid rejection, release by Commitment ID, year-end carry-forward hierarchy and lapse/repeat-close protection. The Vote/Programme expenditure report has PostgreSQL tests for hierarchy/commitment reconciliation and a non-zero posted-GL actual; year-end lapse persistence and repeat-close protection are also covered. GitHub Actions workflow [#46046590970](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46046590970) passed backend build and PostgreSQL integration tests on commit `f0c6672311776a03b9acc9da3382850a136b3951`. This does not certify every public-sector scenario; non-zero posted-GL reconciliation and formal lapse reporting remain open.

**Known product boundaries:** the tender service currently documents that a distinct public vendor self-registration portal is not included; bid submission is through the authenticated application. Barcode/RFID hardware integration is not included; the workflow accepts scanner-produced tag values. These boundaries must remain visible in product and deployment claims.

**Consolidation PostgreSQL evidence added:** `ConsolidationFxIntegrationTests.cs` verifies effective-dated direct/inverse rate provenance, CTA translation reserve and the consolidated accounting equation, plus indirect NCI across a parent → subsidiary → lower-tier subsidiary ownership chain. The 80% × 70% chain produces 56% effective group ownership and 44% NCI; group equity plus NCI reconciles to consolidated net assets. `ConsolidationIntercompanyIntegrationTests.cs` verifies reciprocal and cross-currency balance elimination, linked source/mirror revenue and expense journal elimination at posting-date rates, and non-elimination with exceptions when translated amounts differ. Workflow [#46062200703](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46062200703) passed all 318 backend tests, frontend checks/build and migration/RLS/backup-restore gates. Automated period adjustment/enforcement and unmatched or unlinked intercompany transaction reconciliation remain open.

## 7. Existing foundation to preserve

The implementation must extend the existing ApexERP codebase rather than replace it. Preserve the existing accounting engine, 16-module ERP functionality, five-statement financial reporting, IFRS controls, approvals, audit trails, existing company-level query filtering, and prior merged audit-hardening work.

The enterprise platform foundation already present in this branch documents tenant onboarding, company memberships, subscription plans, jurisdiction profiles, deployment profiles, a separate platform bearer scheme, support grants, operational checks, SLA records and lifecycle-job metadata. Those are implementation claims to be verified against executable code, database migrations, tests and deployed integrations; documentation alone is not acceptance evidence.

## 8. Integration and deployment boundaries — honest status

The application must distinguish an implemented workflow/interface from a configured, operational integration.

| Capability | What counts as verified |
|---|---|
| Email/SMTP | Provider configured; delivery success/failure observed; invitation lifecycle tested end-to-end |
| Billing/payment | Provider configured; signed webhook, idempotency, payment reconciliation and lifecycle transition tested |
| Operations worker | Worker deployed; signed dispatch and callback tested; retries, replay and failure handling verified |
| Backup/restore | Real backup artifact, encryption/key handling, restore into isolated target, integrity check and measured recovery time |
| Monitoring/SLA | Live telemetry source, evidence freshness, alert delivery and incident lifecycle verified |
| Jurisdiction/statutory packs | Approved source, effective date, jurisdiction owner, tests and release/version governance recorded |
| Country tax engines | Current statutory rules validated by an authorised jurisdiction specialist; no inferred/default rates treated as legally complete |

If a provider, credential, legal approval or deployment environment is absent, mark the integration **not configured / not verified**. Do not mark it complete because an interface, database entity, mock or documentation exists.

## 9. Release and consolidation governance

1. Keep `main` and production databases untouched during consolidation.
2. Reconcile the merged mainline, PR #13 corrections, unique changes from PR #14 and PR #15, and relevant synchronisation work from PR #16 at file and behaviour level.
3. Do not merge the candidate branches independently and do not resolve critical conflicts by accepting an entire side.
4. Preserve the newer RLS rollback ordering and verify it on real PostgreSQL.
5. Classify every distinct historical CI failure by root cause; repeated runs of the same defect are not separate defects.
6. A green backend/frontend job is insufficient if PostgreSQL, RLS, migration, security or integration gates fail.
7. Run the full CI matrix on the single consolidated branch; record commit SHA, workflow run, job result and evidence.
8. Create/update one draft consolidation PR only when the candidate is coherent enough for review. Do not merge until all mandatory gates pass and remaining provider-dependent items are explicitly accepted as deployment prerequisites.
9. Keep PR #13–#16 open or otherwise untouched until the consolidated candidate has been reviewed; do not close or merge them as a substitute for reconciliation.
10. No production migration, legacy tenant backfill, or destructive database operation is authorised by this specification.

## 10. Current evidence ledger

The following is the starting position from the live repository review. It is deliberately conservative.

| Area | Repository evidence | Current disposition |
|---|---|---|
| Tenant/company domain model | Tenant, company membership, branch, subscription, jurisdiction and onboarding entities exist | Implemented in candidate; runtime and migration tests required |
| Tenant query filters | Tenant filters exist in the EF model | Verify every entity, service, background job, file/export and null-context path |
| PostgreSQL RLS | Enable/disable hardening scripts and test coverage exist; recent correction addresses policy dependency ordering | Fresh CI PostgreSQL verification required on consolidated HEAD |
| Registration/onboarding | Self-service and platform-assisted provisioning are documented and controller/service code exists | Verify atomicity, invitations, email delivery and concurrency |
| Company memberships/switching | Membership and switching flows are documented and implemented | Verify role derivation, tenant boundary and revocation under PostgreSQL |
| Subscription/licensing | Plan, subscription, limits and middleware exist | Verify all routes, concurrency and lifecycle state transitions |
| Jurisdiction/profiles | Jurisdiction and deployment profile entities/catalogue exist | Statutory content remains approval/provider dependent |
| Platform control plane | Separate PlatformBearer scheme and platform controller exist | Verify every route, issuer/key configuration, audit and support-grant enforcement |
| Branches/departments/cost centres | Branch entity and assignments exist | Verify complete hierarchy, company scope and CRUD/UI coverage |
| Multicurrency/consolidation | Effective direct/inverse rate resolution, rate source/reference capture, revaluation evidence and consolidation rate evidence are implemented | Resolver, PostgreSQL cross-currency provenance/CTA balancing and same-day override timestamp scenarios pass in #46050356711 (293 backend tests); period alignment and elimination acceptance remain open |
| Public sector | Vote/Programme/Sub-Programme appropriations and commitments; approved expense-account allocations by optional cost centre; posted-GL variance report; independent approval workflow and appropriation ceiling | PostgreSQL CI verifies dimensional actual matching, exclusion of pending allocations and clean migration. Broader government reporting schedules, statutory forms and additional economic/objective classifications remain open |
| Operations/DR | Lifecycle metadata, SLA and worker-dispatch boundary are documented | Worker, monitoring and backup infrastructure require deployment evidence |
| SMTP/billing/statutory integrations | Interfaces/configuration boundaries are documented | Not verified until provider and approvals are configured |

## 11. Definition of done

The consolidation is complete only when:

- The nine pillars and six implementation milestones have traceable code, migrations, UI/API paths and tests.
- The existing ERP accounting, reporting and governance functionality remains intact.
- Tenant and company isolation are proven independently using real PostgreSQL and a least-privilege runtime identity.
- The separate platform control plane is proven inaccessible to tenant identities.
- All distinct historical CI failures are either corrected and retested or documented with a justified, explicitly accepted exception.
- Backend, frontend, migration, RLS, PostgreSQL integration, hardening, backup/restore and clean-install checks pass on the same final commit.
- Provider-dependent items are clearly labelled as deployment prerequisites, not falsely represented as operational.
- The final pull request contains the evidence and remains unmerged until review and approval.

**Release status:** Not certified. This register defines the required work and acceptance gates; it does not substitute for passing evidence.
