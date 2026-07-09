using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    /// <summary>Configured rule for automatic mirror posting between two group entities
    /// (PRD Module 2: "As a Group Accountant, I want inter-company rules...").</summary>
    public class InterCompanyRule : TenantEntity
    {
        public Guid FromCompanyId { get; set; }
        public Guid ToCompanyId { get; set; }
        public bool AutoMirror { get; set; } = true;
        public Guid DefaultSenderArAccountId { get; set; }
        public Guid DefaultReceiverApAccountId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>Links an originating transaction in one group entity to its mirror in another,
    /// and tracks matching/netting/settlement status for the inter-company reconciliation report.</summary>
    public class IntercompanyTransaction : TenantEntity
    {
        public Guid FromCompanyId { get; set; }
        public Guid ToCompanyId { get; set; }
        public DateTime TransactionDate { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public IntercompanyStatus Status { get; set; } = IntercompanyStatus.Unmatched;

        /// <summary>The AR invoice (or other source) posted in the "From" (sending) entity.</summary>
        public Guid? SourceArInvoiceId { get; set; }
        public Guid? SourceJournalEntryId { get; set; }
        /// <summary>The mirror AP invoice auto-created in the "To" (receiving) entity.</summary>
        public Guid? MirrorApInvoiceId { get; set; }
        public Guid? MirrorJournalEntryId { get; set; }

        /// <summary>Set when this transaction was settled as part of a netting batch.</summary>
        public Guid? NettingBatchId { get; set; }
    }

    /// <summary>A single netting run: nets open inter-company AR/AP between two entities into one
    /// settlement journal per entity, posted atomically (PRD: "single DB transaction").</summary>
    public class NettingBatch : TenantEntity
    {
        public Guid CompanyAId { get; set; }
        public Guid CompanyBId { get; set; }
        public DateTime BatchDate { get; set; }
        public decimal NetAmount { get; set; }
        /// <summary>Which company owes the net amount to the other (the settlement direction).</summary>
        public Guid PayerCompanyId { get; set; }
        public Guid? SettlementJournalEntryIdPayer { get; set; }
        public Guid? SettlementJournalEntryIdPayee { get; set; }
    }
}
