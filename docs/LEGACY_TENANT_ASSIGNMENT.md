# Legacy Company-to-Tenant Assignment and Backfill Plan

**Status:** Proposed design for review; not an execution authorisation  
**Applies to:** Existing ApexERP databases created before the customer-organisation tenant foundation  
**Target branch:** `feature/legacy-tenant-assignment`

## 1. Purpose

The tenant-foundation migration `20261126165020_AddCustomerOrganisationTenantFoundation` adds:

- `CustomerOrganisations`, representing the independent customer that contracts for ApexERP;
- nullable `Companies.TenantId`, linking each legal entity to its owning customer organisation;
- a restrictive foreign key and supporting indexes.

The migration intentionally does not assign existing companies. This plan defines how to establish those assignments without guessing, changing accounting history, or weakening tenant isolation.

**No production database has been read, modified, backfilled, or migrated by this work.** This document is not permission to execute a backfill.

## 2. Governing domain rules

1. **Tenant means customer organisation.** It is the independent ApexERP customer and security boundary.
2. **Company means legal entity.** One tenant may own one or more companies.
3. A branch, department, fund centre, or operating location is not automatically a tenant or legal company.
4. A company-group parent/child relationship is not proof of common tenant ownership. Group links can cross tenant boundaries where a separately recorded customer/supplier relationship and consent exist.
5. Similar company names, email domains, addresses, directors, registration-number fragments, or shared users are not sufficient evidence to combine companies under one tenant.
6. A company with no reliable ownership evidence remains unassigned until an authorised customer/platform representative resolves it.
7. Each company must have exactly one owning tenant before the follow-up migration makes `Companies.TenantId` non-nullable.

These rules preserve the product distinction in the architecture specification: one customer organisation can operate multiple legal entities, while separate customers remain isolated even if they trade with one another.

## 3. Current application behaviour and migration implications

- `CustomerOrganisation` has a required `Name`, optional legal name and registration number, and an active flag.
- `Company.TenantId` is nullable only for this transition. The domain model comment explicitly anticipates a follow-up migration after all existing companies are assigned.
- The EF Core company and customer-organisation query filters require a matching `CurrentTenantId`. An unassigned company is therefore not visible through ordinary tenant-scoped queries.
- Authentication token issuance rejects a company with a missing tenant or an inactive tenant.
- API tokens require both `tenant_id` and `company_id`.
- PostgreSQL RLS is an additional enforcement layer when enabled. A missing tenant context is not a bypass.
- New self-service registration already creates a customer organisation, a company linked to it, an administrator role, and the first user in one provisioning flow.

Consequently, do not enable existing customer sign-in or run tenant-scoped operations for a legacy company until its mapping is validated. Do not work around this by introducing a default/shared tenant or broadening normal query filters.

## 4. Evidence required for each assignment

Every proposed mapping must be supported by an authoritative source, such as:

- signed customer contract, order form, or implementation agreement;
- verified legal-entity ownership/group structure supplied by the customer;
- approved customer master record from the organisation's authoritative system;
- formal written confirmation from an authorised customer representative;
- platform onboarding record that identifies the contracting customer and the legal entities included.

The evidence must establish both:
- which customer organisation is the ApexERP tenant; and
- which legal company or companies belong to that tenant.

Registration numbers and existing company metadata are useful for cross-checking, but are not, by themselves, sufficient to infer the commercial customer boundary.

## 5. Mapping register

Prepare a controlled CSV mapping register using [the supplied template](templates/legacy-tenant-mapping.csv). Use one row per existing company. Companies belonging to the same tenant must repeat the same TenantId and tenant metadata.

Required columns:

| Column | Meaning | Rule |
|---|---|---|
| `CompanyId` | Existing `Companies.Id` | Required; exact UUID from the target database |
| `TenantId` | New `CustomerOrganisations.Id` | Required; one stable UUID per customer organisation |
| `TenantName` | Customer-facing organisation name | Required; non-empty |
| `TenantLegalName` | Contracting/legal name | Optional only where unavailable and formally accepted |
| `TenantRegistrationNumber` | Customer organisation registration number | Optional where not applicable or not available |
| `ApprovalReference` | Evidence or approval record identifier | Required; must resolve to retained evidence |
| `ApprovedBy` | Approver identity | Required; named authorised person |
| `ApprovedAtUtc` | Approval timestamp | Required; ISO-8601 UTC timestamp |

Do not use company names as keys. The UUIDs are the immutable mapping keys. Do not place passwords, tokens, personal identity numbers, or other secrets in this register.

## 6. Validation gates before any write

A future backfill utility/script must fail closed unless all of these checks pass:

1. Every CompanyId exists exactly once in the target database.
2. Every CompanyId appears exactly once in the mapping register.
3. No CompanyId is mapped to more than one TenantId.
4. Every TenantId has one consistent TenantName, TenantLegalName, and TenantRegistrationNumber across all rows.
5. No supplied TenantId already exists in `CustomerOrganisations`, unless an explicit, separately reviewed resume mode is designed and approved.
6. No company with a non-null TenantId is silently reassigned. Existing assignments must match the approved mapping or cause the run to stop.
7. Every mapping row has an ApprovalReference, ApprovedBy, and ApprovedAtUtc.
8. All UUIDs and timestamps parse strictly; blank, malformed, duplicate, or unexpected columns cause rejection.
9. The database schema/migration level is checked before processing.
10. The number of mapped companies equals the number of companies in the agreed migration scope. Any unaccounted-for company is reported and blocks completion.

The tool must produce a dry-run report first. Dry-run is the default and must not insert tenants or update companies. An apply mode must require an explicit operator flag and a separate approval record.

## 7. Recommended execution sequence

### Phase A — Inventory (read-only)

1. Confirm the database environment and exact migration level.
2. Run the [read-only inventory diagnostic](../backend/database/diagnostics/legacy_tenant_inventory.sql) using an authorised read-only identity that can see all company and tenant rows.
3. Enumerate all companies using a privileged read-only connection.
4. Export company IDs, names, registration numbers, active status, parent-company IDs, and creation timestamps.
5. Report the number of companies already assigned, unassigned, and referencing missing/inactive tenant records.
6. Do not export financial, payroll, employee, user credential, or transaction data for the purpose of tenant mapping.

### Phase B — Customer confirmation

1. Group candidate legal entities only where authoritative evidence identifies the same contracting customer.
2. Send each customer the proposed list of its legal companies for confirmation.
3. Record rejected, disputed, and unresolved rows separately.
4. Obtain an explicit approval reference for every final company-to-tenant assignment.
5. Keep unresolved companies unassigned; do not use a catch-all tenant.

### Phase C — Dry-run

1. Validate the mapping register against the exact target database.
2. Generate a report showing proposed tenant inserts and company assignments.
3. Show conflicts, duplicate company mappings, orphan references, missing evidence, and any pre-existing assignment.
4. Require zero blocking errors and written approval of the dry-run output.

### Phase D — Controlled apply

A future implementation must:

1. Take and verify a recoverable database backup.
2. Run in a maintenance window using a dedicated migration identity, not the API runtime role.
3. Start one database transaction.
4. Insert only the explicitly approved customer organisations.
5. Update only companies whose TenantId is currently NULL.
6. Verify inserted/updated row counts and all foreign-key relationships.
7. Persist an auditable record of the mapping file hash, operator, approver, execution timestamp, and outcome.
8. Commit only if all assertions pass; otherwise roll back the entire transaction.
9. Retain the signed-off mapping register and execution report as controlled migration evidence.

The backfill must never update journal entries, balances, invoices, payroll records, users, roles, or other company-owned business records. Those records remain attached to their existing CompanyId.

### Phase E — Post-backfill verification

Verify all of the following before enabling customer access:

- no in-scope company has a NULL TenantId;
- every Company.TenantId references exactly one CustomerOrganisations.Id;
- each tenant has exactly the approved set of companies;
- no company was reassigned from a non-null TenantId;
- tenant-scoped API queries return only companies belonging to the token's tenant;
- company-scoped queries and RLS tests still isolate company-owned records;
- authentication issues tokens only for active companies and active tenants;
- cross-tenant group links do not expose another tenant's books;
- scheduled jobs discover only active companies assigned to active tenants.

Only after these checks pass should a separate migration make `Companies.TenantId` non-nullable. That migration must have its own review, CI validation, backup, and deployment approval.

## 8. Rollback and recovery

- Before commit, any failed assertion must roll back the entire transaction.
- After commit, do not delete customer organisations or detach companies through an improvised reverse SQL script.
- Recovery must use the retained mapping register and execution report, a verified backup, and a reviewed corrective migration.
- Do not reverse or rewrite accounting transactions to correct a tenant assignment. Correct the ownership metadata through a controlled, audited process while preserving CompanyId and all financial history.

## 9. Explicit non-goals

This work does not:

- infer customer ownership from `ParentCompanyId`;
- merge tenants because companies share a name, address, email domain, supplier, or customer;
- create a generic/default tenant for all legacy companies;
- migrate or modify any production database;
- make `TenantId` non-nullable yet;
- alter company-level financial data or accounting history;
- grant a platform administrator ordinary cross-tenant access to customer books.

## 10. Completion criteria

The legacy assignment milestone is complete only when:

- the inventory is complete for the target environment;
- every company in scope has a documented, approved tenant mapping;
- the dry-run has zero blocking errors;
- the controlled backfill and post-backfill checks pass;
- the follow-up non-null constraint migration is reviewed and passes CI;
- customer access and tenant-isolation tests pass.

Until then, nullable TenantId is a deliberate transition state, not a permanent operating configuration.
