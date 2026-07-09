using System;
using ApexERP.Domain.Common;

namespace ApexERP.Domain.Entities
{
    public enum FdmsSubmissionStatus
    {
        Queued,
        Submitted,
        Acknowledged,
        Failed
    }

    /// <summary>
    /// One fiscalised invoice submission to ZIMRA's FDMS. The signing mechanics here (NIST P-256
    /// ECDSA, 16-char MD5 QR digest) are real and functional. What's necessarily a placeholder is
    /// the actual device certificate/private key, which ZIMRA issues per-device on physical/virtual
    /// fiscal device registration -- that's a real external certification process, not something
    /// that can be fabricated. This entity and FdmsService are written so a real device key can be
    /// dropped in via configuration without changing any other code.
    /// </summary>
    public class FdmsSubmission : TenantEntity
    {
        public Guid ArInvoiceId { get; set; }
        public string InvoiceHash { get; set; } = string.Empty;
        /// <summary>Base64 ECDSA P-256 signature over the canonical invoice payload.</summary>
        public string Signature { get; set; } = string.Empty;
        /// <summary>16-character MD5 digest of the signature, embedded in the printed/displayed QR code.</summary>
        public string QrCode { get; set; } = string.Empty;
        public FdmsSubmissionStatus Status { get; set; } = FdmsSubmissionStatus.Queued;
        public int RetryCount { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
        public DateTime? AcknowledgedAt { get; set; }
        /// <summary>Fiscal day/global invoice number returned by ZIMRA once genuinely submitted.</summary>
        public string? ZimraReceiptId { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
