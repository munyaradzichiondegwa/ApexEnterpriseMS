using Microsoft.EntityFrameworkCore.Migrations;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace ApexERP.Infrastructure.Data.Migrations;

[DbContext(typeof(ApexErpDbContext))]
[Migration("20261127120000_AddEnterprisePlatformFoundation")]
public sealed class AddEnterprisePlatformFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "OrganisationalBranches" (
 "Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "Code" varchar(40) NOT NULL, "Name" text NOT NULL,
 "ParentBranchId" uuid NULL, "IsActive" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL,
 "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_OrganisationalBranches_CompanyId_Code" ON "OrganisationalBranches" ("CompanyId","Code");
CREATE TABLE "CompanyMemberships" (
 "Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "UserId" uuid NOT NULL, "MembershipRole" text NOT NULL, "RoleId" uuid NULL,
 "IsDefault" boolean NOT NULL, "IsActive" boolean NOT NULL, "ValidFrom" timestamptz NULL, "ValidTo" timestamptz NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_CompanyMemberships_CompanyId_UserId" ON "CompanyMemberships" ("CompanyId","UserId");
CREATE INDEX "IX_CompanyMemberships_UserId_IsActive" ON "CompanyMemberships" ("UserId","IsActive");
CREATE TABLE "SubscriptionPlans" (
 "Id" uuid PRIMARY KEY, "Code" text NOT NULL, "Name" text NOT NULL, "Description" text NOT NULL,
 "MaxSeats" integer NOT NULL, "MaxCompanies" integer NOT NULL, "ModuleCodesJson" jsonb NOT NULL,
 "MonthlyPrice" numeric(20,6) NULL, "Currency" text NOT NULL, "IsPubliclyAvailable" boolean NOT NULL, "IsActive" boolean NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_SubscriptionPlans_Code" ON "SubscriptionPlans" ("Code");
CREATE TABLE "TenantSubscriptions" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "PlanId" uuid NOT NULL, "Status" text NOT NULL,
 "StartsAt" timestamptz NOT NULL, "TrialEndsAt" timestamptz NULL, "CurrentPeriodEndsAt" timestamptz NULL,
 "SuspendedAt" timestamptz NULL, "CancelledAt" timestamptz NULL, "ExternalBillingReference" text NULL,
 "SeatOverride" integer NOT NULL, "CompanyOverride" integer NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE INDEX "IX_TenantSubscriptions_TenantId_Status" ON "TenantSubscriptions" ("TenantId","Status");
CREATE TABLE "JurisdictionProfiles" (
 "Id" uuid PRIMARY KEY, "CountryCode" text NOT NULL, "CountryName" text NOT NULL, "DefaultCurrency" text NOT NULL,
 "DefaultTimeZone" text NOT NULL, "DefaultReportingFramework" text NOT NULL, "IsActive" boolean NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_JurisdictionProfiles_CountryCode" ON "JurisdictionProfiles" ("CountryCode");
CREATE TABLE "TenantJurisdictions" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "CompanyId" uuid NULL, "CountryCode" text NOT NULL, "FunctionalCurrency" text NOT NULL,
 "FiscalYearStartMonth" integer NOT NULL, "ReportingFramework" text NOT NULL, "TaxConfigurationJson" jsonb NOT NULL,
 "IsPrimary" boolean NOT NULL, "ConfigurationStatus" text NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_TenantJurisdictions_TenantId_CompanyId_CountryCode" ON "TenantJurisdictions" ("TenantId","CompanyId","CountryCode");
CREATE TABLE "DeploymentProfiles" (
 "Id" uuid PRIMARY KEY, "Code" text NOT NULL, "Name" text NOT NULL, "Sector" text NOT NULL, "ConfigurationJson" jsonb NOT NULL,
 "IsActive" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_DeploymentProfiles_Code" ON "DeploymentProfiles" ("Code");
CREATE TABLE "TenantOnboardings" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "Status" text NOT NULL, "CompletedStepsJson" jsonb NOT NULL,
 "DeploymentProfileId" uuid NULL, "CompletedAt" timestamptz NULL, "BlockedReason" text NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_TenantOnboardings_TenantId" ON "TenantOnboardings" ("TenantId");
CREATE TABLE "TenantAdministratorInvitations" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "CompanyId" uuid NOT NULL, "Email" text NOT NULL,
 "TokenHash" text NOT NULL, "ExpiresAt" timestamptz NOT NULL, "AcceptedAt" timestamptz NULL, "AcceptedUserId" uuid NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_TenantAdministratorInvitations_TokenHash" ON "TenantAdministratorInvitations" ("TokenHash");
CREATE TABLE "PlatformAuditEvents" (
 "Id" uuid PRIMARY KEY, "ActorUserId" uuid NULL, "TenantId" uuid NULL, "Action" text NOT NULL, "TargetType" text NOT NULL,
 "TargetId" text NULL, "Outcome" text NOT NULL, "CorrelationId" text NULL, "DetailsJson" jsonb NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE INDEX "IX_PlatformAuditEvents_TenantId_CreatedAt" ON "PlatformAuditEvents" ("TenantId","CreatedAt");
CREATE TABLE "TenantSupportGrants" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "RequestedBy" uuid NOT NULL, "ApprovedBy" uuid NULL,
 "Reason" text NOT NULL, "Status" text NOT NULL, "ExpiresAt" timestamptz NOT NULL, "RevokedAt" timestamptz NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE INDEX "IX_TenantSupportGrants_TenantId_Status_ExpiresAt" ON "TenantSupportGrants" ("TenantId","Status","ExpiresAt");
CREATE TABLE "TenantOperationalChecks" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "CheckCode" text NOT NULL, "Status" text NOT NULL,
 "CheckedAt" timestamptz NOT NULL, "DurationMilliseconds" bigint NULL, "DetailsJson" jsonb NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE INDEX "IX_TenantOperationalChecks_TenantId_CheckCode_CheckedAt" ON "TenantOperationalChecks" ("TenantId","CheckCode","CheckedAt");
CREATE TABLE "TenantLifecycleJobs" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "JobType" text NOT NULL, "Status" text NOT NULL,
 "RequestedBy" text NOT NULL, "ExternalJobReference" text NULL, "StartedAt" timestamptz NULL, "CompletedAt" timestamptz NULL,
 "ResultSummary" text NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE INDEX "IX_TenantLifecycleJobs_TenantId_Status_CreatedAt" ON "TenantLifecycleJobs" ("TenantId","Status","CreatedAt");
CREATE TABLE "CompanyUserInvitations" (
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "CompanyId" uuid NOT NULL, "RoleId" uuid NOT NULL,
 "Email" text NOT NULL, "TokenHash" text NOT NULL, "ExpiresAt" timestamptz NOT NULL, "InvitedBy" uuid NOT NULL,
 "AcceptedAt" timestamptz NULL, "AcceptedUserId" uuid NULL, "CreatedAt" timestamptz NOT NULL,
 "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_CompanyUserInvitations_TokenHash" ON "CompanyUserInvitations" ("TokenHash");
CREATE INDEX "IX_CompanyUserInvitations_TenantId_Email_AcceptedAt" ON "CompanyUserInvitations" ("TenantId","Email","AcceptedAt");
ALTER TABLE "Departments" ADD COLUMN "BranchId" uuid NULL;
CREATE INDEX "IX_Departments_CompanyId_BranchId" ON "Departments" ("CompanyId","BranchId");
ALTER TABLE "CostCentres" ADD COLUMN "BranchId" uuid NULL;
CREATE INDEX "IX_CostCentres_CompanyId_BranchId" ON "CostCentres" ("CompanyId","BranchId");
-- Existing users already have an unambiguous primary company and role. Preserve that
-- established access as their default membership; this does not infer tenant ownership.
INSERT INTO "CompanyMemberships"
 ("Id","CompanyId","UserId","MembershipRole","RoleId","IsDefault","IsActive","ValidFrom","ValidTo","CreatedAt","UpdatedAt","CreatedBy","UpdatedBy")
SELECT gen_random_uuid(), u."CompanyId", u."Id", COALESCE(r."Name",'Member'), u."RoleId",
       true, u."IsActive", now(), NULL, now(), NULL, NULL, NULL
FROM "Users" u
LEFT JOIN "Roles" r ON r."Id" = u."RoleId"
ON CONFLICT ("CompanyId","UserId") DO NOTHING;

-- Baseline jurisdiction metadata only. These records contain no tax rates or legal interpretations.
INSERT INTO "JurisdictionProfiles"
 ("Id","CountryCode","CountryName","DefaultCurrency","DefaultTimeZone","DefaultReportingFramework","IsActive","CreatedAt")
VALUES
 (gen_random_uuid(),'ZW','Zimbabwe','ZWG','Africa/Harare','IFRS',true,now()),
 (gen_random_uuid(),'ZA','South Africa','ZAR','Africa/Johannesburg','IFRS',true,now()),
 (gen_random_uuid(),'BW','Botswana','BWP','Africa/Gaborone','IFRS',true,now()),
 (gen_random_uuid(),'ZM','Zambia','ZMW','Africa/Lusaka','IFRS',true,now()),
 (gen_random_uuid(),'KE','Kenya','KES','Africa/Nairobi','IFRS',true,now()),
 (gen_random_uuid(),'TZ','Tanzania','TZS','Africa/Dar_es_Salaam','IFRS',true,now()),
 (gen_random_uuid(),'UG','Uganda','UGX','Africa/Kampala','IFRS',true,now()),
 (gen_random_uuid(),'NG','Nigeria','NGN','Africa/Lagos','IFRS',true,now()),
 (gen_random_uuid(),'GH','Ghana','GHS','Africa/Accra','IFRS',true,now()),
 (gen_random_uuid(),'GB','United Kingdom','GBP','Europe/London','IFRS',true,now()),
 (gen_random_uuid(),'US','United States','USD','America/New_York','US-GAAP',true,now()),
 (gen_random_uuid(),'AU','Australia','AUD','Australia/Sydney','IFRS',true,now()),
 (gen_random_uuid(),'IN','India','INR','Asia/Kolkata','Ind AS',true,now()),
 (gen_random_uuid(),'CA','Canada','CAD','America/Toronto','ASPE/IFRS',true,now())
ON CONFLICT ("CountryCode") DO NOTHING;

-- Public self-registration is a controlled trial, never an unlicensed tenant.
INSERT INTO "SubscriptionPlans"
 ("Id","Code","Name","Description","MaxSeats","MaxCompanies","ModuleCodesJson","MonthlyPrice","Currency","IsPubliclyAvailable","IsActive","CreatedAt")
VALUES
 (gen_random_uuid(),'TRIAL','ApexERP Trial','Default 30-day evaluation plan',5,1,'["*"]'::jsonb,NULL,'USD',true,true,now())
ON CONFLICT ("Code") DO NOTHING;

-- Sector templates are starting configurations, not separate product editions or legal/statutory packs.
INSERT INTO "DeploymentProfiles"
 ("Id","Code","Name","Sector","ConfigurationJson","IsActive","CreatedAt")
VALUES
 (gen_random_uuid(),'COMMERCIAL_SME','Commercial SME','COMMERCIAL',
  '{"features":["general-ledger","accounts-receivable","accounts-payable","cash-bank","inventory","fixed-assets","payroll","procurement","crm"],"reportingFramework":"IFRS","multiEntity":false,"multiCurrency":true,"approvalWorkflow":"standard"}'::jsonb,true,now()),
 (gen_random_uuid(),'COMMERCIAL_ENTERPRISE','Commercial Enterprise','COMMERCIAL',
  '{"features":["general-ledger","accounts-receivable","accounts-payable","cash-bank","inventory","fixed-assets","payroll","procurement","crm","projects","manufacturing","consolidation","intercompany"],"reportingFramework":"IFRS","multiEntity":true,"multiCurrency":true,"approvalWorkflow":"tiered"}'::jsonb,true,now()),
 (gen_random_uuid(),'MDA','Ministry / Department / Agency','PUBLIC_SECTOR',
  '{"features":["general-ledger","appropriations","fund-accounting","commitments","procurement","tenders","budget-control","audit-trail"],"reportingFrameworkOptions":["IPSAS","CashBasisIPSAS","IFRS"],"accountingBasis":"REQUIRES_CONFIGURATION","multiEntity":true,"approvalWorkflow":"public-sector"}'::jsonb,true,now()),
 (gen_random_uuid(),'SOE','State-Owned Enterprise','STATE_OWNED',
  '{"features":["general-ledger","budget-control","procurement","tenders","public-sector-governance","consolidation","intercompany"],"reportingFrameworkOptions":["IFRS","IPSAS"],"accountingBasis":"REQUIRES_CONFIGURATION","multiEntity":true,"approvalWorkflow":"board-and-public-sector"}'::jsonb,true,now()),
 (gen_random_uuid(),'NGO_DEVELOPMENT','NGO / Development Organisation','NON_PROFIT',
  '{"features":["general-ledger","fund-accounting","grants","donor-restrictions","sub-awards","project-accounting","procurement","audit-trail"],"reportingFramework":"REQUIRES_CONFIGURATION","multiEntity":true,"multiCurrency":true,"approvalWorkflow":"donor-and-board"}'::jsonb,true,now()),
 (gen_random_uuid(),'MULTINATIONAL_GROUP','Multinational Group','MULTINATIONAL',
  '{"features":["general-ledger","multi-company","branches","multi-currency","multi-jurisdiction","consolidation","intercompany","group-reporting","transfer-pricing-data"],"reportingFramework":"IFRS","multiEntity":true,"multiCurrency":true,"multiJurisdiction":true,"approvalWorkflow":"group"}'::jsonb,true,now())
ON CONFLICT ("Code") DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_Departments_CompanyId_BranchId";
DROP INDEX IF EXISTS "IX_CostCentres_CompanyId_BranchId";
ALTER TABLE IF EXISTS "Departments" DROP COLUMN IF EXISTS "BranchId";
ALTER TABLE IF EXISTS "CostCentres" DROP COLUMN IF EXISTS "BranchId";
DROP TABLE IF EXISTS "CompanyUserInvitations";
DROP TABLE IF EXISTS "TenantLifecycleJobs";
DROP TABLE IF EXISTS "TenantOperationalChecks";
DROP TABLE IF EXISTS "TenantSupportGrants";
DROP TABLE IF EXISTS "PlatformAuditEvents";
DROP TABLE IF EXISTS "TenantAdministratorInvitations";
DROP TABLE IF EXISTS "TenantOnboardings";
DROP TABLE IF EXISTS "DeploymentProfiles";
DROP TABLE IF EXISTS "TenantJurisdictions";
DROP TABLE IF EXISTS "JurisdictionProfiles";
DROP TABLE IF EXISTS "TenantSubscriptions";
DROP TABLE IF EXISTS "SubscriptionPlans";
DROP TABLE IF EXISTS "CompanyMemberships";
DROP TABLE IF EXISTS "OrganisationalBranches";
""");
    }
}
