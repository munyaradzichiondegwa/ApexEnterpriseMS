using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApexERP.Infrastructure.Data
{
    /// <summary>
    /// Central EF Core context. Multi-tenancy is enforced with two layers of global query filters:
    /// CurrentTenantId scopes customer organisations and legal companies; CurrentCompanyId scopes
    /// company-owned business records. Both identities are resolved from the signed access token.
    ///
    /// The filters FAIL CLOSED when either required identity is missing. Code that legitimately
    /// operates across companies must use IgnoreQueryFilters() with explicit company predicates and
    /// a narrowly scoped CrossTenantScope or SystemAccessScope when PostgreSQL RLS is enabled.
    /// IgnoreQueryFilters() alone never bypasses database-level RLS.
    /// </summary>
    public class ApexErpDbContext : DbContext
    {
        /// <summary>Resolved once per request from the authenticated user's JWT claims.</summary>
        public Guid? CurrentCompanyId { get; set; }

        /// <summary>Customer organisation (tenant) resolved from the signed access token.</summary>
        public Guid? CurrentTenantId { get; set; }

        /// <summary>The signed-in user for this request (from the JWT "sub" claim). Used to stamp
        /// CreatedBy/UpdatedBy on every saved row, which segregation-of-duties checks rely on.</summary>
        public Guid? CurrentUserId { get; set; }

        /// <summary>Client IP of the current request (set by the tenant middleware). Audit rows use it when no
        /// address is passed explicitly, so every audited action records where it came from (spec 12.2).</summary>
        public string? CurrentIpAddress { get; set; }

        /// <summary>True only inside a <see cref="CrossTenantScope"/>: the code deliberately reads across companies (group consolidation,
        /// inter-company postings). With row-level security enabled this is what lets the database allow it.</summary>
        public bool AllowCrossTenant { get; set; }

        /// <summary>True only inside an explicitly named platform/system operation (authentication, tenant resolution,
        /// webhook verification, or background-job discovery). A missing company identity alone never grants RLS bypass.</summary>
        internal bool AllowSystemAccess { get; set; }

        internal string? SystemAccessReason { get; set; }

        public ApexErpDbContext(DbContextOptions<ApexErpDbContext> options) : base(options) { }

        // Core / tenancy
        public DbSet<CustomerOrganisation> CustomerOrganisations => Set<CustomerOrganisation>();
        public DbSet<Company> Companies => Set<Company>();
        public DbSet<Role> Roles => Set<Role>();
        public DbSet<User> Users => Set<User>();
        public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
        public DbSet<WorkflowRequest> WorkflowRequests => Set<WorkflowRequest>();
        public DbSet<WorkflowApproval> WorkflowApprovals => Set<WorkflowApproval>();
        public DbSet<ApprovalChain> ApprovalChains => Set<ApprovalChain>();
        public DbSet<ApprovalChainTier> ApprovalChainTiers => Set<ApprovalChainTier>();

        // Financial / GL
        public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
        public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
        public DbSet<CostCentre> CostCentres => Set<CostCentre>();
        public DbSet<Segment> Segments => Set<Segment>();
        public DbSet<StatisticalAccount> StatisticalAccounts => Set<StatisticalAccount>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalLine> JournalLines => Set<JournalLine>();
        public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
        public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
        public DbSet<PayeTaxBand> PayeTaxBands => Set<PayeTaxBand>();
        public DbSet<DividendDeclaration> DividendDeclarations => Set<DividendDeclaration>();
        public DbSet<FinancialStatementNote> FinancialStatementNotes => Set<FinancialStatementNote>();

        // AR / AP / Cash
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<ArInvoice> ArInvoices => Set<ArInvoice>();
        public DbSet<ArInvoiceLine> ArInvoiceLines => Set<ArInvoiceLine>();
        public DbSet<ArReceipt> ArReceipts => Set<ArReceipt>();
        public DbSet<ArReceiptAllocation> ArReceiptAllocations => Set<ArReceiptAllocation>();
        public DbSet<Supplier> Suppliers => Set<Supplier>();
        public DbSet<ApInvoice> ApInvoices => Set<ApInvoice>();
        public DbSet<ApInvoiceLine> ApInvoiceLines => Set<ApInvoiceLine>();
        public DbSet<ApPayment> ApPayments => Set<ApPayment>();
        public DbSet<ApPaymentAllocation> ApPaymentAllocations => Set<ApPaymentAllocation>();
        public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
        public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

        // Inventory / Fixed Assets
        public DbSet<Warehouse> Warehouses => Set<Warehouse>();
        public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
        public DbSet<InventoryCostLayer> InventoryCostLayers => Set<InventoryCostLayer>();
        public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
        public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();
        public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
        public DbSet<DepreciationEntry> DepreciationEntries => Set<DepreciationEntry>();

        // HR / Payroll
        public DbSet<Department> Departments => Set<Department>();
        public DbSet<Employee> Employees => Set<Employee>();
        public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
        public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
        public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

        // Supply Chain
        public DbSet<Vendor> Vendors => Set<Vendor>();
        public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
        public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
        public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
        public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();

        // CRM & Sales
        public DbSet<CrmLead> CrmLeads => Set<CrmLead>();
        public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
        public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

        // Manufacturing
        public DbSet<BomHeader> BomHeaders => Set<BomHeader>();
        public DbSet<BomLine> BomLines => Set<BomLine>();
        public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

        // Projects
        public DbSet<Project> Projects => Set<Project>();
        public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
        public DbSet<Timesheet> Timesheets => Set<Timesheet>();

        // FDMS fiscalisation
        public DbSet<FdmsSubmission> FdmsSubmissions => Set<FdmsSubmission>();

        // Group / Inter-Company / Consolidation
        public DbSet<InterCompanyRule> InterCompanyRules => Set<InterCompanyRule>();
        public DbSet<IntercompanyTransaction> IntercompanyTransactions => Set<IntercompanyTransaction>();
        public DbSet<NettingBatch> NettingBatches => Set<NettingBatch>();

        // Controls: visible exception queue (failed inter-company mirrors, etc.)
        public DbSet<ExceptionItem> ExceptionItems => Set<ExceptionItem>();

        // ---- Controls, automation and integrations (APEXERP spec) ----
        public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
        public DbSet<CompanySetting> CompanySettings => Set<CompanySetting>();
        public DbSet<SoDConflictRule> SoDConflictRules => Set<SoDConflictRule>();
        public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();
        public DbSet<ApprovalMatrixTier> ApprovalMatrixTiers => Set<ApprovalMatrixTier>();
        public DbSet<MaterialityConfiguration> MaterialityConfigurations => Set<MaterialityConfiguration>();
        public DbSet<ControlException> ControlExceptions => Set<ControlException>();
        public DbSet<AccountingPolicyVersion> AccountingPolicyVersions => Set<AccountingPolicyVersion>();
        public DbSet<AccrualSchedule> AccrualSchedules => Set<AccrualSchedule>();
        public DbSet<PrepaymentSchedule> PrepaymentSchedules => Set<PrepaymentSchedule>();
        public DbSet<PrepaymentScheduleLine> PrepaymentScheduleLines => Set<PrepaymentScheduleLine>();
        public DbSet<RevenueContract> RevenueContracts => Set<RevenueContract>();
        public DbSet<PerformanceObligation> PerformanceObligations => Set<PerformanceObligation>();
        public DbSet<RevenueScheduleLine> RevenueScheduleLines => Set<RevenueScheduleLine>();
        public DbSet<ImpairmentAssessment> ImpairmentAssessments => Set<ImpairmentAssessment>();
        public DbSet<InventoryNrvAssessment> InventoryNrvAssessments => Set<InventoryNrvAssessment>();
        public DbSet<EclLossRate> EclLossRates => Set<EclLossRate>();
        public DbSet<EclProvisionRun> EclProvisionRuns => Set<EclProvisionRun>();
        public DbSet<CreditLimitOverride> CreditLimitOverrides => Set<CreditLimitOverride>();
        public DbSet<ArCreditNote> ArCreditNotes => Set<ArCreditNote>();
        public DbSet<ApPaymentRun> ApPaymentRuns => Set<ApPaymentRun>();
        public DbSet<ApPaymentRunItem> ApPaymentRunItems => Set<ApPaymentRunItem>();
        public DbSet<SupplierBankChange> SupplierBankChanges => Set<SupplierBankChange>();
        public DbSet<BankReconciliation> BankReconciliations => Set<BankReconciliation>();
        public DbSet<PettyCashFund> PettyCashFunds => Set<PettyCashFund>();
        public DbSet<PettyCashVoucher> PettyCashVouchers => Set<PettyCashVoucher>();
        public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
        public DbSet<PeriodCloseTask> PeriodCloseTasks => Set<PeriodCloseTask>();
        public DbSet<AuditEvidenceReference> AuditEvidenceReferences => Set<AuditEvidenceReference>();
        public DbSet<ControlTestResult> ControlTestResults => Set<ControlTestResult>();
        public DbSet<JobExecution> JobExecutions => Set<JobExecution>();
        public DbSet<StatutoryRate> StatutoryRates => Set<StatutoryRate>();
        public DbSet<IntegrationEndpoint> IntegrationEndpoints => Set<IntegrationEndpoint>();
        public DbSet<IntegrationMessage> IntegrationMessages => Set<IntegrationMessage>();

        // Public Sector & Government
        public DbSet<FundCentre> FundCentres => Set<FundCentre>();
        public DbSet<Commitment> Commitments => Set<Commitment>();
        public DbSet<TenderNotice> TenderNotices => Set<TenderNotice>();
        public DbSet<TenderBid> TenderBids => Set<TenderBid>();
        public DbSet<AssetVerificationBatch> AssetVerificationBatches => Set<AssetVerificationBatch>();
        public DbSet<AssetVerificationScan> AssetVerificationScans => Set<AssetVerificationScan>();
        public DbSet<AssetRevaluation> AssetRevaluations => Set<AssetRevaluation>();

        // Grants, Donors & NGO
        public DbSet<Grant> Grants => Set<Grant>();
        public DbSet<GrantBudgetLine> GrantBudgetLines => Set<GrantBudgetLine>();
        public DbSet<SubAward> SubAwards => Set<SubAward>();
        public DbSet<SubAwardDisbursement> SubAwardDisbursements => Set<SubAwardDisbursement>();
        public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
        public DbSet<Distribution> Distributions => Set<Distribution>();
        public DbSet<GrantCloseoutChecklist> GrantCloseoutChecklists => Set<GrantCloseoutChecklist>();

        // Enterprise platform: tenant operations and platform control-plane records.
        public DbSet<OrganisationalBranch> OrganisationalBranches => Set<OrganisationalBranch>();
        public DbSet<CompanyMembership> CompanyMemberships => Set<CompanyMembership>();
        public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
        public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
        public DbSet<JurisdictionProfile> JurisdictionProfiles => Set<JurisdictionProfile>();
        public DbSet<TenantJurisdiction> TenantJurisdictions => Set<TenantJurisdiction>();
        public DbSet<DeploymentProfile> DeploymentProfiles => Set<DeploymentProfile>();
        public DbSet<TenantOnboarding> TenantOnboardings => Set<TenantOnboarding>();
        public DbSet<TenantAdministratorInvitation> TenantAdministratorInvitations => Set<TenantAdministratorInvitation>();
        public DbSet<CompanyUserInvitation> CompanyUserInvitations => Set<CompanyUserInvitation>();
        public DbSet<PlatformAuditEvent> PlatformAuditEvents => Set<PlatformAuditEvent>();
        public DbSet<TenantSupportGrant> TenantSupportGrants => Set<TenantSupportGrant>();
        public DbSet<TenantOperationalCheck> TenantOperationalChecks => Set<TenantOperationalCheck>();
        public DbSet<TenantLifecycleJob> TenantLifecycleJobs => Set<TenantLifecycleJob>();
        public DbSet<TenantSlaPolicy> TenantSlaPolicies => Set<TenantSlaPolicy>();
        public DbSet<TenantSlaIncident> TenantSlaIncidents => Set<TenantSlaIncident>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Customer organisation (tenant) owns one or more legal entities (companies).
            // Restrict delete so tenant removal cannot cascade into accounting records.
            modelBuilder.Entity<CustomerOrganisation>()
                .HasMany(t => t.Companies)
                .WithOne(c => c.Tenant)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CustomerOrganisation>()
                .HasIndex(t => t.RegistrationNumber);
            modelBuilder.Entity<Company>()
                .HasIndex(c => c.TenantId);
            modelBuilder.Entity<CustomerOrganisation>()
                .HasQueryFilter(t => CurrentTenantId != null && t.Id == CurrentTenantId);
            modelBuilder.Entity<Company>()
                .HasQueryFilter(c => CurrentTenantId != null && c.TenantId == CurrentTenantId);

            // Enterprise platform constraints. Platform catalogue/audit rows are deliberately
            // outside tenant query filters and must only be exposed through platform-admin policies.
            modelBuilder.Entity<OrganisationalBranch>().HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            modelBuilder.Entity<Department>().HasIndex(x => new { x.CompanyId, x.BranchId });
            modelBuilder.Entity<CostCentre>().HasIndex(x => new { x.CompanyId, x.BranchId });
            modelBuilder.Entity<CompanyMembership>().HasIndex(x => new { x.CompanyId, x.UserId }).IsUnique();
            modelBuilder.Entity<CompanyMembership>().HasIndex(x => new { x.UserId, x.IsActive });
            modelBuilder.Entity<SubscriptionPlan>().HasIndex(x => x.Code).IsUnique();
            modelBuilder.Entity<TenantSubscription>().HasIndex(x => new { x.TenantId, x.Status });
            modelBuilder.Entity<DeploymentProfile>().HasIndex(x => x.Code).IsUnique();
            modelBuilder.Entity<JurisdictionProfile>().HasIndex(x => x.CountryCode).IsUnique();
            modelBuilder.Entity<TenantJurisdiction>().HasIndex(x => new { x.TenantId, x.CompanyId, x.CountryCode }).IsUnique();
            modelBuilder.Entity<TenantOnboarding>().HasIndex(x => x.TenantId).IsUnique();
            modelBuilder.Entity<TenantAdministratorInvitation>().HasIndex(x => x.TokenHash).IsUnique();
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => x.TokenHash).IsUnique();
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => new { x.TenantId, x.Email, x.AcceptedAt });
            modelBuilder.Entity<TenantSupportGrant>().HasIndex(x => new { x.TenantId, x.Status, x.ExpiresAt });
            modelBuilder.Entity<TenantOperationalCheck>().HasIndex(x => new { x.TenantId, x.CheckCode, x.CheckedAt });
            modelBuilder.Entity<TenantLifecycleJob>().HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            modelBuilder.Entity<TenantSlaPolicy>().HasIndex(x => x.TenantId).IsUnique();
            modelBuilder.Entity<TenantSlaIncident>().HasIndex(x => new { x.TenantId, x.IncidentNumber }).IsUnique();
            modelBuilder.Entity<TenantSlaIncident>().HasIndex(x => new { x.TenantId, x.Status, x.OpenedAt });
            // FK indexes are explicit so the migration, snapshot and production query plan remain aligned.
            modelBuilder.Entity<OrganisationalBranch>().HasIndex(x => x.ParentBranchId);
            modelBuilder.Entity<CompanyMembership>().HasIndex(x => x.RoleId);
            modelBuilder.Entity<TenantSubscription>().HasIndex(x => x.PlanId);
            modelBuilder.Entity<TenantJurisdiction>().HasIndex(x => x.CompanyId);
            modelBuilder.Entity<TenantJurisdiction>().HasIndex(x => x.CountryCode);
            modelBuilder.Entity<TenantOnboarding>().HasIndex(x => x.DeploymentProfileId);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasIndex(x => x.AcceptedUserId);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasIndex(x => x.CompanyId);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasIndex(x => x.TenantId);
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => x.AcceptedUserId);
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => x.CompanyId);
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => x.InvitedBy);
            modelBuilder.Entity<CompanyUserInvitation>().HasIndex(x => x.RoleId);
            modelBuilder.Entity<Department>().HasIndex(x => x.BranchId);
            modelBuilder.Entity<CostCentre>().HasIndex(x => x.BranchId);
            modelBuilder.Entity<TenantSlaPolicy>().Property(x => x.BusinessHoursJson).HasColumnType("jsonb");
            modelBuilder.Entity<PlatformAuditEvent>().HasIndex(x => new { x.TenantId, x.CreatedAt });
            modelBuilder.Entity<OrganisationalBranch>().Property(x => x.Code).HasMaxLength(40);
            modelBuilder.Entity<SubscriptionPlan>().Property(x => x.ModuleCodesJson).HasColumnType("jsonb");
            modelBuilder.Entity<DeploymentProfile>().Property(x => x.ConfigurationJson).HasColumnType("jsonb");
            modelBuilder.Entity<TenantJurisdiction>().Property(x => x.TaxConfigurationJson).HasColumnType("jsonb");
            modelBuilder.Entity<TenantOnboarding>().Property(x => x.CompletedStepsJson).HasColumnType("jsonb");
            modelBuilder.Entity<PlatformAuditEvent>().Property(x => x.DetailsJson).HasColumnType("jsonb");
            modelBuilder.Entity<TenantOperationalCheck>().Property(x => x.DetailsJson).HasColumnType("jsonb");

            // Explicitly model every new FK created by the enterprise platform migration.
            // Restrict prevents tenant/company/user deletion from cascading into audit or access records.
            modelBuilder.Entity<OrganisationalBranch>().HasOne<Company>().WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<OrganisationalBranch>().HasOne<OrganisationalBranch>().WithMany()
                .HasForeignKey(x => x.ParentBranchId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyMembership>().HasOne<Company>().WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyMembership>().HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyMembership>().HasOne<Role>().WithMany()
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantSubscription>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantSubscription>().HasOne<SubscriptionPlan>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantJurisdiction>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantJurisdiction>().HasOne<Company>().WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantJurisdiction>().HasOne<JurisdictionProfile>().WithMany()
                .HasForeignKey(x => x.CountryCode).HasPrincipalKey(x => x.CountryCode).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantOnboarding>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantOnboarding>().HasOne<DeploymentProfile>().WithMany()
                .HasForeignKey(x => x.DeploymentProfileId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasOne<Company>().WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantAdministratorInvitation>().HasOne<User>().WithMany()
                .HasForeignKey(x => x.AcceptedUserId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyUserInvitation>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyUserInvitation>().HasOne<Company>().WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyUserInvitation>().HasOne<Role>().WithMany()
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyUserInvitation>().HasOne<User>().WithMany()
                .HasForeignKey(x => x.InvitedBy).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CompanyUserInvitation>().HasOne<User>().WithMany()
                .HasForeignKey(x => x.AcceptedUserId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<Department>().HasOne<OrganisationalBranch>().WithMany()
                .HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CostCentre>().HasOne<OrganisationalBranch>().WithMany()
                .HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantSlaPolicy>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<TenantSlaIncident>().HasOne<CustomerOrganisation>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

            // ---- Decimal precision: NUMERIC(20,6) everywhere per PRD Section 5 ----
            // ---- Dates: every DateTime is stored/read as UTC. Npgsql refuses to write a DateTime whose
            //      Kind is Unspecified (e.g. "2026-10-31" bound from JSON, or new DateTime(y, m, 1)) to a
            //      `timestamp with time zone` column, which would otherwise fail the first posting of a month.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                    {
                        property.SetColumnType("numeric(20,6)");
                    }
                    else if (property.ClrType == typeof(DateTime))
                    {
                        property.SetValueConverter(UtcConverter);
                    }
                    else if (property.ClrType == typeof(DateTime?))
                    {
                        property.SetValueConverter(NullableUtcConverter);
                    }
                }
            }

            // ---- Unique/business indexes ----
            modelBuilder.Entity<User>().HasIndex(u => new { u.CompanyId, u.Email }).IsUnique();
            modelBuilder.Entity<GlAccount>().HasIndex(a => new { a.CompanyId, a.Code }).IsUnique();
            modelBuilder.Entity<Customer>().HasIndex(c => new { c.CompanyId, c.Code }).IsUnique();
            modelBuilder.Entity<Supplier>().HasIndex(s => new { s.CompanyId, s.Code }).IsUnique();
            modelBuilder.Entity<InventoryItem>().HasIndex(i => new { i.CompanyId, i.Sku }).IsUnique();
            modelBuilder.Entity<Employee>().HasIndex(e => new { e.CompanyId, e.EmployeeNo }).IsUnique();
            modelBuilder.Entity<ArInvoice>().HasIndex(i => new { i.CompanyId, i.InvoiceNo }).IsUnique();
            modelBuilder.Entity<ApInvoice>().HasIndex(i => new { i.CompanyId, i.InvoiceNo }).IsUnique();
            modelBuilder.Entity<JournalEntry>().HasIndex(j => new { j.CompanyId, j.EntryNo }).IsUnique();

            // ---- Integrity safeguards (repository audit findings 4, 5, 8; design doc "essential database safeguards") ----
            // A journal can be reversed at most once: no two entries may reverse the same original
            // (PostgreSQL permits many NULLs in a unique index, so ordinary entries are unaffected).
            modelBuilder.Entity<JournalEntry>().HasIndex(j => j.ReversalOfId).IsUnique();
            // One accounting period per company per month (guards concurrent first-posting races).
            modelBuilder.Entity<AccountingPeriod>().HasIndex(p => new { p.CompanyId, p.Year, p.PeriodNo }).IsUnique();
            // An AR invoice is mirrored into the counterparty's books at most once.
            modelBuilder.Entity<IntercompanyTransaction>().HasIndex(t => t.SourceArInvoiceId).IsUnique();
            modelBuilder.Entity<ExceptionItem>().HasIndex(e => new { e.CompanyId, e.Status, e.Category });
            // Ledger lines: non-negative amounts, one side only, strictly positive FX rate.
            modelBuilder.Entity<JournalLine>().ToTable(t =>
            {
                t.HasCheckConstraint("CK_JournalLines_Amounts",
                    "\"Debit\" >= 0 AND \"Credit\" >= 0 AND NOT (\"Debit\" > 0 AND \"Credit\" > 0)");
                t.HasCheckConstraint("CK_JournalLines_ExchangeRate", "\"ExchangeRate\" > 0");
            });

            // ---- Relationships (delete behaviour Restrict to protect financial history) ----
            modelBuilder.Entity<JournalEntry>()
                .HasMany(j => j.Lines)
                .WithOne(l => l.JournalEntry!)
                .HasForeignKey(l => l.JournalEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ArInvoice>()
                .HasMany(i => i.Lines)
                .WithOne()
                .HasForeignKey(l => l.ArInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ApInvoice>()
                .HasMany(i => i.Lines)
                .WithOne()
                .HasForeignKey(l => l.ApInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<BomHeader>()
                .HasMany(b => b.Lines)
                .WithOne()
                .HasForeignKey(l => l.BomHeaderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PurchaseOrder>()
                .HasMany(p => p.Lines)
                .WithOne()
                .HasForeignKey(l => l.PurchaseOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SalesOrder>()
                .HasMany(o => o.Lines)
                .WithOne()
                .HasForeignKey(l => l.SalesOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PayrollRun>()
                .HasMany(p => p.Lines)
                .WithOne()
                .HasForeignKey(l => l.PayrollRunId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Project>()
                .HasMany(p => p.Tasks)
                .WithOne()
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // ---- Global tenant-isolation query filters (mirrors PostgreSQL RLS) ----
            ApplyTenantFilter<OrganisationalBranch>(modelBuilder);
            ApplyTenantFilter<CompanyMembership>(modelBuilder);
            ApplyTenantFilter<Role>(modelBuilder);
            modelBuilder.Entity<User>().HasQueryFilter(u => CurrentCompanyId != null &&
                (u.CompanyId == CurrentCompanyId || (CurrentUserId != null && CompanyMemberships.Any(m =>
                    m.UserId == u.Id && m.CompanyId == CurrentCompanyId && m.IsActive &&
                    (!m.ValidFrom.HasValue || m.ValidFrom <= DateTime.UtcNow) &&
                    (!m.ValidTo.HasValue || m.ValidTo > DateTime.UtcNow)))));
            ApplyTenantFilter<AuditLogEntry>(modelBuilder);
            ApplyTenantFilter<WorkflowRequest>(modelBuilder);
            ApplyTenantFilter<GlAccount>(modelBuilder);
            ApplyTenantFilter<AccountingPeriod>(modelBuilder);
            ApplyTenantFilter<CostCentre>(modelBuilder);
            ApplyTenantFilter<StatisticalAccount>(modelBuilder);
            ApplyTenantFilter<JournalEntry>(modelBuilder);
            ApplyTenantFilter<ExchangeRate>(modelBuilder);
            ApplyTenantFilter<TaxCode>(modelBuilder);
            ApplyTenantFilter<PayeTaxBand>(modelBuilder);
            ApplyTenantFilter<Customer>(modelBuilder);
            ApplyTenantFilter<ArInvoice>(modelBuilder);
            ApplyTenantFilter<ArReceipt>(modelBuilder);
            ApplyTenantFilter<Supplier>(modelBuilder);
            ApplyTenantFilter<ApInvoice>(modelBuilder);
            ApplyTenantFilter<ApPayment>(modelBuilder);
            ApplyTenantFilter<BankAccount>(modelBuilder);
            ApplyTenantFilter<BankTransaction>(modelBuilder);
            ApplyTenantFilter<Warehouse>(modelBuilder);
            ApplyTenantFilter<InventoryItem>(modelBuilder);
            ApplyTenantFilter<InventoryCostLayer>(modelBuilder);
            ApplyTenantFilter<InventoryMovement>(modelBuilder);
            ApplyTenantFilter<AssetCategory>(modelBuilder);
            ApplyTenantFilter<FixedAsset>(modelBuilder);
            ApplyTenantFilter<DepreciationEntry>(modelBuilder);
            ApplyTenantFilter<Department>(modelBuilder);
            ApplyTenantFilter<Employee>(modelBuilder);
            ApplyTenantFilter<PayrollRun>(modelBuilder);
            ApplyTenantFilter<LeaveRequest>(modelBuilder);
            ApplyTenantFilter<Vendor>(modelBuilder);
            ApplyTenantFilter<PurchaseOrder>(modelBuilder);
            ApplyTenantFilter<GoodsReceipt>(modelBuilder);
            ApplyTenantFilter<CrmLead>(modelBuilder);
            ApplyTenantFilter<SalesOrder>(modelBuilder);
            ApplyTenantFilter<BomHeader>(modelBuilder);
            ApplyTenantFilter<WorkOrder>(modelBuilder);
            ApplyTenantFilter<Project>(modelBuilder);
            ApplyTenantFilter<ProjectTask>(modelBuilder);
            ApplyTenantFilter<Timesheet>(modelBuilder);
            ApplyTenantFilter<Segment>(modelBuilder);
            ApplyTenantFilter<DividendDeclaration>(modelBuilder);
            ApplyTenantFilter<FinancialStatementNote>(modelBuilder);
            ApplyTenantFilter<ApprovalChain>(modelBuilder);
            ApplyTenantFilter<FdmsSubmission>(modelBuilder);
            ApplyTenantFilter<InterCompanyRule>(modelBuilder);
            ApplyTenantFilter<IntercompanyTransaction>(modelBuilder);
            ApplyTenantFilter<NettingBatch>(modelBuilder);
            ApplyTenantFilter<FundCentre>(modelBuilder);
            ApplyTenantFilter<Commitment>(modelBuilder);
            ApplyTenantFilter<TenderNotice>(modelBuilder);
            ApplyTenantFilter<AssetVerificationBatch>(modelBuilder);
            ApplyTenantFilter<AssetRevaluation>(modelBuilder);
            ApplyTenantFilter<Grant>(modelBuilder);
            ApplyTenantFilter<SubAward>(modelBuilder);
            ApplyTenantFilter<Beneficiary>(modelBuilder);
            ApplyTenantFilter<Distribution>(modelBuilder);
            ApplyTenantFilter<GrantCloseoutChecklist>(modelBuilder);
            ApplyTenantFilter<TenderBid>(modelBuilder);
            ApplyTenantFilter<ExceptionItem>(modelBuilder);
            ApplyTenantFilter<IdempotencyRecord>(modelBuilder);
            ApplyTenantFilter<RefreshToken>(modelBuilder);
            ApplyTenantFilter<MfaRecoveryCode>(modelBuilder);
            ApplyTenantFilter<CompanySetting>(modelBuilder);
            ApplyTenantFilter<SoDConflictRule>(modelBuilder);
            ApplyTenantFilter<ApprovalDelegation>(modelBuilder);
            ApplyTenantFilter<ApprovalMatrixTier>(modelBuilder);
            ApplyTenantFilter<MaterialityConfiguration>(modelBuilder);
            ApplyTenantFilter<ControlException>(modelBuilder);
            ApplyTenantFilter<AccountingPolicyVersion>(modelBuilder);
            ApplyTenantFilter<AccrualSchedule>(modelBuilder);
            ApplyTenantFilter<PrepaymentSchedule>(modelBuilder);
            ApplyTenantFilter<RevenueContract>(modelBuilder);
            ApplyTenantFilter<ImpairmentAssessment>(modelBuilder);
            ApplyTenantFilter<InventoryNrvAssessment>(modelBuilder);
            ApplyTenantFilter<EclLossRate>(modelBuilder);
            ApplyTenantFilter<EclProvisionRun>(modelBuilder);
            ApplyTenantFilter<CreditLimitOverride>(modelBuilder);
            ApplyTenantFilter<ArCreditNote>(modelBuilder);
            ApplyTenantFilter<ApPaymentRun>(modelBuilder);
            ApplyTenantFilter<SupplierBankChange>(modelBuilder);
            ApplyTenantFilter<BankReconciliation>(modelBuilder);
            ApplyTenantFilter<PettyCashFund>(modelBuilder);
            ApplyTenantFilter<PettyCashVoucher>(modelBuilder);
            ApplyTenantFilter<ReconciliationRun>(modelBuilder);
            ApplyTenantFilter<PeriodCloseTask>(modelBuilder);
            ApplyTenantFilter<AuditEvidenceReference>(modelBuilder);
            ApplyTenantFilter<ControlTestResult>(modelBuilder);
            ApplyTenantFilter<JobExecution>(modelBuilder);
            ApplyTenantFilter<StatutoryRate>(modelBuilder);
            ApplyTenantFilter<IntegrationEndpoint>(modelBuilder);
            ApplyTenantFilter<IntegrationMessage>(modelBuilder);

            // ---- Controls & automation: relationships and integrity indexes ----
            // Existing rows must keep working after the migration: accounts stay postable, existing invoices need no match.
            // (EF Core 8 sends an explicit false even when the column default is true.)
            modelBuilder.Entity<GlAccount>().Property(a => a.IsPostable).HasDefaultValue(true);
            modelBuilder.Entity<ApInvoice>().Property(i => i.MatchStatus).HasDefaultValue("NotRequired");
            modelBuilder.Entity<IdempotencyRecord>().HasIndex(r => new { r.CompanyId, r.Scope, r.Key }).IsUnique();
            modelBuilder.Entity<RefreshToken>().HasIndex(r => r.TokenHash).IsUnique();
            modelBuilder.Entity<RefreshToken>().HasIndex(r => new { r.UserId, r.FamilyId });
            modelBuilder.Entity<MfaRecoveryCode>().HasIndex(r => new { r.UserId, r.CodeHash });
            modelBuilder.Entity<CompanySetting>().HasIndex(r => new { r.CompanyId, r.Key }).IsUnique();
            modelBuilder.Entity<ControlException>().HasIndex(r => new { r.CompanyId, r.Fingerprint }).IsUnique();
            modelBuilder.Entity<ControlException>().HasIndex(r => new { r.CompanyId, r.Status, r.Severity });
            modelBuilder.Entity<MaterialityConfiguration>().HasIndex(r => new { r.CompanyId, r.Version }).IsUnique();
            modelBuilder.Entity<AccountingPolicyVersion>().HasIndex(r => new { r.CompanyId, r.Area, r.Version }).IsUnique();
            modelBuilder.Entity<ApprovalMatrixTier>().HasIndex(r => new { r.CompanyId, r.EntityType, r.UpToAmount });
            modelBuilder.Entity<ApprovalDelegation>().HasIndex(r => new { r.CompanyId, r.DelegateUserId, r.ValidTo });
            modelBuilder.Entity<JobExecution>().HasIndex(r => new { r.CompanyId, r.JobCode, r.RunKey }).IsUnique();
            modelBuilder.Entity<IntegrationMessage>().HasIndex(r => new { r.CompanyId, r.Provider, r.ExternalRef }).IsUnique();
            modelBuilder.Entity<IntegrationMessage>().HasIndex(r => new { r.Status, r.NextAttemptAt });
            modelBuilder.Entity<RevenueContract>().HasIndex(r => new { r.CompanyId, r.ContractNo }).IsUnique();
            modelBuilder.Entity<ArCreditNote>().HasIndex(r => new { r.CompanyId, r.CreditNoteNo }).IsUnique();
            modelBuilder.Entity<ApPaymentRun>().HasIndex(r => new { r.CompanyId, r.RunNo }).IsUnique();
            modelBuilder.Entity<ReconciliationRun>().HasIndex(r => new { r.CompanyId, r.Area, r.AsOfDate });
            modelBuilder.Entity<PeriodCloseTask>().HasIndex(r => new { r.CompanyId, r.PeriodId, r.Code }).IsUnique();
            modelBuilder.Entity<AuditEvidenceReference>().HasIndex(r => new { r.CompanyId, r.EntityType, r.EntityId });
            modelBuilder.Entity<StatutoryRate>().HasIndex(r => new { r.CompanyId, r.Code, r.EffectiveFrom });
            modelBuilder.Entity<EclLossRate>().HasIndex(r => new { r.CompanyId, r.FromDays });
            // Duplicate supplier invoices are refused by the database too (cancelled invoices free the number).
            modelBuilder.Entity<ApInvoice>().HasIndex(i => new { i.CompanyId, i.SupplierId, i.SupplierInvoiceNo })
                .IsUnique().HasFilter("\"SupplierInvoiceNo\" IS NOT NULL AND \"Status\" <> 4");

            modelBuilder.Entity<PrepaymentSchedule>().HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PrepaymentScheduleId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<RevenueContract>().HasMany(c => c.Obligations).WithOne().HasForeignKey(o => o.RevenueContractId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PerformanceObligation>().HasMany(o => o.Schedule).WithOne().HasForeignKey(l => l.PerformanceObligationId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<ApPaymentRun>().HasMany(r => r.Items).WithOne().HasForeignKey(i => i.ApPaymentRunId).OnDelete(DeleteBehavior.Cascade);

            // ---- Additional relationships for the expanded modules ----
            modelBuilder.Entity<WorkflowRequest>()
                .HasMany(w => w.Approvals)
                .WithOne()
                .HasForeignKey(a => a.WorkflowRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ApprovalChain>()
                .HasMany(c => c.Tiers)
                .WithOne()
                .HasForeignKey(t => t.ApprovalChainId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TenderNotice>()
                .HasMany(t => t.Bids)
                .WithOne()
                .HasForeignKey(b => b.TenderNoticeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AssetVerificationBatch>()
                .HasMany(b => b.Scans)
                .WithOne()
                .HasForeignKey(s => s.AssetVerificationBatchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Grant>()
                .HasMany(g => g.BudgetLines)
                .WithOne()
                .HasForeignKey(l => l.GrantId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique business keys for the new modules
            modelBuilder.Entity<FundCentre>().HasIndex(f => new { f.CompanyId, f.Code, f.FinancialYear }).IsUnique();
            modelBuilder.Entity<Grant>().HasIndex(g => new { g.CompanyId, g.GrantNo }).IsUnique();
            modelBuilder.Entity<TenderNotice>().HasIndex(t => new { t.CompanyId, t.TenderNo }).IsUnique();
            modelBuilder.Entity<Segment>().HasIndex(s => new { s.CompanyId, s.Code }).IsUnique();
        }

        /// <summary>Fail-closed: no tenant context => no rows. See the class remarks.</summary>
        private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantEntity
        {
            modelBuilder.Entity<T>().HasQueryFilter(e => CurrentCompanyId != null && e.CompanyId == CurrentCompanyId);
        }

        // ---------------------------------------------------------------------------------------------
        // UTC normalisation
        // ---------------------------------------------------------------------------------------------
        private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
            new(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
            new(v => v.HasValue ? ToUtc(v.Value) : v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        /// <summary>Unspecified is taken as UTC (calendar date preserved); Local is converted.</summary>
        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        // ---------------------------------------------------------------------------------------------
        // SaveChanges: audit stamping + ledger immutability
        // ---------------------------------------------------------------------------------------------
        public override int SaveChanges()
        {
            StampAudit();
            EnforceLedgerImmutability();
            return base.SaveChanges();
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            StampAudit();
            EnforceLedgerImmutability();   // a violation surfaces through the returned Task, like any other save failure
            return await base.SaveChangesAsync(cancellationToken);
        }

        private void StampAudit()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            {
                if (entry.State == EntityState.Added && entry.Entity.CreatedBy == null && CurrentUserId != null)
                {
                    entry.Entity.CreatedBy = CurrentUserId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedAt = now;
                    if (CurrentUserId != null) entry.Entity.UpdatedBy = CurrentUserId;
                }
            }
        }

        private static readonly HashSet<string> PostedJournalMutableProperties =
            new() { nameof(JournalEntry.Status), nameof(BaseEntity.UpdatedAt), nameof(BaseEntity.UpdatedBy) };

        /// <summary>
        /// Posted history is append-only: journal lines never change once written, a posted journal may only
        /// move Posted -> Reversed (nothing else on it may be edited or deleted), and audit-log rows are never
        /// modified or removed. Errors in posted entries are corrected by reversal, not by editing
        /// (design document: "no silent edits to posted records").
        /// </summary>
        private void EnforceLedgerImmutability()
        {
            foreach (var entry in ChangeTracker.Entries<JournalLine>())
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("Journal lines are immutable. Correct a posted entry by reversing it.");
            }

            foreach (var entry in ChangeTracker.Entries<AuditLogEntry>())
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("The audit log is append-only and cannot be changed or deleted.");
            }

            foreach (var entry in ChangeTracker.Entries<JournalEntry>())
            {
                if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;

                var original = entry.Property(j => j.Status).OriginalValue;
                if (original is not (JournalStatus.Posted or JournalStatus.Reversed)) continue;

                if (entry.State == EntityState.Deleted)
                    throw new InvalidOperationException("A posted journal entry cannot be deleted. Reverse it instead.");

                var changed = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
                if (changed.Any(name => !PostedJournalMutableProperties.Contains(name)))
                    throw new InvalidOperationException("A posted journal entry cannot be edited. Reverse it and post a corrected entry.");

                var current = entry.Property(j => j.Status).CurrentValue;
                var statusChanged = current != original;
                var allowed = !statusChanged || (original == JournalStatus.Posted && current == JournalStatus.Reversed);
                if (!allowed)
                    throw new InvalidOperationException($"A journal entry cannot move from {original} to {current}.");
            }
        }
    }
}
