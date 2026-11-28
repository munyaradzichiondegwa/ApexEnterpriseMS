using System.Globalization;
using System.Security.Cryptography;
using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers;

public sealed record UploadBankStatementRequest(
    Guid BankAccountId,
    string SourceInstitution,
    string SourceAccountReference,
    string StatementReference,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal TotalDebits,
    decimal TotalCredits,
    int TransactionCount,
    IFormFile File);

public sealed record AuthenticateBankStatementRequest(string ConfirmationReference);
public sealed record MatchBankStatementTransactionRequest(Guid BankTransactionId);
public sealed record ExcludeBankStatementTransactionRequest(string Reason);

[Route("api/v1/bank-statements")]
public sealed class BankStatementImportController : ApiControllerBase
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;
    private readonly ApexErpDbContext _db;
    private readonly ApprovalChainService _approvals;
    private readonly AuditService _audit;

    public BankStatementImportController(ApexErpDbContext db, ApprovalChainService approvals, AuditService audit)
    {
        _db = db;
        _approvals = approvals;
        _audit = audit;
    }

    /// <summary>Imports a strict UTF-8 CSV. The original bytes are retained and hashed, but the
    /// batch remains unauthenticated until an independent bank-confirmation approval is completed.</summary>
    [HttpPost("imports")]
    [Consumes("multipart/form-data")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> Upload([FromForm] UploadBankStatementRequest req, CancellationToken ct)
    {
        if (req.File == null || req.File.Length <= 0 || req.File.Length > MaxUploadBytes)
            return BadRequest(new { error = "Upload a non-empty statement CSV no larger than 10 MB." });
        if (req.PeriodStart.Kind != DateTimeKind.Utc || req.PeriodEnd.Kind != DateTimeKind.Utc ||
            req.PeriodStart >= req.PeriodEnd)
            return BadRequest(new { error = "Statement period boundaries must be valid UTC timestamps." });
        if (string.IsNullOrWhiteSpace(req.SourceInstitution) || req.SourceInstitution.Trim().Length > 160 ||
            string.IsNullOrWhiteSpace(req.SourceAccountReference) || req.SourceAccountReference.Trim().Length > 160 ||
            string.IsNullOrWhiteSpace(req.StatementReference) || req.StatementReference.Trim().Length > 160)
            return BadRequest(new { error = "Bank institution, account reference and statement reference are required and must not exceed 160 characters." });

        try
        {
            await using var stream = new MemoryStream((int)req.File.Length);
            await req.File.CopyToAsync(stream, ct);
            var originalBytes = stream.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
            var mediaType = string.IsNullOrWhiteSpace(req.File.ContentType)
                ? "text/csv" : req.File.ContentType.Trim();
            var fileName = Path.GetFileName(req.File.FileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "bank-statement.csv";
            if (fileName.Length > 255 || mediaType.Length > 128)
                return BadRequest(new { error = "File name or media type exceeds the supported metadata length." });

            var batch = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync(
                    $"ipsas24-bank-statement:{CompanyId:N}:{req.BankAccountId:N}:{req.PeriodStart:O}", token);
                var bankAccount = await _db.BankAccounts.SingleOrDefaultAsync(x =>
                    x.Id == req.BankAccountId && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Bank account not found in this company.");
                if (!string.Equals(bankAccount.BankName, req.SourceInstitution.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(bankAccount.AccountNo, req.SourceAccountReference.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Statement institution and account reference must match the registered bank account.");
                if (await _db.BankStatementImportBatches.AnyAsync(x =>
                    x.CompanyId == CompanyId && x.BankAccountId == bankAccount.Id &&
                    x.Status != BankStatementImportStatus.Rejected &&
                    x.PeriodStart < req.PeriodEnd && x.PeriodEnd > req.PeriodStart, token))
                    throw new InvalidOperationException("Statement periods for the same bank account cannot overlap. Resolve or formally replace the existing batch first.");
                if (await _db.BankStatementImportBatches.AnyAsync(x =>
                    x.CompanyId == CompanyId && x.BankAccountId == bankAccount.Id &&
                    x.StatementReference == req.StatementReference.Trim(), token))
                    throw new InvalidOperationException("This statement reference has already been imported for the bank account.");

                var parsed = BankStatementCsvParser.Parse(
                    originalBytes, bankAccount.Currency, req.PeriodStart, req.PeriodEnd,
                    req.TotalDebits, req.TotalCredits, req.OpeningBalance, req.ClosingBalance,
                    req.TransactionCount);
                var externalIds = parsed.Transactions.Select(x => x.ExternalTransactionId).ToArray();
                var fingerprints = parsed.Transactions.Select(x => x.TransactionFingerprint).ToArray();
                if (await _db.BankStatementImportedTransactions.AnyAsync(x =>
                    x.CompanyId == CompanyId && x.BankAccountId == bankAccount.Id &&
                    (externalIds.Contains(x.ExternalTransactionId) ||
                     fingerprints.Contains(x.TransactionFingerprint)), token))
                    throw new InvalidOperationException("The statement contains a transaction identifier or fingerprint already imported for this bank account.");

                var envelope = new BankStatementEvidenceEnvelope(
                    CompanyId, Guid.NewGuid(), bankAccount.AccountNo, req.StatementReference.Trim(),
                    req.PeriodStart, req.PeriodEnd, originalBytes, originalBytes.Length,
                    mediaType, BankStatementCsvParser.ParserVersion, sha,
                    BankStatementAuthenticationMethod.UnverifiedUpload, false, null);
                var integrity = BankStatementEvidenceValidator.ValidateIntegrity(
                    envelope, new HashSet<Guid>(), new HashSet<string>());
                if (!integrity.IsValid)
                    throw new InvalidOperationException(integrity.Reason);

                var item = new BankStatementImportBatch
                {
                    Id = envelope.ImportBatchId, CompanyId = CompanyId, BankAccountId = bankAccount.Id,
                    SourceInstitution = req.SourceInstitution.Trim(),
                    SourceAccountReference = req.SourceAccountReference.Trim(),
                    StatementReference = req.StatementReference.Trim(),
                    PeriodStart = req.PeriodStart, PeriodEnd = req.PeriodEnd,
                    OpeningBalance = req.OpeningBalance, ClosingBalance = req.ClosingBalance,
                    TotalDebits = parsed.TotalDebits, TotalCredits = parsed.TotalCredits,
                    TransactionCount = parsed.Transactions.Count,
                    OriginalFileName = fileName, MediaType = mediaType,
                    ParserVersion = BankStatementCsvParser.ParserVersion,
                    Sha256 = integrity.Sha256!, ByteLength = integrity.ByteLength,
                    OriginalBytes = originalBytes, UploadedBy = UserId, UploadedAt = DateTime.UtcNow,
                    AuthenticationMethod = BankStatementOriginAuthenticationMethod.UnverifiedUpload,
                    Status = BankStatementImportStatus.PendingAuthentication, CreatedBy = UserId
                };
                _db.BankStatementImportBatches.Add(item);
                foreach (var row in parsed.Transactions)
                    _db.BankStatementImportedTransactions.Add(new BankStatementImportedTransaction
                    {
                        CompanyId = CompanyId, ImportBatchId = item.Id, BankAccountId = bankAccount.Id,
                        ExternalTransactionId = row.ExternalTransactionId, TransactionDate = row.TransactionDate,
                        Description = row.Description, Reference = row.Reference,
                        Debit = row.Debit, Credit = row.Credit, Currency = row.Currency,
                        SourceLineNumber = row.SourceLineNumber,
                        TransactionFingerprint = row.TransactionFingerprint,
                        Status = BankStatementImportedTransactionStatus.Imported,
                        CreatedBy = UserId
                    });
                _audit.Record(CompanyId, UserId, "BankStatementImportBatch.Uploaded", "Banking",
                    nameof(BankStatementImportBatch), item.Id, null,
                    new { item.BankAccountId, item.StatementReference, item.PeriodStart, item.PeriodEnd,
                        item.TransactionCount, item.TotalDebits, item.TotalCredits, item.Sha256,
                        item.ByteLength, item.ParserVersion, Authentication = "UnverifiedUpload" });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
            return Ok(new
            {
                batch.Id, batch.StatementReference, batch.Status, batch.TransactionCount,
                batch.TotalDebits, batch.TotalCredits, batch.Sha256, batch.ByteLength,
                AuthenticationRequired = true
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("imports/{id:guid}/submit-authentication")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> SubmitAuthentication(Guid id, CancellationToken ct)
    {
        try
        {
            return await _db.InTransactionAsync<IActionResult>(async token =>
            {
                var batch = await _db.BankStatementImportBatches.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Bank statement import batch not found.");
                await _db.AcquireTransactionLockAsync($"bank-statement-batch:{CompanyId:N}:{id:N}", token);
                if (batch.Status != BankStatementImportStatus.PendingAuthentication ||
                    batch.UploadedBy != UserId || batch.AuthenticationWorkflowId.HasValue)
                    throw new InvalidOperationException("Only the uploader may submit an unsubmitted, unauthenticated statement batch for independent bank confirmation.");
                var workflow = await _approvals.SubmitForApprovalAsync(CompanyId,
                    "BankStatementImportBatch", batch.Id, nameof(BankStatementImportBatch),
                    UserId, batch.TotalDebits + batch.TotalCredits, null,
                    batch.StatementReference, token);
                batch.AuthenticationWorkflowId = workflow.Id;
                await _db.SaveChangesAsync(token);
                return Ok(new { batch.Id, batch.Status, batch.AuthenticationWorkflowId });
            }, ct);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Authenticates only through independent human confirmation. Trusted API and
    /// digital-signature methods remain disabled until their server-side verifiers are configured.</summary>
    [HttpPost("imports/{id:guid}/authenticate")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> Authenticate(Guid id, AuthenticateBankStatementRequest req, CancellationToken ct)
    {
        try
        {
            var batch = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"bank-statement-batch:{CompanyId:N}:{id:N}", token);
                var item = await _db.BankStatementImportBatches.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Bank statement import batch not found.");
                if (item.Status != BankStatementImportStatus.PendingAuthentication ||
                    !item.AuthenticationWorkflowId.HasValue || item.UploadedBy == UserId)
                    throw new InvalidOperationException("An independent approver must authenticate a submitted, unauthenticated batch.");
                if (string.IsNullOrWhiteSpace(req.ConfirmationReference) || req.ConfirmationReference.Trim().Length > 1000)
                    throw new InvalidOperationException("A bank confirmation evidence reference is required.");
                var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).SingleOrDefaultAsync(x =>
                    x.Id == item.AuthenticationWorkflowId.Value && x.CompanyId == CompanyId &&
                    x.RequestType == "BankStatementImportBatch" && x.ReferenceId == item.Id &&
                    x.Status == WorkflowStatus.Approved &&
                    x.Approvals.Any(a => a.Approved && a.ApproverId == UserId), token)
                    ?? throw new InvalidOperationException("An approved independent bank-confirmation workflow is required.");
                if (workflow.SubmittedBy == UserId || item.UploadedBy == UserId)
                    throw new InvalidOperationException("The uploader or workflow submitter cannot authenticate the statement.");
                item.AuthenticationMethod = BankStatementOriginAuthenticationMethod.IndependentBankConfirmation;
                item.AuthenticationEvidenceReference = req.ConfirmationReference.Trim();
                item.AuthenticatedBy = UserId;
                item.AuthenticatedAt = DateTime.UtcNow;
                item.Status = BankStatementImportStatus.Authenticated;
                _audit.Record(CompanyId, UserId, "BankStatementImportBatch.Authenticated", "Banking",
                    nameof(BankStatementImportBatch), item.Id,
                    new { Status = BankStatementImportStatus.PendingAuthentication },
                    new { item.Status, item.AuthenticationMethod, item.AuthenticationEvidenceReference,
                        item.AuthenticatedBy, item.AuthenticatedAt });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
            return Ok(new { batch.Id, batch.Status, batch.AuthenticationMethod, batch.AuthenticatedBy, batch.AuthenticatedAt });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("imports/{id:guid}/transactions/{transactionId:guid}/match")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> MatchTransaction(Guid id, Guid transactionId,
        MatchBankStatementTransactionRequest req, CancellationToken ct)
    {
        try
        {
            var matched = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"bank-statement-batch:{CompanyId:N}:{id:N}", token);
                await _db.AcquireTransactionLockAsync($"bank-transaction:{CompanyId:N}:{req.BankTransactionId:N}", token);
                var batch = await _db.BankStatementImportBatches.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId &&
                    x.Status == BankStatementImportStatus.Authenticated, token)
                    ?? throw new InvalidOperationException("An independently authenticated statement batch is required before reconciliation.");
                if (batch.UploadedBy == UserId || batch.AuthenticatedBy == UserId)
                    throw new InvalidOperationException("The uploader and authenticator cannot perform transaction matching.");
                var statementLine = await _db.BankStatementImportedTransactions.SingleOrDefaultAsync(x =>
                    x.Id == transactionId && x.CompanyId == CompanyId &&
                    x.ImportBatchId == batch.Id &&
                    (x.Status == BankStatementImportedTransactionStatus.Imported ||
                     x.Status == BankStatementImportedTransactionStatus.Excluded), token)
                    ?? throw new InvalidOperationException("Unmatched or quarantined imported statement transaction not found.");
                var bankTransaction = await _db.BankTransactions.SingleOrDefaultAsync(x =>
                    x.Id == req.BankTransactionId && x.CompanyId == CompanyId &&
                    x.BankAccountId == batch.BankAccountId, token)
                    ?? throw new InvalidOperationException("Bank transaction not found for the statement bank account.");
                if (bankTransaction.MatchedStatementLineHash.HasValue || !bankTransaction.JournalEntryId.HasValue)
                    throw new InvalidOperationException("The bank transaction is already linked to another statement row or lacks a posted journal.");
                var journalPosted = await _db.JournalEntries.AnyAsync(x =>
                    x.Id == bankTransaction.JournalEntryId.Value && x.CompanyId == CompanyId &&
                    x.Status == JournalStatus.Posted, token);
                var account = await _db.BankAccounts.SingleAsync(x =>
                    x.Id == batch.BankAccountId && x.CompanyId == CompanyId, token);
                if (!journalPosted ||
                    bankTransaction.TransactionDate != statementLine.TransactionDate ||
                    bankTransaction.Debit != statementLine.Debit ||
                    bankTransaction.Credit != statementLine.Credit ||
                    !string.Equals(account.Currency, statementLine.Currency, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((bankTransaction.Reference ?? string.Empty).Trim(),
                        (statementLine.Reference ?? string.Empty).Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Bank transaction does not exactly match the imported statement date, amount, currency and reference.");
                var priorStatus = statementLine.Status;
                var priorExclusion = new
                {
                    statementLine.ExclusionReason,
                    statementLine.ExcludedBy,
                    statementLine.ExcludedAt
                };
                bankTransaction.IsReconciled = true;
                bankTransaction.MatchedStatementLineHash = statementLine.Id;
                statementLine.Status = BankStatementImportedTransactionStatus.Matched;
                statementLine.MatchedBankTransactionId = bankTransaction.Id;
                statementLine.MatchedBy = UserId;
                statementLine.MatchedAt = DateTime.UtcNow;
                // A reviewed exclusion is a quarantine decision, not permission to omit a
                // statement row from completeness. A later independent exact match may resolve it.
                statementLine.ExclusionReason = null;
                statementLine.ExcludedBy = null;
                statementLine.ExcludedAt = null;
                _audit.Record(CompanyId, UserId, "BankStatementTransaction.Matched", "Banking",
                    nameof(BankStatementImportedTransaction), statementLine.Id,
                    new { PriorStatus = priorStatus, PriorExclusion = priorExclusion },
                    new { statementLine.Status, statementLine.MatchedBankTransactionId,
                        statementLine.MatchedBy, statementLine.MatchedAt });
                await _db.SaveChangesAsync(token);
                return statementLine;
            }, ct);
            return Ok(matched);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("imports/{id:guid}/transactions/{transactionId:guid}/exclude")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> ExcludeTransaction(Guid id, Guid transactionId,
        ExcludeBankStatementTransactionRequest req, CancellationToken ct)
    {
        try
        {
            var excluded = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"bank-statement-batch:{CompanyId:N}:{id:N}", token);
                var batch = await _db.BankStatementImportBatches.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId &&
                    x.Status == BankStatementImportStatus.Authenticated, token)
                    ?? throw new InvalidOperationException("An authenticated statement batch is required.");
                if (batch.UploadedBy == UserId || batch.AuthenticatedBy == UserId)
                    throw new InvalidOperationException("The uploader and authenticator cannot independently exclude statement transactions.");
                if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length > 1000)
                    throw new InvalidOperationException("An independently reviewed exclusion reason is required.");
                var line = await _db.BankStatementImportedTransactions.SingleOrDefaultAsync(x =>
                    x.Id == transactionId && x.CompanyId == CompanyId &&
                    x.ImportBatchId == batch.Id && x.Status == BankStatementImportedTransactionStatus.Imported, token)
                    ?? throw new InvalidOperationException("Unmatched imported statement transaction not found.");
                line.Status = BankStatementImportedTransactionStatus.Excluded;
                line.ExclusionReason = req.Reason.Trim();
                line.ExcludedBy = UserId;
                line.ExcludedAt = DateTime.UtcNow;
                _audit.Record(CompanyId, UserId, "BankStatementTransaction.Excluded", "Banking",
                    nameof(BankStatementImportedTransaction), line.Id, null,
                    new { line.Status, line.ExclusionReason, line.ExcludedBy, line.ExcludedAt });
                await _db.SaveChangesAsync(token);
                return line;
            }, ct);
            return Ok(excluded);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("imports/{id:guid}/certify-completeness")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> CertifyCompleteness(Guid id, CancellationToken ct)
    {
        try
        {
            var batch = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"bank-statement-batch:{CompanyId:N}:{id:N}", token);
                var item = await _db.BankStatementImportBatches.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId &&
                    x.Status == BankStatementImportStatus.Authenticated, token)
                    ?? throw new InvalidOperationException("An authenticated statement batch is required.");
                if (item.UploadedBy == UserId || item.AuthenticatedBy == UserId)
                    throw new InvalidOperationException("The uploader and authenticator cannot certify statement completeness.");
                var lines = await _db.BankStatementImportedTransactions.Where(x =>
                    x.CompanyId == CompanyId && x.ImportBatchId == item.Id).ToListAsync(token);
                if (lines.Count != item.TransactionCount ||
                    lines.Any(x => x.Status != BankStatementImportedTransactionStatus.Matched ||
                        x.MatchedBy == null || x.MatchedAt == null || !x.MatchedBankTransactionId.HasValue))
                    throw new InvalidOperationException("Every imported statement row must be matched one-to-one to a posted bank-ledger transaction. Quarantined exclusions cannot certify source completeness.");
                if (lines.Any(x => x.MatchedBy == UserId))
                    throw new InvalidOperationException("The completeness certifier cannot certify their own transaction matching.");
                var totalDebits = lines.Sum(x => x.Debit);
                var totalCredits = lines.Sum(x => x.Credit);
                if (totalDebits != item.TotalDebits || totalCredits != item.TotalCredits ||
                    item.OpeningBalance + totalCredits - totalDebits != item.ClosingBalance)
                    throw new InvalidOperationException("Imported transaction totals no longer reconcile to the authenticated statement control totals.");

                var bankTransactions = await _db.BankTransactions.Where(x =>
                    x.CompanyId == CompanyId && x.BankAccountId == item.BankAccountId &&
                    x.TransactionDate >= item.PeriodStart && x.TransactionDate < item.PeriodEnd).ToListAsync(token);
                var matchedIds = lines.Where(x => x.Status == BankStatementImportedTransactionStatus.Matched)
                    .Select(x => x.MatchedBankTransactionId!.Value).ToHashSet();
                if (bankTransactions.Any(x => !x.IsReconciled || !x.MatchedStatementLineHash.HasValue ||
                    !matchedIds.Contains(x.Id) || !lines.Any(line => line.Id == x.MatchedStatementLineHash.Value &&
                        line.MatchedBankTransactionId == x.Id)))
                    throw new InvalidOperationException("The live bank-transaction population contains unmatched or unreconciled records.");
                if (matchedIds.Count != bankTransactions.Count)
                    throw new InvalidOperationException("Imported-to-ledger matching is not one-to-one for the complete bank-account period.");

                item.Status = BankStatementImportStatus.Certified;
                item.CompletenessCertifiedBy = UserId;
                item.CompletenessCertifiedAt = DateTime.UtcNow;
                _audit.Record(CompanyId, UserId, "BankStatementImportBatch.CompletenessCertified", "Banking",
                    nameof(BankStatementImportBatch), item.Id,
                    new { Status = BankStatementImportStatus.Authenticated },
                    new { item.Status, item.CompletenessCertifiedBy, item.CompletenessCertifiedAt,
                        MatchedCount = lines.Count(x => x.Status == BankStatementImportedTransactionStatus.Matched),
                        ExcludedCount = lines.Count(x => x.Status == BankStatementImportedTransactionStatus.Excluded) });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
            return Ok(new { batch.Id, batch.Status, batch.CompletenessCertifiedBy, batch.CompletenessCertifiedAt });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
