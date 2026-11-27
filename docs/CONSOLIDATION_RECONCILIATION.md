# Enterprise-platform branch reconciliation record

**Repository:** `munyaradzichiondegwa/ApexEnterpriseMS`  
**Consolidated branch:** `feature/apexerp-consolidated-enterprise`  
**Review PR:** #17 (draft; not approved for merge)  
**Mainline baseline:** `8d8a6f69b48f602430c3536aa868fe46807c483d`

This record captures the file-level reconciliation decisions for PRs #13–#16. It supplements, and does not replace, the controlling acceptance register in [PRODUCT_ARCHITECTURE_ROADMAP.md](PRODUCT_ARCHITECTURE_ROADMAP.md).

## 1. Source pull requests at review

| PR | Head branch | Reviewed head | Base | Files changed | Decision |
|---|---|---|---|---:|---|
| #13 | `fix/post-merge-ci-regressions` | `42fb5ebd77f5507c79bf95a67bd7b96533719c77` | `main` | 10 | Carry forward valid corrections; do not merge separately |
| #14 | `feature/enterprise-platform-release-candidate` | `ada045c0c734f41bee3608f3646322650c85072c` | `main` | 18 | Compare against #15; retain unique valid behaviour only |
| #15 | `feature/enterprise-platform-release-candidate-v2` | `1842e1586c08637de4abe5e14503867589e3ca07` | `main` | 18 | Principal enterprise-platform source |
| #16 | `fix/post-merge-ci-regressions` | `42fb5ebd77f5507c79bf95a67bd7b96533719c77` | PR #14 branch | 15 | Synchronisation subset; not an independent merge candidate |

The source comparison showed that #14 and #15 modify the same 18 paths. #16 modifies 15 of those 18 paths and targets #14, not `main`. #13 modifies 10 of the same critical paths. The release-candidate-v2 branch is therefore the principal source, not a separate branch to merge wholesale.

## 2. File-level decisions

| Area | Competing changes | Consolidation decision |
|---|---|---|
| RLS enable script | #13 contained malformed PL/pgSQL block terminators (`END $` / `DO $`) in the script. #15 had corrected delimiters and added tenant/company table-policy handling. | Use #15's valid delimiters and policy structure. Further harden user visibility and explicit system bypass based on real PostgreSQL test failures found on PR #17. |
| RLS rollback | #15 explicitly drops `apex_user_membership_access` on `Users` before dropping the helper function; #16's patch did not retain that explicit first drop. Both include a broader policy cleanup loop. | Keep #15's explicit dependency removal and broader cleanup. Verify the full rollback on a freshly migrated PostgreSQL database. |
| Authentication controller | #14 retained `[AuthenticatedOnly]` on `MyCompanies` and `SwitchCompany`; #15/#16 removed them even though the authorization test expects those explicit self-service markers. | Restore both attributes while retaining #15's transactional invitation acceptance and company-switch validation. |
| Organisation management | #15 added a management guard to subscription renewal requests; #14/#13 did not include that exact guard. Candidate-wide exemption would bypass the intended module permission convention. | Keep the renewal guard, remove the controller-wide permission exemption, require `organisation.read/write` module permissions, and retain explicit tenant-admin checks for mutations. |
| Platform administration | Competing versions contain the separate `PlatformBearer` scheme and platform-only policy. Anonymous invitation acceptance and signed worker callbacks are intentional exceptions, not ordinary tenant endpoints. | Keep the separate platform identity boundary, explicit exemption only for the platform controller, rate limiting on administrator invitation acceptance, tenant/company activity validation, transactional one-time invitation claiming, and signed callback verification. |
| Tenant listing / SLA | The candidate includes tenant company counts and business-hours-aware SLA measurement/configuration. | Retain these capabilities; test query behaviour and time-zone/business-hours calculations. |
| AuthService | #13 added broad `SystemAccessScope` wrappers around signed-in password/MFA operations and unbounded all-company session revocation. #15 scopes refresh-family revocation to the owning tenant and preserves target-company role loading. | Keep tenant-scoped revocation and target-company role resolution. Do not restore broad system bypasses for ordinary signed-in user operations. Verify password change revokes all sessions within the same tenant and no other tenant. |
| EF Core model | #15 adds explicit FK indexes; mainline contains explicit restrictive FK relationships introduced by the earlier audit-hardening work. | Preserve both the mainline restrictive FK mappings and candidate FK indexes. The migration, model snapshot and live schema must remain aligned. |
| Authorization tests | Candidate test helpers initially used reflection metadata detection that did not reliably recognise inherited controller attributes; platform endpoints also have signed/anonymous callback exceptions. | Use robust controller-type attribute detection; assert only the platform control plane is module-exempt; assert organisation actions require module permissions; assert the intended auth self-service markers remain present. |
| Request-time tenant membership validation | A valid signed JWT alone would otherwise remain usable after membership revocation, company-role change, account lockout or permission changes. | Revalidate tenant, active home/target companies, user, current company membership, role ID/name and permission snapshot on every authenticated tenant request. Skip this tenant check for AllowAnonymous endpoints and platform-admin tokens. |
| Seat/company capacity concurrency | Seat/company limits checked without a transaction-scoped lock can be exceeded by simultaneous requests across API instances. | Add PostgreSQL transaction-scoped advisory locks keyed by tenant and capacity purpose. Begin a transaction before every lock call; hold it through capacity checks and writes. Count pending administrator and company invitations in seat reservations. |
| Capacity-lock test | The new lock helper requires a live PostgreSQL transaction and must serialize the same tenant/purpose key. | Add a real PostgreSQL integration test proving no-transaction use fails, concurrent try-lock is blocked, and lock is released at transaction end. |
| Tenant identity test fixtures | A later branch update attempted to assign `TenantId` directly to `Role`, `User` and `CompanyMembership`, which are company-scoped entities without that property. It also initially omitted two fixture IDs. | Keep the domain model's company ownership relationship; remove invalid property initialisers and explicitly declare the two user IDs. Tenant ownership is established through the owning Company. |

## 3. Additional defects exposed by PR #17 CI

These are not hypothetical review comments; each was exposed by an actual GitHub Actions run and is being corrected on the consolidated branch.

| Run / stage | Finding | Correction applied | Verification still required |
|---|---|---|---|
| #46005719893 | Backend compilation failed because tests assigned nonexistent `TenantId` properties to `Role` and `CompanyMembership`; the RLS test also referenced undeclared user IDs. | Corrected test fixtures to use the actual company-scoped entity model and declare fixture IDs. | Confirm on the latest PR #17 head. |
| #46006097408 | 253/255 tests passed. The auth self-service marker test failed because `Auth.MyCompanies` and `Auth.SwitchCompany` were missing `[AuthenticatedOnly]`. The RLS test also exposed an unrelated-user visibility failure. | Restored both auth attributes. Updated the Users RLS membership predicate to bind the membership to the signed `app.user_id` as well as the Users row and selected company. | Confirm on latest PR #17 head. |
| #46006252206 | 254/255 tests passed. The remaining RLS failure showed system bypass could not see company-owned tables that also carry `TenantId`. | Updated generated RLS policies for tables with both `CompanyId` and `TenantId` so explicit `app.rls_bypass = on` bypasses both predicates; normal requests still require both company and tenant match. | Confirm on latest PR #17 head. |
| #46006584175 | The prior code was tested before the system-bypass correction was present; the run therefore does not verify the correction. | Superseded by the next run. | Use the run for the final head only. |

The latest fully green executable-code verification is workflow [#46052352753](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46052352753) on commit `dfe037faa5e752a711e1bf9a3570c5f148a75c76` (27 November 2026). All three jobs passed: backend build and **299/299 unit/PostgreSQL integration tests**; frontend type-check, tests and production build; and migration drift, clean install, hardening scripts, least-privilege runtime-role boot and backup/restore checks. The run includes FX provenance, same-day manual-override precedence, CTA balancing and indirect NCI through an 80% × 70% ownership chain; public-sector posted-GL, year-end lapse, closed-budget, atomic/concurrent GRN and AP invoice commitment realisation tests; payment retry/webhook tests; and bounded tenant lifecycle dispatch attempts. Later commits update the readiness and roadmap documents only; verify the current branch workflow before release. The Vercel commit status remains separate and does not establish live deployment.

## 4. Subsequent security and capacity hardening

The branch received additional reviewed changes after the first reconciliation:

- TenantCapacityLock uses a PostgreSQL transaction-scoped advisory lock for tenant seat and company capacity checks. The call sites in legal-company creation, membership assignment, company invitations, company-invitation acceptance and initial administrator-invitation acceptance now open a relational transaction before acquiring the lock and commit only after the reservation or membership write succeeds.
- Active seats are counted once per user across a tenant. Pending company invitations and pending initial-administrator invitations are included in seat reservations. Existing tenant members do not consume a second seat when added to another company.
- Invitation acceptance claims the one-time invitation inside the same transaction as user and membership creation. Email delivery occurs only after the invitation record is committed; provider delivery remains a separate deployment prerequisite.
- TenantMembershipValidator revalidates signed tenant/company/user/role claims on each authenticated tenant request. It rejects inactive tenant/company/home-company/user, locked accounts, revoked or expired company membership, changed role identity/name, and stale permission snapshots. This makes membership and permission revocation effective without waiting for the 15-minute access token to expire.
- The middleware skips tenant membership validation for endpoints explicitly marked AllowAnonymous, so a stale bearer token cannot prevent a user from reaching login or invitation acceptance. Platform-admin tokens remain on their separate authentication boundary.
- Real PostgreSQL tests cover role/membership revocation, permission-snapshot changes, and transaction-scoped advisory lock behaviour.

## 5. Historical CI failure classification

- Repeated runs against the same changing branch head are not counted as independent defects.
- PR #13's earlier RLS rollback failure was caused by the `Users` membership policy depending on `apex_rls_allowed(uuid)` when the helper was dropped. The newer rollback explicitly removes that policy first.
- PR #13's migration/hardening script also contained malformed PL/pgSQL delimiters; the release-candidate-v2 script has the corrected `$$` delimiters.
- The later authorization failure concerns the test's treatment of controller-level exemptions and organisation-management permission coverage. The consolidated branch now limits the exemption to platform administration and applies explicit organisation module permissions.
- The tenant-security-hardening branch had 18 successful recorded runs in the retrieved 100-run history. Those successful runs are useful evidence for that branch's code at those commits, but do not certify the final consolidated commit.
- PR #17's tests are the controlling evidence for this consolidated candidate. Earlier branch successes cannot substitute for a green run on the final head.

## 6. Consolidated branch contents

The consolidated branch is based on `main` and carries the 18 enterprise-platform source paths from PR #15, plus reconciliation corrections and the controlling product acceptance documentation. It does not merge the divergent source branch histories.

The branch includes:
- `docs/PRODUCT_ARCHITECTURE_ROADMAP.md` — controlling product architecture and acceptance register.
- `docs/CONSOLIDATION_RECONCILIATION.md` — this file-level decision record.
- The tenant/company membership, onboarding, subscription, jurisdiction, deployment-profile and platform-control-plane implementation.
- The selected RLS scripts, migrations, model snapshot, integration tests, Docker configuration and platform administration interface.
- Additional corrections for permission enforcement, authentication-only self-service actions, RLS user visibility and explicit system bypass.

## 7. Outstanding release gates

1. The latest fully green code baseline is workflow #46049897283 on commit `abed068`: backend 292/292 tests, frontend checks/build and migration/restore checks all passed. The current head adds the same-day FX override-by-effective-date correction and regression test; workflow #46050356711 must finish green.
2. Full PostgreSQL consolidation acceptance remains for reporting-period alignment and intercompany elimination reconciliation. CTA translation has a PostgreSQL regression scenario, and indirect NCI through the full ownership chain now has a PostgreSQL test.
3. The AP invoice/GRN-to-commitment lifecycle now has PostgreSQL coverage: GRN keeps the commitment active, and partial/final posted AP invoices realise and release it. A complete government variance/reporting pack and broader repeated-release audit evidence remain open. Fund hierarchy, budget controls, posted-GL expenditure, year-end carry-forward and lapse/repeat-close scenarios also have PostgreSQL coverage.
4. Tenant-isolation acceptance remains across all evidence/file routes, exports and background payloads. Evidence has cross-company boundary tests; exports are client-side; no shared application cache has been established in this branch.
5. Provider-specific EcoCash, OneMoney, PayNow, ZimSwitch and bank protocols plus sandbox certification remain external prerequisites. The current signed-JSON adapter is provider-neutral, not live certification.
6. The tenant lifecycle dispatcher has stale-lease recovery and a bounded eight-attempt retry policy; production worker deployment, callback monitoring and live recovery acceptance remain unverified.
7. Legally approved jurisdiction-specific statutory packs remain outstanding. The Zimbabwe 2026 baseline must stay draft-only until full legal schedules and approvals are supplied.
8. Disaster-recovery acceptance in the target environment remains outstanding: encrypted/off-site backup custody, isolated restore, integrity verification and measured RPO/RTO. CI restore checks are not production RPO/RTO evidence.
9. Final release review must reconcile the acceptance register, deployment configuration, monitoring and security posture.

**Decision:** PR #17 remains open, draft and unmerged. The base branch `main` remains at `8d8a6f69b48f602430c3536aa868fe46807c483d`; implementation remains isolated to `feature/apexerp-consolidated-enterprise`. Green CI is necessary but is not production deployment, provider certification, legal approval or release authorisation.
