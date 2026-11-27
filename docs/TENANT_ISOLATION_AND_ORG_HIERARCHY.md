# Tenant Isolation and Organisation Hierarchy Review

This review records the implementation boundaries for tenant isolation and the company-level organisational hierarchy. It does not mark infrastructure or production deployment as complete.

## 1. Tenant isolation

### Database access

- `ApexErpDbContext` applies fail-closed global query filters to company-owned records using the authenticated `CurrentCompanyId`. Company and customer-organisation records are separately scoped by `CurrentTenantId`.
- PostgreSQL row-level security is an additional, opt-in enforcement layer. The RLS migration applies policies to tables with a `CompanyId` or tenant-only `TenantId` column. It must only be enabled after the approved legacy tenant assignment/backfill and with a non-owner, non-superuser runtime role that has no `BYPASSRLS` privilege.
- Explicit system and cross-tenant access is represented by named scopes. A missing company identity is not itself a bypass.

### File evidence

- Supporting evidence is currently stored as a database `bytea` payload in `AuditEvidenceReferences.Content`; there is no external object-storage adapter or filesystem path to partition.
- Evidence records are company-owned and inherit the company query filter/RLS policy.
- Uploads now require the service's company argument to equal the authenticated DbContext company and verify that the target record exists in that same company before creating the evidence link.
- Listing and downloading also require the authenticated company context. Downloads retain SHA-256 integrity verification and audit logging.
- PostgreSQL integration coverage checks cross-company upload rejection, wrong-company context rejection, cross-company download rejection, and RLS visibility for evidence rows.

### Exports and HTTP caching

- CSV exports are generated in the browser from rows already loaded by the screen. There is no separate server-side export store or export download endpoint in the current implementation.
- The API sets `Cache-Control: no-store` on responses outside Development. This prevents shared HTTP caches from retaining tenant API responses.
- No application memory/distributed cache, Next.js query cache provider, React Query client/provider, or active query-key implementation is currently wired. The React Query package is present in the frontend dependencies but is not used by the application. Therefore there are no active application cache keys to tenant-prefix today.
- Do not introduce shared caching until a cache design namespaces every tenant-owned entry by tenant and company, and by user/permission scope where the cached representation is user-specific. Cache invalidation must occur on company/tenant switch and membership/permission changes.

### Background jobs

- The scheduled runner discovers active companies, then each scheduled job revalidates the owning active tenant and sets a fresh company/tenant context before running.
- `JobExecution` is company-owned and has a unique company/job/run-key index, preventing one company's execution record from claiming another company's run.
- Tenant lifecycle jobs are tenant-level control-plane records. The dispatcher claims jobs atomically, includes the tenant ID in the worker payload, and signs the payload with HMAC. The worker receives job metadata, not database credentials or backup contents.
- PostgreSQL integration tests now exercise per-company scheduled-job contexts and RLS visibility for `AuditEvidenceReferences`, `JobExecutions`, and `TenantLifecycleJobs`.

## 2. Organisation hierarchy

The hierarchy remains company-level; a branch does not create a tenant.

### Supported management operations

- Branches: list, create, edit, deactivate.
- Departments: list, create, edit, deactivate.
- Cost centres: list, create, edit, deactivate.
- Department assignment lookups: active employees and segments are listed within the selected tenant/company boundary.

### Validation and lifecycle rules

- Branch codes are normalised and unique within a company; edits reject duplicate codes, invalid parents, self-parenting and circular parent chains.
- Cost-centre codes are normalised and checked for duplicates within a company. Parent changes reject self-parenting, circular hierarchies, inactive/cross-company parents and incompatible branch assignments.
- Department names are checked for duplicates within a company. Branches, cost centres and managers must be active and belong to the same company; department and cost-centre branch assignments must be consistent.
- A branch cannot be deactivated while it has active child branches, departments or cost centres.
- A department cannot be deactivated while active employees remain assigned to it.
- A cost centre cannot be deactivated while active child cost centres or departments remain assigned to it.
- Deactivation is soft: records and historical references are retained. There is no hard-delete endpoint for hierarchy records.
- Departments now have an explicit `IsActive` property, added with an EF migration and model-snapshot update.

## 3. Verification boundaries

The CI workflow checks backend build/tests, frontend typecheck/tests/build, and migration drift/clean-install/boot. PostgreSQL-specific isolation tests run when the workflow supplies `APEX_TEST_DB`.

A passing CI run proves the tested code paths and migration checks passed. It does not prove that production has enabled RLS, completed legacy tenant backfill, deployed the operations worker, or configured a shared cache/object store. Those remain deployment controls and are not inferred from application code alone.
