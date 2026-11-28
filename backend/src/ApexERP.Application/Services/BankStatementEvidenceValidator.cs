using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ApexERP.Application.Services
{
    public enum BankStatementAuthenticationMethod
    {
        UnverifiedUpload,
        TrustedBankApi,
        VerifiedDigitalSignature,
        IndependentBankConfirmation
    }

    /// <summary>
    /// Metadata supplied by the statement-ingestion adapter. OriginalBytes must be the exact
    /// received payload; a digest is calculated from those bytes rather than trusted from a caller.
    /// </summary>
    public sealed record BankStatementEvidenceEnvelope(
        Guid CompanyId,
        Guid ImportBatchId,
        string BankAccountReference,
        string StatementReference,
        DateTime PeriodStart,
        DateTime PeriodEnd,
        ReadOnlyMemory<byte> OriginalBytes,
        long DeclaredByteLength,
        string MediaType,
        string ParserVersion,
        string ExpectedSha256,
        BankStatementAuthenticationMethod AuthenticationMethod,
        bool AuthenticationVerified,
        string? AuthenticationEvidenceReference);

    public sealed record BankStatementEvidenceValidation(
        bool IsValid,
        string Reason,
        string? Sha256,
        long ByteLength);

    /// <summary>
    /// Validates source-file integrity, import identity and bank-origin authentication metadata.
    /// SHA-256 establishes byte identity only; bank authorship is accepted only when a trusted
    /// authentication mechanism has separately verified the source.
    /// </summary>
    public static class BankStatementEvidenceValidator
    {
        public static BankStatementEvidenceValidation ValidateIntegrity(
            BankStatementEvidenceEnvelope envelope,
            IReadOnlySet<Guid> existingImportBatchIds,
            IReadOnlySet<string> existingStatementKeys)
        {
            if (envelope.CompanyId == Guid.Empty || envelope.ImportBatchId == Guid.Empty ||
                string.IsNullOrWhiteSpace(envelope.BankAccountReference) ||
                string.IsNullOrWhiteSpace(envelope.StatementReference))
                return Reject("Company, import batch, bank account and statement identifiers are required.");
            if (existingImportBatchIds.Contains(envelope.ImportBatchId))
                return Reject("The import batch identifier has already been used.");
            var statementKey = $"{envelope.CompanyId:N}:{envelope.BankAccountReference.Trim()}:{envelope.StatementReference.Trim()}";
            if (existingStatementKeys.Contains(statementKey))
                return Reject("This company bank account statement has already been imported.");
            if (envelope.PeriodStart >= envelope.PeriodEnd)
                return Reject("The statement period is invalid.");
            if (envelope.OriginalBytes.IsEmpty || envelope.DeclaredByteLength != envelope.OriginalBytes.Length)
                return Reject("The original statement bytes are empty or the declared byte length does not match.");
            if (string.IsNullOrWhiteSpace(envelope.MediaType) ||
                string.IsNullOrWhiteSpace(envelope.ParserVersion))
                return Reject("A media type and versioned parser identity are required.");
            if (!TryParseSha256(envelope.ExpectedSha256, out var expectedDigest))
                return Reject("The expected SHA-256 digest must contain exactly 32 bytes of hexadecimal data.");

            var actualDigest = SHA256.HashData(envelope.OriginalBytes.Span);
            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
                return Reject("The original statement bytes do not match the supplied SHA-256 digest.");

            return new BankStatementEvidenceValidation(
                true,
                "Statement bytes, identity and metadata passed integrity validation; bank origin remains unauthenticated.",
                Convert.ToHexString(actualDigest).ToLowerInvariant(),
                envelope.OriginalBytes.Length);
        }

        public static BankStatementEvidenceValidation Validate(
            BankStatementEvidenceEnvelope envelope,
            IReadOnlySet<Guid> existingImportBatchIds,
            IReadOnlySet<string> existingStatementKeys)
        {
            var integrity = ValidateIntegrity(envelope, existingImportBatchIds, existingStatementKeys);
            if (!integrity.IsValid) return integrity;
            if (envelope.AuthenticationMethod != BankStatementAuthenticationMethod.IndependentBankConfirmation ||
                !envelope.AuthenticationVerified ||
                string.IsNullOrWhiteSpace(envelope.AuthenticationEvidenceReference))
                return Reject("Only independently evidenced bank confirmation is enabled. Trusted API and digital-signature authentication remain disabled until server-side verifiers are configured.");
            return integrity with
            {
                Reason = "Statement bytes and verified source-authentication metadata passed validation."
            };
        }

        private static bool TryParseSha256(string? value, out byte[] digest)
        {
            digest = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                return false;
            try
            {
                digest = Convert.FromHexString(value);
                return digest.Length == 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static BankStatementEvidenceValidation Reject(string reason) =>
            new(false, reason, null, 0);
    }
}
