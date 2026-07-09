using System;
using System.Collections.Generic;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public class Customer : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal CreditLimit { get; set; }
        public Guid? ArAccountId { get; set; }
        public string? VatNo { get; set; }
        public int PaymentTermsDays { get; set; } = 30;
        public bool IsActive { get; set; } = true;

        // FDMS fiscal invoicing fields (Public Notice 30/2025 -- mandatory on every fiscal invoice)
        public string? Tin { get; set; }
        public string? LegalName { get; set; }
        public string? FiscalAddress { get; set; }

        /// <summary>Group/Inter-Company (PRD Module 2): set when this "customer" is actually another
        /// company within the same group, enabling automatic mirror AP posting on invoice creation.</summary>
        public Guid? LinkedCompanyId { get; set; }
    }

    public class ArInvoice : TenantEntity
    {
        public string InvoiceNo { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public Customer? Customer { get; set; }
        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal ExchangeRate { get; set; } = 1m;
        public decimal Subtotal { get; set; }
        public decimal VatAmount { get; set; }
        public decimal Total { get; set; }
        public decimal Outstanding { get; set; }
        public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
        public Guid? JournalEntryId { get; set; }

        /// <summary>Grants & NGO (Module 15): tags this sale/reimbursement to a specific grant.</summary>
        public Guid? GrantId { get; set; }
        /// <summary>Set once FdmsSubmissionService successfully fiscalises this invoice with ZIMRA.</summary>
        public Guid? FdmsSubmissionId { get; set; }

        public ICollection<ArInvoiceLine> Lines { get; set; } = new List<ArInvoiceLine>();
    }

    public class ArInvoiceLine : BaseEntity
    {
        public Guid ArInvoiceId { get; set; }
        public string Description { get; set; } = string.Empty;
        public Guid? InventoryItemId { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; }
        public Guid? TaxCodeId { get; set; }
        public decimal LineSubtotal { get; set; }
        public decimal LineVat { get; set; }
        public decimal LineTotal { get; set; }
        public Guid GlAccountId { get; set; } // income account (required at posting time)
    }

    public class ArReceipt : TenantEntity
    {
        public string ReceiptNo { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public DateTime ReceiptDate { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal Amount { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public string? Reference { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? BankAccountId { get; set; }

        public ICollection<ArReceiptAllocation> Allocations { get; set; } = new List<ArReceiptAllocation>();
    }

    /// <summary>Applies a receipt against one or more open invoices (partial allocation supported).</summary>
    public class ArReceiptAllocation : BaseEntity
    {
        public Guid ArReceiptId { get; set; }
        public Guid ArInvoiceId { get; set; }
        public decimal AmountApplied { get; set; }
    }

    public class Supplier : TenantEntity
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string Currency { get; set; } = "USD";
        public Guid? ApAccountId { get; set; }
        public string? VatNo { get; set; }
        public int PaymentTermsDays { get; set; } = 30;
        public bool WhtApplicable { get; set; }
        public decimal WhtRate { get; set; } = 0.10m; // ZIMRA default non-compliant supplier WHT
        public bool IsActive { get; set; } = true;

        /// <summary>Group/Inter-Company (PRD Module 2): set when this "supplier" is actually another
        /// company within the same group (the counterparty of a mirror-posted intercompany invoice).</summary>
        public Guid? LinkedCompanyId { get; set; }
    }

    public class ApInvoice : TenantEntity
    {
        public string InvoiceNo { get; set; } = string.Empty;
        public Guid SupplierId { get; set; }
        public Supplier? Supplier { get; set; }
        public DateTime InvoiceDate { get; set; }
        public DateTime DueDate { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal ExchangeRate { get; set; } = 1m;
        public decimal Subtotal { get; set; }
        public decimal VatAmount { get; set; }
        public decimal WhtAmount { get; set; }
        public decimal Total { get; set; }
        public decimal Outstanding { get; set; }
        public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
        public Guid? JournalEntryId { get; set; }

        /// <summary>Public Sector (Module 16): fund centre this expenditure is committed/actualised against.</summary>
        public Guid? FundCentreId { get; set; }
        /// <summary>If FundCentreId is set and this invoice fulfils a committed PO, the matching
        /// Commitment record that gets released when this invoice posts.</summary>
        public Guid? CommitmentId { get; set; }
        /// <summary>Grants & NGO (Module 15): tags this expenditure to a specific grant for
        /// budget-vs-actual and indirect cost recovery.</summary>
        public Guid? GrantId { get; set; }
        /// <summary>True if this invoice originated as a mirror posting from another group entity's AR invoice.</summary>
        public bool IsIntercompanyMirror { get; set; }

        public ICollection<ApInvoiceLine> Lines { get; set; } = new List<ApInvoiceLine>();
    }

    public class ApInvoiceLine : BaseEntity
    {
        public Guid ApInvoiceId { get; set; }
        public string Description { get; set; } = string.Empty;
        public Guid? InventoryItemId { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal UnitCost { get; set; }
        public Guid? TaxCodeId { get; set; }
        public decimal LineSubtotal { get; set; }
        public decimal LineVat { get; set; }
        public decimal LineTotal { get; set; }
        public Guid GlAccountId { get; set; } // expense/asset account being debited
    }

    public class ApPayment : TenantEntity
    {
        public string PaymentNo { get; set; } = string.Empty;
        public Guid SupplierId { get; set; }
        public DateTime PaymentDate { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal Amount { get; set; }
        public PaymentMethod PaymentMethod { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? BankAccountId { get; set; }

        public ICollection<ApPaymentAllocation> Allocations { get; set; } = new List<ApPaymentAllocation>();
    }

    public class ApPaymentAllocation : BaseEntity
    {
        public Guid ApPaymentId { get; set; }
        public Guid ApInvoiceId { get; set; }
        public decimal AmountApplied { get; set; }
    }

    public class BankAccount : TenantEntity
    {
        public string Name { get; set; } = string.Empty;
        public string AccountNo { get; set; } = string.Empty;
        public string BankName { get; set; } = string.Empty;
        public string Currency { get; set; } = "USD";
        public Guid GlAccountId { get; set; }
        public decimal CurrentBalance { get; set; }
    }

    public class BankTransaction : TenantEntity
    {
        public Guid BankAccountId { get; set; }
        public DateTime TransactionDate { get; set; }
        public string? Description { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string? Reference { get; set; }
        public bool IsReconciled { get; set; }
        public Guid? JournalEntryId { get; set; }
        public Guid? MatchedStatementLineHash { get; set; }
    }
}
