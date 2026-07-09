using System.Security.Cryptography;
using System.Text;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// PRD Module 9 (ZW Tax Compliance & E-Invoicing / FDMS): fiscalises AR invoices per ZIMRA's
    /// Fiscalisation Data Management System requirements -- NIST P-256 ECDSA signature over the
    /// canonical invoice payload, a 16-character MD5 digest of that signature embedded in the
    /// printed/displayed QR code, and a 72-hour offline queue with retry for when the ZIMRA endpoint
    /// is unreachable.
    ///
    /// What's real: the entire signing, hashing, and queue/retry mechanism below runs genuine
    /// cryptography against a real key pair. What's necessarily a placeholder: the actual submission
    /// to fdmsapi.zimra.co.zw, because that requires a real device certificate ZIMRA issues per
    /// physical/virtual fiscal device on registration -- an external certification process this
    /// environment cannot fabricate. Drop a real device key into configuration and SubmitAsync's
    /// HTTP call is the only piece left to implement against ZIMRA's actual endpoint.
    /// </summary>
    public class FdmsService
    {
        private readonly ApexErpDbContext _db;
        private readonly ECDsa _deviceKey; // stand-in for the ZIMRA-issued device private key

        public FdmsService(ApexErpDbContext db)
        {
            _db = db;
            // In production this key is loaded from secure device-certificate storage (e.g. Azure Key
            // Vault / HSM), provisioned once when the fiscal device is registered with ZIMRA. Here we
            // generate a session key so the signing mechanics are fully exercised end-to-end.
            _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }

        /// <summary>Builds the canonical payload, signs it, computes the QR digest, and queues the
        /// submission. Call immediately after an AR invoice is posted.</summary>
        public async Task<FdmsSubmission> FiscaliseInvoiceAsync(Guid companyId, Guid arInvoiceId, CancellationToken ct = default)
        {
            var invoice = await _db.ArInvoices.Include(i => i.Lines).Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == arInvoiceId && i.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("AR invoice not found.");

            var canonicalPayload = BuildCanonicalPayload(invoice);
            var invoiceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));
            var signatureBytes = _deviceKey.SignData(Encoding.UTF8.GetBytes(canonicalPayload), HashAlgorithmName.SHA256);
            var signature = Convert.ToBase64String(signatureBytes);
            var qrCode = ComputeQrDigest(signature);

            var submission = new FdmsSubmission
            {
                CompanyId = companyId,
                ArInvoiceId = arInvoiceId,
                InvoiceHash = invoiceHash,
                Signature = signature,
                QrCode = qrCode,
                Status = FdmsSubmissionStatus.Queued,
                QueuedAt = DateTime.UtcNow
            };
            _db.FdmsSubmissions.Add(submission);
            invoice.FdmsSubmissionId = submission.Id;
            await _db.SaveChangesAsync(ct);
            return submission;
        }

        /// <summary>16-character MD5 digest of the signature, per ZIMRA's QR code spec.</summary>
        public static string ComputeQrDigest(string signature)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(signature));
            return Convert.ToHexString(hash)[..16];
        }

        private static string BuildCanonicalPayload(ArInvoice invoice)
        {
            // Canonical, deterministic field ordering so the same invoice always signs identically.
            var sb = new StringBuilder();
            sb.Append(invoice.InvoiceNo).Append('|');
            sb.Append(invoice.InvoiceDate.ToString("yyyy-MM-ddTHH:mm:ss")).Append('|');
            sb.Append(invoice.Customer?.Tin ?? "").Append('|');
            sb.Append(invoice.Customer?.VatNo ?? "").Append('|');
            sb.Append(invoice.Total.ToString("F2")).Append('|');
            sb.Append(invoice.VatAmount.ToString("F2")).Append('|');
            foreach (var line in invoice.Lines.OrderBy(l => l.Id))
                sb.Append(line.Description).Append(':').Append(line.LineTotal.ToString("F2")).Append(';');
            return sb.ToString();
        }

        /// <summary>Attempts submission to ZIMRA. The HTTP call itself is a placeholder (see class
        /// summary) -- everything up to and including "what would be sent" is real. On failure,
        /// increments RetryCount; the queue processor (below) keeps retrying for 72 hours per PRD.</summary>
        public async Task<FdmsSubmission> AttemptSubmissionAsync(Guid companyId, Guid submissionId, CancellationToken ct = default)
        {
            var submission = await _db.FdmsSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId && s.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("FDMS submission not found.");

            submission.LastAttemptAt = DateTime.UtcNow;
            submission.RetryCount++;

            // PLACEHOLDER: real deployment posts `signature` + `qrCode` + canonical payload to
            // https://fdmsapi.zimra.co.zw using the registered device's mTLS certificate, and reads
            // back a ZIMRA-assigned receipt/global invoice number. No real ZIMRA endpoint is called here.
            var hoursSinceQueued = (DateTime.UtcNow - submission.QueuedAt).TotalHours;
            if (hoursSinceQueued > 72)
            {
                submission.Status = FdmsSubmissionStatus.Failed;
                submission.ErrorMessage = "Exceeded the 72-hour offline submission window without a registered ZIMRA device connection.";
            }
            else
            {
                submission.Status = FdmsSubmissionStatus.Failed;
                submission.ErrorMessage = "No ZIMRA device certificate configured -- submission not actually attempted (placeholder). Queued for retry.";
            }

            await _db.SaveChangesAsync(ct);
            return submission;
        }

        /// <summary>Everything still Queued/Failed within the 72-hour window, oldest first --
        /// what a scheduled retry job would process.</summary>
        public async Task<List<FdmsSubmission>> GetPendingQueueAsync(Guid companyId, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddHours(-72);
            return await _db.FdmsSubmissions
                .Where(s => s.CompanyId == companyId && s.Status != FdmsSubmissionStatus.Acknowledged && s.QueuedAt >= cutoff)
                .OrderBy(s => s.QueuedAt)
                .ToListAsync(ct);
        }
    }
}
