-- Removes the row-level security installed by 002_row_level_security.sql.
DO $$
DECLARE t record;
BEGIN
    FOR t IN
        SELECT c.table_schema, c.table_name FROM information_schema.columns c
        JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name AND tb.table_type = 'BASE TABLE'
        WHERE c.column_name = 'CompanyId' AND c.table_schema = current_schema() AND c.data_type = 'uuid'
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS apex_tenant_isolation ON %I.%I', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I NO FORCE ROW LEVEL SECURITY', t.table_schema, t.table_name);
        EXECUTE format('ALTER TABLE %I.%I DISABLE ROW LEVEL SECURITY', t.table_schema, t.table_name);
    END LOOP;
END $$;
DROP FUNCTION IF EXISTS apex_rls_allowed(uuid);
