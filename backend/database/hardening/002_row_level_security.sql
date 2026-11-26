-- ApexERP PostgreSQL row-level security (APEXERP spec 10.3). OPT-IN: run this only after setting
-- Security:RowLevelSecurity:Enabled=true so the API stamps each connection with the caller's company.
--
-- Rule: a row is visible/writable only when its CompanyId matches app.company_id AND that company belongs to
-- app.tenant_id. Explicit system contexts (app.rls_bypass = 'on') remain available for tightly controlled
-- sign-in, registration, and background operations. A normal connection missing either identity sees nothing.
-- Apply the tenant identity migration before enabling this policy: Companies.TenantId must exist.
--
-- FORCE ROW LEVEL SECURITY makes the policy apply to the table owner as well, which is the account the API normally uses.
-- Superusers and roles with BYPASSRLS (for example the one used for backups) are not restricted: keep them off the API.
-- Idempotent. Undo with 002_row_level_security_disable.sql.

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

DO $$
DECLARE t record;
BEGIN
    FOR t IN
        SELECT c.table_schema, c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name AND tb.table_type = 'BASE TABLE'
        WHERE c.column_name = 'CompanyId' AND c.table_schema = current_schema() AND c.data_type = 'uuid'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS apex_tenant_isolation ON %I.%I', t.table_schema, t.table_name);
        EXECUTE format('CREATE POLICY apex_tenant_isolation ON %I.%I USING (apex_rls_allowed("CompanyId")) WITH CHECK (apex_rls_allowed("CompanyId"))',
                       t.table_schema, t.table_name);
    END LOOP;
END $$;
