using System.Security.Cryptography;
using System.Text;
using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class BankStatementEvidenceValidatorTests
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Batch = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("bank-statement-payload");

    private static BankStatementEvidenceEnvelope Envelope(
        string? digest = null,
        BankStatementAuthenticationMethod method = BankStatementAuthenticationMethod.TrustedBankApi,
        bool verified = true,
        long? byteLength = null) =>
        new(
            Company,
            Batch,
            "BANK-ACCOUNT-001",
            "STATEMENT-2026-12",
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Bytes,
            byteLength ?? Bytes.Length,
            "application/pdf",
            "bank-pdf-parser/1.0.0",
            digest ?? Convert.ToHexString(SHA256.HashData(Bytes)),
            method,
            verified,
            "bank-api-response:statement-2026-12");

    [Fact]
    public void Validate_AcceptsMatchingBytesAndVerifiedBankOrigin()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(), new HashSet<Guid>(), new HashSet<string>());

        Assert.True(result.IsValid);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(Bytes.Length, result.ByteLength);
    }

    [Fact]
    public void Validate_RejectsTamperedOrMismatchedBytes()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(digest: new string('0', 64)), new HashSet<Guid>(), new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains("do not match", result.Reason);
    }

    [Fact]
    public void Validate_RejectsHashWithoutBankOriginAuthentication()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(method: BankStatementAuthenticationMethod.UnverifiedUpload, verified: false),
            new HashSet<Guid>(), new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains("bank-origin authentication", result.Reason);
    }

    [Fact]
    public void Validate_RejectsReusedImportBatch()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(), new HashSet<Guid> { Batch }, new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains("batch identifier", result.Reason);
    }

    [Fact]
    public void Validate_RejectsDuplicateStatementIdentity()
    {
        const string key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:BANK-ACCOUNT-001:STATEMENT-2026-12";
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(), new HashSet<Guid>(), new HashSet<string> { key });

        Assert.False(result.IsValid);
        Assert.Contains("already been imported", result.Reason);
    }

    [Fact]
    public void Validate_RejectsIncorrectDeclaredByteLength()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(byteLength: Bytes.Length + 1), new HashSet<Guid>(), new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains("byte length", result.Reason);
    }

    [Fact]
    public void Validate_RejectsMalformedSha256()
    {
        var result = BankStatementEvidenceValidator.Validate(
            Envelope(digest: "not-a-digest"), new HashSet<Guid>(), new HashSet<string>());

        Assert.False(result.IsValid);
        Assert.Contains("32 bytes", result.Reason);
    }
}
