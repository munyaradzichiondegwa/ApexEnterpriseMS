using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Infrastructure.Data
{
    /// <summary>
    /// Central EF Core context. Multi-tenancy is enforced with global query filters keyed off
    /// CurrentCompanyId (set per-request by TenantMiddleware from the JWT's company_id claim) ---
    /// this is the application-level equivalent of the PostgreSQL RLS policies called for in the PRD.
    /// </summary>
    public class ApexErpDbContext : DbContext
    {
        /// <summary>Resolved once per request from the authenticated user's JWT claims.</summary>
        public Guid? CurrentCompanyId { get; set; }

        public ApexErpDbContext(DbContextOptions<ApexErpDbContext> options) : base(options) { }

        // Core / tenancy
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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ---- Decimal precision: NUMERIC(20,6) everywhere per PRD Section 5 ----
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                    {
                        property.SetColumnType("numeric(20,6)");
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
            ApplyTenantFilter<Role>(modelBuilder);
            ApplyTenantFilter<User>(modelBuilder);
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

        private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantEntity
        {
            modelBuilder.Entity<T>().HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        }

        public override int SaveChanges()
        {
            StampAudit();
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            StampAudit();
            return base.SaveChangesAsync(cancellationToken);
        }

        private void StampAudit()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            {
                if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedAt = now;
                }
            }
        }
    }
}
