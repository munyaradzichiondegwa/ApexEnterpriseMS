-- ApexERP PostgreSQL row-level security (APEXERP spec 10.3). OPT-IN.
-- Apply the tenant identity migration and complete the approved legacy tenant backfill before enabling this policy.
-- The API must stamp app.tenant_id and app.company_id for an authenticated tenant request.
-- System access is available only inside an explicit SystemAccessScope or CrossTenantScope.
-- The API database role must not be a superuser and must not have BYPASSRLS.
--
-- Company rows are visible to the current tenant. Company-owned rows are visible only when
-- CompanyId is the selected company AND that company belongs to the current tenant.
-- CustomerOrganisations are visible only to their own tenant.
--
-- FORCE ROW LEVEL SECURITY applies policies to table owners as well. Superusers and roles with
-- BYPASSRLS remain exempt; use a separate elevated role for backups and controlled administration.

BEGIN;

CREATE OR REPLACE FUNCTION apex_rls_allowed(row_company uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            row_company IS NOT NULL
            AND row_company = nullif(current_setting('app.company_id', true), '')::uuid
            AND EXISTS (
                SELECT 1
                FROM "Companies" c
                WHERE c."Id" = row_company
                  AND c."TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
            )
        )
$$;

-- Tenant-level tables need direct policies. Companies must not call apex_rls_allowed here:
-- that function reads Companies, which would create an RLS policy recursion.
ALTER TABLE "Companies" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "Companies" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS apex_tenant_isolation ON "Companies";
CREATE POLICY apex_tenant_isolation ON "Companies"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            "TenantId" IS NOT NULL
            AND "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
        )
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            "TenantId" IS NOT NULL
            AND "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
        )
    );

ALTER TABLE "CustomerOrganisations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "CustomerOrganisations" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS apex_tenant_isolation ON "CustomerOrganisations";
CREATE POLICY apex_tenant_isolation ON "CustomerOrganisations"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR "Id" = nullif(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR "Id" = nullif(current_setting('app.tenant_id', true), '')::uuid
    );

-- Every company-owned table receives the same company + tenant check.
DO $$
DECLARE t record;
BEGIN
    FOR t IN
        SELECT c.table_schema, c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema
         AND tb.table_name = c.table_name
         AND tb.table_type = 'BASE TABLE'
        WHERE c.column_name = 'CompanyId'
          AND c.table_schema = current_schema()
          AND c.data_type = 'uuid'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS apex_tenant_isolation ON %I.%I', t.table_schema, t.table_name);
        EXECUTE format(
            'CREATE POLICY apex_tenant_isolation ON %I.%I USING (apex_rls_allowed("CompanyId")) WITH CHECK (apex_rls_allowed("CompanyId"))',
            t.table_schema, t.table_name);
    END LOOP;
END $$;


-- User records remain anchored to their home company. In a selected company, records are
-- visible only for users with an active membership in that same company. app.user_id is also
-- stamped from the validated JWT for request auditing; it is never accepted from a request.
DROP POLICY IF EXISTS apex_user_membership_access ON "Users";
CREATE POLICY apex_user_membership_access ON "Users"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR apex_rls_allowed("CompanyId")
        OR EXISTS (
            SELECT 1 FROM "CompanyMemberships" m
            WHERE m."UserId" = "Users"."Id"
              AND m."CompanyId" = nullif(current_setting('app.company_id', true), '')::uuid
              AND m."IsActive" = true
              AND (m."ValidFrom" IS NULL OR m."ValidFrom" <= now())
              AND (m."ValidTo" IS NULL OR m."ValidTo" > now())
        )
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR apex_rls_allowed("CompanyId")
    );xERP PostgreSQL row-level security (APEXERP spec 10.3). OPT-IN.
-- Apply the tenant identity migration and complete the approved legacy tenant backfill before enabling this policy.
-- The API must stamp app.tenant_id and app.company_id for an authenticated tenant request.
-- System access is available only inside an explicit SystemAccessScope or CrossTenantScope.
-- The API database role must not be a superuser and must not have BYPASSRLS.
--
-- Company rows are visible to the current tenant. Company-owned rows are visible only when
-- CompanyId is the selected company AND that company belongs to the current tenant.
-- CustomerOrganisations are visible only to their own tenant.
--
-- FORCE ROW LEVEL SECURITY applies policies to table owners as well. Superusers and roles with
-- BYPASSRLS remain exempt; use a separate elevated role for backups and controlled administration.

BEGIN;

CREATE OR REPLACE FUNCTION apex_rls_allowed(row_company uuid) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            row_company IS NOT NULL
            AND row_company = nullif(current_setting('app.company_id', true), '')::uuid
            AND EXISTS (
                SELECT 1
                FROM "Companies" c
                WHERE c."Id" = row_company
                  AND c."TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
            )
        )
$$;

-- Tenant-level tables need direct policies. Companies must not call apex_rls_allowed here:
-- that function reads Companies, which would create an RLS policy recursion.
ALTER TABLE "Companies" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "Companies" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS apex_tenant_isolation ON "Companies";
CREATE POLICY apex_tenant_isolation ON "Companies"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            "TenantId" IS NOT NULL
            AND "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
        )
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR (
            "TenantId" IS NOT NULL
            AND "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
        )
    );

ALTER TABLE "CustomerOrganisations" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "CustomerOrganisations" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS apex_tenant_isolation ON "CustomerOrganisations";
CREATE POLICY apex_tenant_isolation ON "CustomerOrganisations"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR "Id" = nullif(current_setting('app.tenant_id', true), '')::uuid
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR "Id" = nullif(current_setting('app.tenant_id', true), '')::uuid
    );

-- Every company-owned table receives the same company + tenant check.
DO $$
DECLARE t record;
BEGIN
    FOR t IN
        SELECT c.table_schema, c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema
         AND tb.table_name = c.table_name
         AND tb.table_type = 'BASE TABLE'
        WHERE c.column_name = 'CompanyId'
          AND c.table_schema = current_schema()
          AND c.data_type = 'uuid'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS apex_tenant_isolation ON %I.%I', t.table_schema, t.table_name);
        EXECUTE format(
            'CREATE POLICY apex_tenant_isolation ON %I.%I USING (apex_rls_allowed("CompanyId")) WITH CHECK (apex_rls_allowed("CompanyId"))',
            t.table_schema, t.table_name);
    END LOOP;
END $$;


-- A user identity remains anchored to its home company, but may be read in another
-- company only when the signed-in user has an active membership there. The app.user_id
-- value is stamped from the validated JWT by TenantSession; it is never accepted from a request.
DROP POLICY IF EXISTS apex_user_membership_access ON "Users";
CREATE POLICY apex_user_membership_access ON "Users"
    USING (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR apex_rls_allowed("CompanyId")
        OR (
            "Id" = nullif(current_setting('app.user_id', true), '')::uuid
            AND EXISTS (
                SELECT 1 FROM "CompanyMemberships" m
                WHERE m."UserId" = "Users"."Id"
                  AND m."CompanyId" = nullif(current_setting('app.company_id', true), '')::uuid
                  AND m."IsActive" = true
                  AND (m."ValidFrom" IS NULL OR m."ValidFrom" <= now())
                  AND (m."ValidTo" IS NULL OR m."ValidTo" > now())
            )
        )
    )
    WITH CHECK (
        coalesce(current_setting('app.rls_bypass', true), '') = 'on'
        OR apex_rls_allowed("CompanyId")
    );

COMMIT;
