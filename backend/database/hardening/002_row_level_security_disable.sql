-- Removes the row-level security installed by 002_row_level_security.sql.
-- This is an administrative rollback script; execute only with the database owner's role.

BEGIN;

DROP POLICY IF EXISTS apex_tenant_isolation ON "Companies";
ALTER TABLE "Companies" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE "Companies" DISABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS apex_tenant_isolation ON "CustomerOrganisations";
ALTER TABLE "CustomerOrganisations" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE "CustomerOrganisations" DISABLE ROW LEVEL SECURITY;

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
        EXECUTE format('DROP POLICY IF EXISTS apex_tenant_isolation ON %I.%I', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I NO FORCE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I DISABLE ROW LEVEL SECURITY', t.table_schema, t.table_name);
    END LOOP;
END $;

-- Remove the additional Users policy before dropping the helper function it calls.
DROP POLICY IF EXISTS apex_user_membership_access ON "Users";

DROP FUNCTION IF EXISTS apex_rls_allowed(uuid);

COMMIT;
