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
 "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "CountryCode" text NOT NULL, "FunctionalCurrency" text NOT NULL,
 "FiscalYearStartMonth" integer NOT NULL, "ReportingFramework" text NOT NULL, "TaxConfigurationJson" jsonb NOT NULL,
 "IsPrimary" boolean NOT NULL, "ConfigurationStatus" text NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NULL, "CreatedBy" uuid NULL, "UpdatedBy" uuid NULL);
CREATE UNIQUE INDEX "IX_TenantJurisdictions_TenantId_CountryCode" ON "TenantJurisdictions" ("TenantId","CountryCode");
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
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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
