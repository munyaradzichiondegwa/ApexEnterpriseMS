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
        private readonly ECDsa? _deviceKey;
        private readonly IFdmsGateway _gateway;

        /// <summary>
        /// The fiscal device's private key, loaded once from the FDMS_DEVICE_KEY_PEM environment variable (PKCS#8 or SEC1 PEM, ECDSA P-256),
        /// provisioned when the device is registered with ZIMRA. It is deliberately NOT generated on the fly: a key invented per request makes
        /// every signature unverifiable, which is worse than having none. With no key configured, invoices are recorded as "not fiscalised".
        /// </summary>
        private static readonly Lazy<ECDsa?> ConfiguredKey = new(() =>
        {
            var pem = Environment.GetEnvironmentVariable("FDMS_DEVICE_KEY_PEM");
            if (string.IsNullOrWhiteSpace(pem)) return null;
            var key = ECDsa.Create();
            key.ImportFromPem(pem.Replace("\\n", "\n"));
            return key;
        });

        public static bool IsDeviceConfigured => ConfiguredKey.Value != null;

        public FdmsService(ApexErpDbContext db, IFdmsGateway? gateway = null)
        {
            _db = db;
            _deviceKey = ConfiguredKey.Value;
            _gateway = gateway ?? new NotConfiguredFdmsGateway();
        }

        /// <summary>Verifies a stored signature against the device's public key, so an auditor can prove a fiscal record was not altered.</summary>
        public bool VerifySignature(ArInvoice invoice, string signatureBase64)
        {
            if (_deviceKey == null || string.IsNullOrEmpty(signatureBase64)) return false;
            try { return _deviceKey.VerifyData(Encoding.UTF8.GetBytes(BuildCanonicalPayload(invoice)), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256); }
            catch (FormatException) { return false; }
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

            if (_deviceKey == null)
            {
                // No fiscal device key: record the invoice as NOT fiscalised rather than fabricating a signature or QR code.
                var unsigned = new FdmsSubmission
                {
                    CompanyId = companyId, ArInvoiceId = arInvoiceId, InvoiceHash = invoiceHash, Signature = string.Empty, QrCode = string.Empty,
                    Status = FdmsSubmissionStatus.Failed, QueuedAt = DateTime.UtcNow,
                    ErrorMessage = "Fiscal device key is not configured (FDMS_DEVICE_KEY_PEM). This invoice has NOT been fiscalised and carries no valid fiscal signature or QR code."
                };
                _db.FdmsSubmissions.Add(unsigned);
                invoice.FdmsSubmissionId = unsigned.Id;
                await _db.SaveChangesAsync(ct);
                return unsigned;
            }
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

            if (string.IsNullOrEmpty(submission.Signature))
            {
                submission.Status = FdmsSubmissionStatus.Failed;
                submission.ErrorMessage = "The invoice was never signed (no fiscal device key). Configure the device and re-fiscalise it.";
            }
            else if ((DateTime.UtcNow - submission.QueuedAt).TotalHours > 72)
            {
                submission.Status = FdmsSubmissionStatus.Failed;
                submission.ErrorMessage = "Exceeded the 72-hour offline submission window.";
            }
            else
            {
                var result = await _gateway.SubmitAsync(submission.InvoiceHash, submission.Signature, submission.QrCode, ct);
                if (result.Accepted) { submission.Status = FdmsSubmissionStatus.Acknowledged; submission.ErrorMessage = null; }
                else { submission.Status = FdmsSubmissionStatus.Failed; submission.ErrorMessage = result.Error ?? "Submission failed."; }
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

    public sealed record FdmsGatewayResult(bool Accepted, string? ReceiptNumber, string? Error);

    /// <summary>The call to ZIMRA's fiscal service. Implement this once the device is registered and ZIMRA's current API specification and mTLS certificate are available, then register it in DI.</summary>
    public interface IFdmsGateway
    {
        Task<FdmsGatewayResult> SubmitAsync(string invoiceHash, string signature, string qrCode, CancellationToken ct);
    }

    /// <summary>Default gateway: states plainly that no ZIMRA connection exists, so nothing is ever reported as submitted.</summary>
    public sealed class NotConfiguredFdmsGateway : IFdmsGateway
    {
        public Task<FdmsGatewayResult> SubmitAsync(string invoiceHash, string signature, string qrCode, CancellationToken ct) =>
            Task.FromResult(new FdmsGatewayResult(false, null, "No ZIMRA gateway is configured. The signed invoice is queued and will be submitted when a gateway is registered."));
    }
}
