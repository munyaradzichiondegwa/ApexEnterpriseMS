using System;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities;

public enum BankStatementImportStatus { PendingAuthentication, Authenticated, Certified, Rejected }
public enum BankStatementOriginAuthenticationMethod { UnverifiedUpload, IndependentBankConfirmation, TrustedBankApi, VerifiedDigitalSignature }
public enum BankStatementImportedTransactionStatus { Imported, Matched, Excluded }

/// <summary>
/// Immutable source-file record for one bank account and statement period. The exact original
/// bytes are retained in bytea and SHA-256 is computed at receipt. A matching hash proves file
/// integrity after receipt, not bank authorship; independent origin authentication is separate.
/// </summary>
public sealed class BankStatementImportBatch : TenantEntity
{
    public Guid BankAccountId { get; set; }
    public string SourceInstitution { get; set; } = string.Empty;
    public string SourceAccountReference { get; set; } = string.Empty;
    public string StatementReference { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public int TransactionCount { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string ParserVersion { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public byte[] OriginalBytes { get; set; } = Array.Empty<byte>();
    public Guid UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public BankStatementOriginAuthenticationMethod AuthenticationMethod { get; set; } =
        BankStatementOriginAuthenticationMethod.UnverifiedUpload;
    public string? AuthenticationEvidenceReference { get; set; }
    public Guid? AuthenticationWorkflowId { get; set; }
    public Guid? AuthenticatedBy { get; set; }
    public DateTime? AuthenticatedAt { get; set; }
    public BankStatementImportStatus Status { get; set; } = BankStatementImportStatus.PendingAuthentication;
    public Guid? CompletenessCertifiedBy { get; set; }
    public DateTime? CompletenessCertifiedAt { get; set; }
}

/// <summary>
/// Parsed statement row retaining the bank's transaction identity and source line. Imported
/// amounts and provenance are immutable; reconciliation or independently reviewed exclusion
/// changes only the workflow fields.
/// </summary>
public sealed class BankStatementImportedTransaction : TenantEntity
{
    public Guid ImportBatchId { get; set; }
    public Guid BankAccountId { get; set; }
    public string ExternalTransactionId { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int SourceLineNumber { get; set; }
    public string TransactionFingerprint { get; set; } = string.Empty;
    public BankStatementImportedTransactionStatus Status { get; set; } =
        BankStatementImportedTransactionStatus.Imported;
    public Guid? MatchedBankTransactionId { get; set; }
    public Guid? MatchedBy { get; set; }
    public DateTime? MatchedAt { get; set; }
    public string? ExclusionReason { get; set; }
    public Guid? ExcludedBy { get; set; }
    public DateTime? ExcludedAt { get; set; }
}
