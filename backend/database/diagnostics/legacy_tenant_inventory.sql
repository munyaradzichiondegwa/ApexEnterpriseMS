-- READ-ONLY legacy company/tenant inventory.
-- Run only against the explicitly selected environment using an authorised read-only
-- inventory identity that can see all Companies and CustomerOrganisations rows.
-- This script does not create, update, or delete data. Do not remove the READ ONLY mode.

BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '60s';

-- Summary: every company must be accounted for in the approved mapping scope.
SELECT
    COUNT(*) AS total_companies,
    COUNT(*) FILTER (WHERE c."TenantId" IS NULL) AS unassigned_companies,
    COUNT(*) FILTER (WHERE c."TenantId" IS NOT NULL) AS assigned_companies,
    COUNT(*) FILTER (WHERE c."TenantId" IS NOT NULL AND t."Id" IS NULL) AS orphaned_tenant_references,
    COUNT(*) FILTER (WHERE c."TenantId" IS NOT NULL AND t."Id" IS NOT NULL AND NOT t."IsActive") AS assigned_to_inactive_tenants
FROM "Companies" c
LEFT JOIN "CustomerOrganisations" t ON t."Id" = c."TenantId";

-- Company inventory. ParentCompanyId is displayed for human review only;
-- it is NOT evidence that two companies belong to the same tenant.
SELECT
    c."Id" AS "CompanyId",
    c."Name" AS "CompanyName",
    c."RegistrationNo" AS "CompanyRegistrationNo",
    c."TenantId",
    t."Name" AS "CurrentTenantName",
    t."RegistrationNumber" AS "CurrentTenantRegistrationNumber",
    t."IsActive" AS "CurrentTenantIsActive",
    c."ParentCompanyId",
    c."IsActive" AS "CompanyIsActive",
    c."CreatedAt"
FROM "Companies" c
LEFT JOIN "CustomerOrganisations" t ON t."Id" = c."TenantId"
ORDER BY c."Name", c."Id";

-- Any existing tenant assignment must be reconciled, never silently overwritten.
SELECT
    c."Id" AS "CompanyId",
    c."Name" AS "CompanyName",
    c."TenantId",
    t."Id" AS "ResolvedTenantId"
FROM "Companies" c
LEFT JOIN "CustomerOrganisations" t ON t."Id" = c."TenantId"
WHERE c."TenantId" IS NOT NULL
  AND t."Id" IS NULL
ORDER BY c."Name", c."Id";

COMMIT;
