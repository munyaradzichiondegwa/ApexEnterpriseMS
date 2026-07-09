using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public class GlAccount : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public GlAccountType Type { get; set; }
        public string? SubType { get; set; }
        public Guid? ParentId { get; set; }
        public string Currency { get; set; } = "USD";
        public bool IsActive { get; set; } = true;
        public bool IsBankAccount { get; set; }
        public bool IsControlAccount { get; set; }

        /// <summary>Running balance in functional currency, maintained by GlPostingService.</summary>
        public decimal CurrentBalance { get; set; }

        /// <summary>Classifies this account for the indirect-method Statement of Cash Flows
        /// (PRD Module 10). Set once per account at chart-of-accounts setup time.</summary>
        public CashFlowCategory CashFlowCategory { get; set; } = CashFlowCategory.NotApplicable;
        /// <summary>If this account's movements represent Other Comprehensive Income rather than
        /// P&amp;L (IAS 1), classify it here so the Income Statement/OCI split and the Statement of
        /// Changes in Equity present it correctly.</summary>
        public OciCategory OciCategory { get; set; } = OciCategory.NotApplicable;
        /// <summary>True for inter-company AR/AP/loan accounts that must be eliminated on
        /// consolidation (PRD Module 2).</summary>
        public bool IsIntercompanyAccount { get; set; }
        /// <summary>When IsIntercompanyAccount, which group Company this balance is with.</summary>
        public Guid? IntercompanyCounterpartyCompanyId { get; set; }
    }

    public class AccountingPeriod : TenantEntity
    {
        public int Year { get; set; }
        public int PeriodNo { get; set; } // 1-12
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public PeriodStatus Status { get; set; } = PeriodStatus.Open;
        public Guid? ClosedBy { get; set; }
        public DateTime? ClosedAt { get; set; }
    }

    public class CostCentre : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Optional IFRS 8 operating segment this cost centre rolls up into.</summary>
        public Guid? SegmentId { get; set; }
    }

    /// <summary>IFRS 8 operating segment (geographic or product/service line) for segment reporting.</summary>
    public class Segment : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SegmentType { get; set; } = "Geographic"; // Geographic | Product | Service
        public bool IsActive { get; set; } = true;
    }

    public class StatisticalAccount : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty; // FTEs / SqFt / Units
        public decimal CurrentValue { get; set; }
    }

    /// <summary>Journal header. Balanced (sum debit == sum credit) enforced by GlPostingService.</summary>
    public class JournalEntry : TenantEntity
    {
        public string EntryNo { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime EntryDate { get; set; }
        public Guid PeriodId { get; set; }
        public JournalSource Source { get; set; }
        public string? Reference { get; set; }
        public JournalStatus Status { get; set; } = JournalStatus.Draft;
        public Guid? ReversalOfId { get; set; }

        public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();
    }

    public class JournalLine : BaseEntity
    {
        public Guid JournalEntryId { get; set; }
        public JournalEntry? JournalEntry { get; set; }
        public Guid GlAccountId { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal ExchangeRate { get; set; } = 1m;
        public decimal BaseDebit { get; set; }
        public decimal BaseCredit { get; set; }
        public Guid? CostCentreId { get; set; }
        public string? Narrative { get; set; }

        /// <summary>Public Sector (Module 16): which Vote/Program/Sub-Program this expenditure/income
        /// line is tagged against, for real-time budget-vs-actual and appropriation control.</summary>
        public Guid? FundCentreId { get; set; }
        /// <summary>Grants & NGO (Module 15): auto-tags this line to a grant so expenditure rolls up
        /// into that grant's budget-vs-actual and donor reporting automatically.</summary>
        public Guid? GrantId { get; set; }
    }

    /// <summary>Daily exchange rates (ZIMRA official + manual override) used for FX translation.</summary>
    public class ExchangeRate : TenantEntity
    {
        public string FromCurrency { get; set; } = string.Empty;
        public string ToCurrency { get; set; } = "USD";
        public DateTime RateDate { get; set; }
        public decimal Rate { get; set; }
        public bool IsManualOverride { get; set; }
    }

    public class TaxCode : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Rate { get; set; } // e.g. 0.15 for 15% VAT
        public TaxType Type { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime EffectiveFrom { get; set; }
    }

    /// <summary>ZIMRA PAYE tax bands, stored with effective date ranges so annual budget changes
    /// require no code deployment (PRD 8.x requirement).</summary>
    public class PayeTaxBand : TenantEntity
    {
        public decimal LowerBound { get; set; }
        public decimal? UpperBound { get; set; } // null = no upper bound (top band)
        public decimal Rate { get; set; }         // marginal rate e.g. 0.40
        public decimal Deduct { get; set; }       // cumulative subtraction (standard tax-table technique)
        public string Currency { get; set; } = "USD";
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
    }

    /// <summary>A declared dividend -- feeds the Statement of Changes in Equity (reduces Retained
    /// Earnings) and the Statement of Cash Flows financing section (cash paid to shareholders).</summary>
    public class DividendDeclaration : TenantEntity
    {
        public DateTime DeclarationDate { get; set; }
        public DateTime? PaymentDate { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public bool IsPaid { get; set; }
        public Guid? DeclarationJournalEntryId { get; set; } // Dr Retained Earnings / Cr Dividends Payable
        public Guid? PaymentJournalEntryId { get; set; }     // Dr Dividends Payable / Cr Bank
    }

    /// <summary>Ad hoc structured disclosure for the Notes to the Financial Statements (e.g.
    /// contingent liabilities, commitments, subsequent events) that can't be system-derived.</summary>
    public class FinancialStatementNote : TenantEntity
    {
        public string NoteTitle { get; set; } = string.Empty;
        public string NoteBody { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public int FinancialYear { get; set; }
    }
}
