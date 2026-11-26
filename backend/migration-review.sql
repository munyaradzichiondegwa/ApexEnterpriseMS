START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    CREATE TABLE "ExceptionItems" (
        "Id" uuid NOT NULL,
        "Category" integer NOT NULL,
        "SourceType" text NOT NULL,
        "SourceId" uuid NOT NULL,
        "Message" text NOT NULL,
        "Status" integer NOT NULL,
        "OwnerUserId" uuid,
        "RetryCount" integer NOT NULL,
        "LastAttemptAt" timestamp with time zone,
        "ResolvedAt" timestamp with time zone,
        "ResolvedBy" uuid,
        "ResolutionNote" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ExceptionItems" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    CREATE UNIQUE INDEX "IX_AccountingPeriods_CompanyId_Year_PeriodNo" ON "AccountingPeriods" ("CompanyId", "Year", "PeriodNo");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    CREATE INDEX "IX_ExceptionItems_CompanyId_Status_Category" ON "ExceptionItems" ("CompanyId", "Status", "Category");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    CREATE UNIQUE INDEX "IX_IntercompanyTransactions_SourceArInvoiceId" ON "IntercompanyTransactions" ("SourceArInvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    CREATE UNIQUE INDEX "IX_JournalEntries_ReversalOfId" ON "JournalEntries" ("ReversalOfId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    ALTER TABLE "JournalLines" ADD CONSTRAINT "CK_JournalLines_Amounts" CHECK ("Debit" >= 0 AND "Credit" >= 0 AND NOT ("Debit" > 0 AND "Credit" > 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    ALTER TABLE "JournalLines" ADD CONSTRAINT "CK_JournalLines_ExchangeRate" CHECK ("ExchangeRate" > 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261024120000_AuditHardeningPhase1And2') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261024120000_AuditHardeningPhase1And2', '8.0.10');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105120000_AppendOnlyDatabaseGuards') THEN
    -- ApexERP database-level append-only guards (spec sections 12.4, 23.2, 28.3 "audit-log tampering").
    -- The application already refuses these changes (ApexErpDbContext.EnforceLedgerImmutability); this makes the
    -- DATABASE refuse them too, so a bug, a raw ExecuteUpdate/ExecuteDelete, or a person with SQL access to the
    -- application login cannot silently alter posted history.
    -- JournalStatus values: 0 Draft, 1 Posted, 2 Reversed, 3 Submitted, 4 Approved, 5 Rejected, 6 Cancelled.
    -- Idempotent: safe to run more than once.

    CREATE OR REPLACE FUNCTION apex_block_audit_changes() RETURNS trigger AS $$
    BEGIN
        RAISE EXCEPTION 'The audit log is append-only: % is not permitted.', TG_OP USING ERRCODE = 'restrict_violation';
    END;
    $$ LANGUAGE plpgsql;

    DROP TRIGGER IF EXISTS trg_auditlogs_append_only ON "AuditLogs";
    CREATE TRIGGER trg_auditlogs_append_only BEFORE UPDATE OR DELETE ON "AuditLogs"
        FOR EACH ROW EXECUTE FUNCTION apex_block_audit_changes();
    DROP TRIGGER IF EXISTS trg_auditlogs_no_truncate ON "AuditLogs";
    CREATE TRIGGER trg_auditlogs_no_truncate BEFORE TRUNCATE ON "AuditLogs"
        FOR EACH STATEMENT EXECUTE FUNCTION apex_block_audit_changes();

    CREATE OR REPLACE FUNCTION apex_guard_journal_lines() RETURNS trigger AS $$
    DECLARE parent_status integer;
    BEGIN
        IF TG_OP = 'UPDATE' THEN
            RAISE EXCEPTION 'Journal lines are immutable. Correct a posted entry by reversing it.' USING ERRCODE = 'restrict_violation';
        END IF;
        SELECT "Status" INTO parent_status FROM "JournalEntries" WHERE "Id" = OLD."JournalEntryId";
        IF parent_status IN (1, 2) THEN
            RAISE EXCEPTION 'Lines of a posted journal cannot be deleted. Reverse the journal instead.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN OLD;
    END;
    $$ LANGUAGE plpgsql;

    DROP TRIGGER IF EXISTS trg_journallines_guard ON "JournalLines";
    CREATE TRIGGER trg_journallines_guard BEFORE UPDATE OR DELETE ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION apex_guard_journal_lines();

    CREATE OR REPLACE FUNCTION apex_guard_posted_journal() RETURNS trigger AS $$
    BEGIN
        IF OLD."Status" NOT IN (1, 2) THEN
            IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END IF;

        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'A posted journal entry cannot be deleted. Reverse it instead.' USING ERRCODE = 'restrict_violation';
        END IF;

        -- Only Status (Posted -> Reversed) and the audit stamps may change on a posted entry.
        IF (to_jsonb(NEW) - 'Status' - 'UpdatedAt' - 'UpdatedBy') IS DISTINCT FROM
           (to_jsonb(OLD) - 'Status' - 'UpdatedAt' - 'UpdatedBy') THEN
            RAISE EXCEPTION 'A posted journal entry cannot be edited. Reverse it and post a corrected entry.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NEW."Status" <> OLD."Status" AND NOT (OLD."Status" = 1 AND NEW."Status" = 2) THEN
            RAISE EXCEPTION 'A journal entry cannot move from status % to %.', OLD."Status", NEW."Status" USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$ LANGUAGE plpgsql;

    DROP TRIGGER IF EXISTS trg_journalentries_guard ON "JournalEntries";
    CREATE TRIGGER trg_journalentries_guard BEFORE UPDATE OR DELETE ON "JournalEntries"
        FOR EACH ROW EXECUTE FUNCTION apex_guard_posted_journal();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261105120000_AppendOnlyDatabaseGuards') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261105120000_AppendOnlyDatabaseGuards', '8.0.10');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Users" ADD "FailedLoginCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Users" ADD "LockedUntil" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Users" ADD "PasswordChangedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Suppliers" ADD "BankAccountNo" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Suppliers" ADD "BankBranch" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Suppliers" ADD "BankName" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "Suppliers" ADD "PaymentHold" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "GlAccounts" ADD "AllowedSourcesCsv" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "GlAccounts" ADD "IsPostable" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "FixedAssets" ADD "ParentAssetId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApPayments" ADD "AuthorisedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApPayments" ADD "AuthorisedBy" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApPayments" ADD "PaymentRunId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApPayments" ADD "PreparedBy" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApPayments" ADD "WithholdingCertificateNo" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApInvoices" ADD "MatchNotes" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApInvoices" ADD "MatchOverriddenBy" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApInvoices" ADD "MatchStatus" text NOT NULL DEFAULT 'NotRequired';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApInvoices" ADD "PurchaseOrderId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    ALTER TABLE "ApInvoices" ADD "SupplierInvoiceNo" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "AccountingPolicyVersions" (
        "Id" uuid NOT NULL,
        "Area" text NOT NULL,
        "Version" integer NOT NULL,
        "Description" text NOT NULL,
        "EffectiveFrom" timestamp with time zone NOT NULL,
        "ApprovedBy" uuid,
        "ApprovedAt" timestamp with time zone,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_AccountingPolicyVersions" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "AccrualSchedules" (
        "Id" uuid NOT NULL,
        "Description" text NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "Currency" text NOT NULL,
        "AccrualDate" timestamp with time zone NOT NULL,
        "ExpenseAccountId" uuid NOT NULL,
        "AccruedLiabilityAccountId" uuid NOT NULL,
        "SupplierId" uuid,
        "AutoReverse" boolean NOT NULL,
        "Status" text NOT NULL,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "ReversalJournalEntryId" uuid,
        "MatchedApInvoiceId" uuid,
        "CostCentreId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_AccrualSchedules" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ApPaymentRuns" (
        "Id" uuid NOT NULL,
        "RunNo" text NOT NULL,
        "PaymentDate" timestamp with time zone NOT NULL,
        "Currency" text NOT NULL,
        "BankGlAccountId" uuid NOT NULL,
        "ApControlAccountId" uuid NOT NULL,
        "TotalAmount" numeric(20,6) NOT NULL,
        "Status" text NOT NULL,
        "PreparedBy" uuid,
        "AuthorisedBy" uuid,
        "AuthorisedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ApPaymentRuns" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ApprovalDelegations" (
        "Id" uuid NOT NULL,
        "DelegatorUserId" uuid NOT NULL,
        "DelegateUserId" uuid NOT NULL,
        "EntityType" text,
        "MaxAmount" numeric(20,6),
        "ValidFrom" timestamp with time zone NOT NULL,
        "ValidTo" timestamp with time zone NOT NULL,
        "Reason" text NOT NULL,
        "IsEmergency" boolean NOT NULL,
        "RevokedAt" timestamp with time zone,
        "ReviewedBy" uuid,
        "ReviewedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ApprovalDelegations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ApprovalMatrixTiers" (
        "Id" uuid NOT NULL,
        "EntityType" text NOT NULL,
        "UpToAmount" numeric(20,6) NOT NULL,
        "RequiredRoleName" text NOT NULL,
        "SlaHours" integer NOT NULL,
        "EscalateToRoleName" text,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ApprovalMatrixTiers" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ArCreditNotes" (
        "Id" uuid NOT NULL,
        "CreditNoteNo" text NOT NULL,
        "ArInvoiceId" uuid NOT NULL,
        "CustomerId" uuid NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "Reason" text NOT NULL,
        "Status" text NOT NULL,
        "PreparedBy" uuid,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "RevenueReversalAccountId" uuid,
        "ArControlAccountId" uuid,
        "VatOutputAccountId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ArCreditNotes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "AuditEvidenceReferences" (
        "Id" uuid NOT NULL,
        "EntityType" text NOT NULL,
        "EntityId" uuid NOT NULL,
        "FileName" text NOT NULL,
        "ContentType" text NOT NULL,
        "SizeBytes" bigint NOT NULL,
        "Sha256" text NOT NULL,
        "Content" bytea NOT NULL,
        "Description" text,
        "UploadedBy" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_AuditEvidenceReferences" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "BankReconciliations" (
        "Id" uuid NOT NULL,
        "BankAccountId" uuid NOT NULL,
        "StatementDate" timestamp with time zone NOT NULL,
        "StatementBalance" numeric(20,6) NOT NULL,
        "BookBalance" numeric(20,6) NOT NULL,
        "UnreconciledItemsTotal" numeric(20,6) NOT NULL,
        "Difference" numeric(20,6) NOT NULL,
        "PreparedBy" uuid,
        "ReviewedBy" uuid,
        "CertifiedAt" timestamp with time zone,
        "Status" text NOT NULL,
        "Notes" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_BankReconciliations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "CompanySettings" (
        "Id" uuid NOT NULL,
        "Key" text NOT NULL,
        "Value" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_CompanySettings" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ControlExceptions" (
        "Id" uuid NOT NULL,
        "DetectorCode" text NOT NULL,
        "Fingerprint" text NOT NULL,
        "Category" text NOT NULL,
        "Severity" text NOT NULL,
        "RiskScore" integer NOT NULL,
        "SourceType" text NOT NULL,
        "SourceId" uuid,
        "Description" text NOT NULL,
        "Status" text NOT NULL,
        "OwnerUserId" uuid,
        "DueDate" timestamp with time zone,
        "InvestigationNotes" text,
        "ResolutionNote" text,
        "ResolvedBy" uuid,
        "ReviewedBy" uuid,
        "ClosedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ControlExceptions" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ControlTestResults" (
        "Id" uuid NOT NULL,
        "TestCode" text NOT NULL,
        "Title" text NOT NULL,
        "Passed" boolean NOT NULL,
        "Details" text NOT NULL,
        "RunAt" timestamp with time zone NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ControlTestResults" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "CreditLimitOverrides" (
        "Id" uuid NOT NULL,
        "CustomerId" uuid NOT NULL,
        "TemporaryLimit" numeric(20,6) NOT NULL,
        "Reason" text NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "ApprovedBy" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_CreditLimitOverrides" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "EclLossRates" (
        "Id" uuid NOT NULL,
        "FromDays" integer NOT NULL,
        "ToDays" integer NOT NULL,
        "LossRatePct" numeric(20,6) NOT NULL,
        "EffectiveFrom" timestamp with time zone NOT NULL,
        "ApprovedBy" uuid,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_EclLossRates" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "EclProvisionRuns" (
        "Id" uuid NOT NULL,
        "AsOfDate" timestamp with time zone NOT NULL,
        "TotalReceivables" numeric(20,6) NOT NULL,
        "RequiredAllowance" numeric(20,6) NOT NULL,
        "PreviousAllowance" numeric(20,6) NOT NULL,
        "Movement" numeric(20,6) NOT NULL,
        "MatrixSnapshotJson" text NOT NULL,
        "Status" text NOT NULL,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "BadDebtExpenseAccountId" uuid,
        "AllowanceAccountId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_EclProvisionRuns" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "IdempotencyRecords" (
        "Id" uuid NOT NULL,
        "Scope" text NOT NULL,
        "Key" text NOT NULL,
        "RequestHash" text NOT NULL,
        "ResponseStatus" integer NOT NULL,
        "ResponseBody" text,
        "UserId" uuid,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_IdempotencyRecords" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ImpairmentAssessments" (
        "Id" uuid NOT NULL,
        "FixedAssetId" uuid NOT NULL,
        "AssessmentDate" timestamp with time zone NOT NULL,
        "CarryingAmount" numeric(20,6) NOT NULL,
        "FairValueLessCostsToSell" numeric(20,6) NOT NULL,
        "ValueInUse" numeric(20,6) NOT NULL,
        "DiscountRatePct" numeric(20,6) NOT NULL,
        "RecoverableAmount" numeric(20,6) NOT NULL,
        "ImpairmentLoss" numeric(20,6) NOT NULL,
        "Indicators" text NOT NULL,
        "Assumptions" text NOT NULL,
        "Status" text NOT NULL,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "ImpairmentLossAccountId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ImpairmentAssessments" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "IntegrationEndpoints" (
        "Id" uuid NOT NULL,
        "Provider" text NOT NULL,
        "Name" text NOT NULL,
        "BaseUrl" text,
        "SecretName" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "SettlementBankGlAccountId" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_IntegrationEndpoints" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "IntegrationMessages" (
        "Id" uuid NOT NULL,
        "Provider" text NOT NULL,
        "Direction" text NOT NULL,
        "ExternalRef" text NOT NULL,
        "PayloadJson" text NOT NULL,
        "SignatureValid" boolean NOT NULL,
        "Status" text NOT NULL,
        "Attempts" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone,
        "LastError" text,
        "ResultReceiptId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_IntegrationMessages" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "InventoryNrvAssessments" (
        "Id" uuid NOT NULL,
        "InventoryItemId" uuid NOT NULL,
        "AssessmentDate" timestamp with time zone NOT NULL,
        "Quantity" numeric(20,6) NOT NULL,
        "UnitCost" numeric(20,6) NOT NULL,
        "EstimatedSellingPrice" numeric(20,6) NOT NULL,
        "CostsToSell" numeric(20,6) NOT NULL,
        "NetRealisableValue" numeric(20,6) NOT NULL,
        "WriteDownAmount" numeric(20,6) NOT NULL,
        "Kind" text NOT NULL,
        "Reason" text NOT NULL,
        "Status" text NOT NULL,
        "PreparedBy" uuid,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "WriteDownExpenseAccountId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_InventoryNrvAssessments" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "JobExecutions" (
        "Id" uuid NOT NULL,
        "JobCode" text NOT NULL,
        "RunKey" text NOT NULL,
        "StartedAt" timestamp with time zone NOT NULL,
        "FinishedAt" timestamp with time zone,
        "Status" text NOT NULL,
        "Attempt" integer NOT NULL,
        "Message" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_JobExecutions" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "MaterialityConfigurations" (
        "Id" uuid NOT NULL,
        "Version" integer NOT NULL,
        "Currency" text NOT NULL,
        "MediumThreshold" numeric(20,6) NOT NULL,
        "HighThreshold" numeric(20,6) NOT NULL,
        "CriticalThreshold" numeric(20,6) NOT NULL,
        "EffectiveFrom" timestamp with time zone NOT NULL,
        "ApprovedBy" uuid,
        "ApprovedAt" timestamp with time zone,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_MaterialityConfigurations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "MfaRecoveryCodes" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "CodeHash" text NOT NULL,
        "UsedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_MfaRecoveryCodes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PeriodCloseTasks" (
        "Id" uuid NOT NULL,
        "PeriodId" uuid NOT NULL,
        "Code" text NOT NULL,
        "Description" text NOT NULL,
        "SortOrder" integer NOT NULL,
        "IsAutomatic" boolean NOT NULL,
        "Status" text NOT NULL,
        "AssignedTo" uuid,
        "CompletedBy" uuid,
        "CompletedAt" timestamp with time zone,
        "Note" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_PeriodCloseTasks" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PettyCashFunds" (
        "Id" uuid NOT NULL,
        "Name" text NOT NULL,
        "GlAccountId" uuid NOT NULL,
        "ImprestAmount" numeric(20,6) NOT NULL,
        "CustodianUserId" uuid,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_PettyCashFunds" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PettyCashVouchers" (
        "Id" uuid NOT NULL,
        "PettyCashFundId" uuid NOT NULL,
        "VoucherDate" timestamp with time zone NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "ExpenseAccountId" uuid NOT NULL,
        "Description" text NOT NULL,
        "Status" text NOT NULL,
        "PreparedBy" uuid,
        "ApprovedBy" uuid,
        "JournalEntryId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_PettyCashVouchers" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PrepaymentSchedules" (
        "Id" uuid NOT NULL,
        "Description" text NOT NULL,
        "TotalAmount" numeric(20,6) NOT NULL,
        "Currency" text NOT NULL,
        "StartDate" timestamp with time zone NOT NULL,
        "Months" integer NOT NULL,
        "PrepaidAssetAccountId" uuid NOT NULL,
        "ExpenseAccountId" uuid NOT NULL,
        "SupplierId" uuid,
        "Status" text NOT NULL,
        "ApprovedBy" uuid,
        "CostCentreId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_PrepaymentSchedules" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ReconciliationRuns" (
        "Id" uuid NOT NULL,
        "Area" text NOT NULL,
        "AsOfDate" timestamp with time zone NOT NULL,
        "SubledgerTotal" numeric(20,6) NOT NULL,
        "GlTotal" numeric(20,6) NOT NULL,
        "Difference" numeric(20,6) NOT NULL,
        "Status" text NOT NULL,
        "Explanation" text,
        "RunBy" uuid,
        "SignedOffBy" uuid,
        "SignedOffAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_ReconciliationRuns" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "RefreshTokens" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "FamilyId" uuid NOT NULL,
        "TokenHash" text NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "RevokedAt" timestamp with time zone,
        "ReplacedByTokenHash" text,
        "CreatedByIp" text,
        "RevokedReason" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_RefreshTokens" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "RevenueContracts" (
        "Id" uuid NOT NULL,
        "ContractNo" text NOT NULL,
        "CustomerId" uuid NOT NULL,
        "StartDate" timestamp with time zone NOT NULL,
        "EndDate" timestamp with time zone,
        "TransactionPrice" numeric(20,6) NOT NULL,
        "Currency" text NOT NULL,
        "ReceivableAccountId" uuid NOT NULL,
        "ContractLiabilityAccountId" uuid NOT NULL,
        "ContractAssetAccountId" uuid,
        "Status" text NOT NULL,
        "ModifiedFromContractId" uuid,
        "ModificationNote" text,
        "BilledToDate" numeric(20,6) NOT NULL,
        "BillingJournalEntryId" uuid,
        "ApprovedBy" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_RevenueContracts" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "SoDConflictRules" (
        "Id" uuid NOT NULL,
        "PermissionA" text NOT NULL,
        "PermissionB" text NOT NULL,
        "Reason" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_SoDConflictRules" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "StatutoryRates" (
        "Id" uuid NOT NULL,
        "Code" text NOT NULL,
        "RatePct" numeric(20,6) NOT NULL,
        "Ceiling" numeric(20,6),
        "Currency" text NOT NULL,
        "EffectiveFrom" timestamp with time zone NOT NULL,
        "EffectiveTo" timestamp with time zone,
        "SourceReference" text,
        "ApprovedBy" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_StatutoryRates" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "SupplierBankChanges" (
        "Id" uuid NOT NULL,
        "SupplierId" uuid NOT NULL,
        "NewBankName" text NOT NULL,
        "NewBankAccountNo" text NOT NULL,
        "NewBankBranch" text,
        "RequestedBy" uuid NOT NULL,
        "VerificationNote" text,
        "VerifiedBy" uuid,
        "Status" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        "CompanyId" uuid NOT NULL,
        CONSTRAINT "PK_SupplierBankChanges" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "ApPaymentRunItems" (
        "Id" uuid NOT NULL,
        "ApPaymentRunId" uuid NOT NULL,
        "SupplierId" uuid NOT NULL,
        "ApInvoiceId" uuid NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "ApPaymentId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        CONSTRAINT "PK_ApPaymentRunItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_ApPaymentRunItems_ApPaymentRuns_ApPaymentRunId" FOREIGN KEY ("ApPaymentRunId") REFERENCES "ApPaymentRuns" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PrepaymentScheduleLines" (
        "Id" uuid NOT NULL,
        "PrepaymentScheduleId" uuid NOT NULL,
        "Sequence" integer NOT NULL,
        "PeriodStart" timestamp with time zone NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "JournalEntryId" uuid,
        "PostedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        CONSTRAINT "PK_PrepaymentScheduleLines" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PrepaymentScheduleLines_PrepaymentSchedules_PrepaymentSched~" FOREIGN KEY ("PrepaymentScheduleId") REFERENCES "PrepaymentSchedules" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "PerformanceObligations" (
        "Id" uuid NOT NULL,
        "RevenueContractId" uuid NOT NULL,
        "Description" text NOT NULL,
        "StandaloneSellingPrice" numeric(20,6) NOT NULL,
        "AllocatedPrice" numeric(20,6) NOT NULL,
        "Pattern" text NOT NULL,
        "StartDate" timestamp with time zone NOT NULL,
        "EndDate" timestamp with time zone,
        "RevenueAccountId" uuid NOT NULL,
        "SatisfiedOn" timestamp with time zone,
        "SatisfactionJournalEntryId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        CONSTRAINT "PK_PerformanceObligations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PerformanceObligations_RevenueContracts_RevenueContractId" FOREIGN KEY ("RevenueContractId") REFERENCES "RevenueContracts" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE TABLE "RevenueScheduleLines" (
        "Id" uuid NOT NULL,
        "PerformanceObligationId" uuid NOT NULL,
        "Sequence" integer NOT NULL,
        "PeriodStart" timestamp with time zone NOT NULL,
        "Amount" numeric(20,6) NOT NULL,
        "JournalEntryId" uuid,
        "RecognisedAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        "CreatedBy" uuid,
        "UpdatedBy" uuid,
        CONSTRAINT "PK_RevenueScheduleLines" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_RevenueScheduleLines_PerformanceObligations_PerformanceObli~" FOREIGN KEY ("PerformanceObligationId") REFERENCES "PerformanceObligations" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_ApInvoices_CompanyId_SupplierId_SupplierInvoiceNo" ON "ApInvoices" ("CompanyId", "SupplierId", "SupplierInvoiceNo") WHERE "SupplierInvoiceNo" IS NOT NULL AND "Status" <> 4;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_AccountingPolicyVersions_CompanyId_Area_Version" ON "AccountingPolicyVersions" ("CompanyId", "Area", "Version");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_ApPaymentRunItems_ApPaymentRunId" ON "ApPaymentRunItems" ("ApPaymentRunId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_ApPaymentRuns_CompanyId_RunNo" ON "ApPaymentRuns" ("CompanyId", "RunNo");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_ApprovalDelegations_CompanyId_DelegateUserId_ValidTo" ON "ApprovalDelegations" ("CompanyId", "DelegateUserId", "ValidTo");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_ApprovalMatrixTiers_CompanyId_EntityType_UpToAmount" ON "ApprovalMatrixTiers" ("CompanyId", "EntityType", "UpToAmount");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_ArCreditNotes_CompanyId_CreditNoteNo" ON "ArCreditNotes" ("CompanyId", "CreditNoteNo");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_AuditEvidenceReferences_CompanyId_EntityType_EntityId" ON "AuditEvidenceReferences" ("CompanyId", "EntityType", "EntityId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_CompanySettings_CompanyId_Key" ON "CompanySettings" ("CompanyId", "Key");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_ControlExceptions_CompanyId_Fingerprint" ON "ControlExceptions" ("CompanyId", "Fingerprint");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_ControlExceptions_CompanyId_Status_Severity" ON "ControlExceptions" ("CompanyId", "Status", "Severity");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_EclLossRates_CompanyId_FromDays" ON "EclLossRates" ("CompanyId", "FromDays");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_IdempotencyRecords_CompanyId_Scope_Key" ON "IdempotencyRecords" ("CompanyId", "Scope", "Key");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_IntegrationMessages_CompanyId_Provider_ExternalRef" ON "IntegrationMessages" ("CompanyId", "Provider", "ExternalRef");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_IntegrationMessages_Status_NextAttemptAt" ON "IntegrationMessages" ("Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_JobExecutions_CompanyId_JobCode_RunKey" ON "JobExecutions" ("CompanyId", "JobCode", "RunKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_MaterialityConfigurations_CompanyId_Version" ON "MaterialityConfigurations" ("CompanyId", "Version");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_MfaRecoveryCodes_UserId_CodeHash" ON "MfaRecoveryCodes" ("UserId", "CodeHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_PerformanceObligations_RevenueContractId" ON "PerformanceObligations" ("RevenueContractId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_PeriodCloseTasks_CompanyId_PeriodId_Code" ON "PeriodCloseTasks" ("CompanyId", "PeriodId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_PrepaymentScheduleLines_PrepaymentScheduleId" ON "PrepaymentScheduleLines" ("PrepaymentScheduleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_ReconciliationRuns_CompanyId_Area_AsOfDate" ON "ReconciliationRuns" ("CompanyId", "Area", "AsOfDate");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_RefreshTokens_UserId_FamilyId" ON "RefreshTokens" ("UserId", "FamilyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE UNIQUE INDEX "IX_RevenueContracts_CompanyId_ContractNo" ON "RevenueContracts" ("CompanyId", "ContractNo");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_RevenueScheduleLines_PerformanceObligationId" ON "RevenueScheduleLines" ("PerformanceObligationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    CREATE INDEX "IX_StatutoryRates_CompanyId_Code_EffectiveFrom" ON "StatutoryRates" ("CompanyId", "Code", "EffectiveFrom");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261124084104_ControlsAndAutomation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261124084104_ControlsAndAutomation', '8.0.10');
    END IF;
END $EF$;
COMMIT;

