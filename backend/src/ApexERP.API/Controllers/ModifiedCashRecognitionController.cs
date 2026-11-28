using ApexERP.API.Authorization;
using ApexERP.Application.Services;
using ApexERP.Application.Security;
using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers;

public sealed record ModifiedCashMappingInput(string SourceEventKind, Guid BudgetVersionLineId);
public sealed record CreateModifiedCashPolicyRequest(
    Guid BudgetVersionId, string PolicyReference, DateTime EffectiveFrom, DateTime? EffectiveTo,
    DateTime ReportingPeriodStart, DateTime ReportingPeriodEnd, int PostPeriodRecognitionWindowDays,
    ModifiedCashBudgetTreatment Treatment, IReadOnlyCollection<ModifiedCashMappingInput> Mappings);
public sealed record CreateModifiedCashRecognitionRequest(Guid CommitmentId, Guid GoodsReceiptLineId, string EvidenceReference);

[Route("api/v1/public-sector/modified-cash")]
public sealed class ModifiedCashRecognitionController : ApiControllerBase
{
    private readonly ApexErpDbContext _db;
    private readonly ApprovalChainService _approvals;
    private readonly AuditService _audit;

    public ModifiedCashRecognitionController(ApexErpDbContext db, ApprovalChainService approvals, AuditService audit)
    {
        _db = db;
        _approvals = approvals;
        _audit = audit;
    }

    [HttpPost("policies")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> CreatePolicy(CreateModifiedCashPolicyRequest req, CancellationToken ct)
    {
        if (req.Mappings == null || req.Mappings.Count == 0 ||
            req.Mappings.Any(x => string.IsNullOrWhiteSpace(x.SourceEventKind) || x.BudgetVersionLineId == Guid.Empty) ||
            req.Mappings.Select(x => x.SourceEventKind.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != req.Mappings.Count)
            return BadRequest(new { error = "Provide unique, explicit commitment-event-to-budget-line mappings." });
        if (req.Mappings.Any(x => !string.Equals(x.SourceEventKind.Trim(), "GoodsReceived", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { error = "GoodsReceived is the only commitment event class currently supported. Other event classes remain disabled until their source adapters are implemented." });
        if (string.IsNullOrWhiteSpace(req.PolicyReference) || req.PolicyReference.Trim().Length > 160 ||
            req.EffectiveFrom >= (req.EffectiveTo ?? DateTime.MaxValue) ||
            req.ReportingPeriodStart >= req.ReportingPeriodEnd ||
            req.PostPeriodRecognitionWindowDays is < 0 or > 120 ||
            req.EffectiveFrom > req.ReportingPeriodStart ||
            (req.EffectiveTo.HasValue && req.EffectiveTo.Value < req.ReportingPeriodEnd))
            return BadRequest(new { error = "Policy reference, effective dates, reporting period and a recognition window of 0–120 days are required." });

        try
        {
            var policy = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"ipsas24-modified-cash-policy:{CompanyId:N}:{req.BudgetVersionId:N}", token);
                var version = await _db.PublicSectorBudgetVersions.Include(x => x.Lines)
                    .SingleOrDefaultAsync(x => x.Id == req.BudgetVersionId && x.CompanyId == CompanyId &&
                        x.Status == PublicSectorBudgetVersionStatus.Approved, token)
                    ?? throw new InvalidOperationException("An approved budget version in this company is required.");
                if (version.AccountingBasis != AccountingBasis.Cash ||
                    req.ReportingPeriodStart < version.PeriodStart || req.ReportingPeriodEnd > version.PeriodEnd)
                    throw new InvalidOperationException("Modified-cash policy must apply to an approved cash-basis budget and a reporting period contained within that budget period.");
                var lineIds = req.Mappings.Select(x => x.BudgetVersionLineId).Distinct().ToArray();
                var lines = version.Lines.Where(x => lineIds.Contains(x.Id)).ToList();
                if (lines.Count != lineIds.Length)
                    throw new InvalidOperationException("Every policy mapping must reference a line in the selected approved budget version.");
                foreach (var line in lines)
                {
                    var account = await _db.GlAccounts.SingleOrDefaultAsync(x => x.Id == line.GlAccountId &&
                        x.CompanyId == CompanyId && x.IsActive, token);
                    if (account == null || account.Type != GlAccountType.Expense)
                        throw new InvalidOperationException("Modified-cash commitment events must map to active expense GL budget lines.");
                }
                var versionNumber = (await _db.ModifiedCashRecognitionPolicies.Where(x =>
                    x.CompanyId == CompanyId && x.BudgetVersionId == version.Id)
                    .Select(x => (int?)x.VersionNumber).MaxAsync(token) ?? 0) + 1;
                var item = new ModifiedCashRecognitionPolicy
                {
                    CompanyId = CompanyId, BudgetVersionId = version.Id, FinancialYear = version.FinancialYear,
                    VersionNumber = versionNumber, PolicyReference = req.PolicyReference.Trim(),
                    EffectiveFrom = req.EffectiveFrom, EffectiveTo = req.EffectiveTo,
                    ReportingPeriodStart = req.ReportingPeriodStart, ReportingPeriodEnd = req.ReportingPeriodEnd,
                    PostPeriodRecognitionWindowDays = req.PostPeriodRecognitionWindowDays,
                    Treatment = req.Treatment, Status = ModifiedCashPolicyStatus.Draft,
                    PreparedBy = UserId, CreatedBy = UserId
                };
                _db.ModifiedCashRecognitionPolicies.Add(item);
                foreach (var mapping in req.Mappings)
                    _db.ModifiedCashRecognitionMappings.Add(new ModifiedCashRecognitionMapping
                    {
                        CompanyId = CompanyId, PolicyId = item.Id,
                        SourceEventKind = mapping.SourceEventKind.Trim(),
                        BudgetVersionLineId = mapping.BudgetVersionLineId, CreatedBy = UserId
                    });
                _audit.Record(CompanyId, UserId, "ModifiedCashRecognitionPolicy.Created", "PublicSector",
                    nameof(ModifiedCashRecognitionPolicy), item.Id, null,
                    new { item.BudgetVersionId, item.VersionNumber, item.PolicyReference, item.EffectiveFrom,
                        item.EffectiveTo, item.ReportingPeriodStart, item.ReportingPeriodEnd,
                        item.PostPeriodRecognitionWindowDays, item.Treatment, MappingCount = req.Mappings.Count });
                await _db.SaveChangesAsync(token);
                return item;
            }, ct);
            return Ok(policy);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("policies/{id:guid}/submit")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> SubmitPolicy(Guid id, CancellationToken ct)
    {
        try
        {
            return await _db.InTransactionAsync<IActionResult>(async token =>
            {
                var policy = await _db.ModifiedCashRecognitionPolicies.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Modified-cash policy not found.");
                if (policy.Status != ModifiedCashPolicyStatus.Draft || policy.PreparedBy != UserId)
                    throw new InvalidOperationException("Only the policy preparer may submit a draft policy.");
                var workflow = await _approvals.SubmitForApprovalAsync(CompanyId,
                    "ModifiedCashRecognitionPolicy", policy.Id, nameof(ModifiedCashRecognitionPolicy),
                    UserId, 0m, null, policy.PolicyReference, token);
                policy.ApprovalWorkflowId = workflow.Id;
                policy.SubmittedBy = UserId;
                policy.SubmittedAt = DateTime.UtcNow;
                policy.Status = ModifiedCashPolicyStatus.PendingApproval;
                await _db.SaveChangesAsync(token);
                return Ok(policy);
            }, ct);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("policies/{id:guid}/finalise-approval")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> FinalisePolicyApproval(Guid id, CancellationToken ct)
    {
        try
        {
            var policy = await _db.ModifiedCashRecognitionPolicies.SingleOrDefaultAsync(x =>
                x.Id == id && x.CompanyId == CompanyId, ct)
                ?? throw new InvalidOperationException("Modified-cash policy not found.");
            if (policy.Status != ModifiedCashPolicyStatus.PendingApproval || !policy.ApprovalWorkflowId.HasValue)
                throw new InvalidOperationException("Only a policy pending approval can be finalised.");
            var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).SingleOrDefaultAsync(x =>
                x.Id == policy.ApprovalWorkflowId.Value && x.CompanyId == CompanyId &&
                x.RequestType == "ModifiedCashRecognitionPolicy" && x.ReferenceId == policy.Id &&
                x.Status == WorkflowStatus.Approved && x.Approvals.Any(a => a.Approved && a.ApproverId == UserId), ct)
                ?? throw new InvalidOperationException("A completed independent approval decision is required.");
            if (policy.PreparedBy == UserId || workflow.SubmittedBy == UserId)
                throw new InvalidOperationException("The policy preparer cannot approve their own policy.");
            policy.Status = ModifiedCashPolicyStatus.Approved;
            policy.ApprovedBy = UserId;
            policy.ApprovedAt = DateTime.UtcNow;
            _audit.Record(CompanyId, UserId, "ModifiedCashRecognitionPolicy.Approved", "PublicSector",
                nameof(ModifiedCashRecognitionPolicy), policy.Id,
                new { Status = ModifiedCashPolicyStatus.PendingApproval },
                new { policy.Status, policy.ApprovedBy, policy.ApprovedAt, policy.ApprovalWorkflowId });
            await _db.SaveChangesAsync(ct);
            return Ok(policy);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("policies/{id:guid}/recognitions")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> CreateRecognition(Guid id, CreateModifiedCashRecognitionRequest req, CancellationToken ct)
    {
        try
        {
            var item = await _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"ipsas24-modified-cash-commitment:{CompanyId:N}:{req.CommitmentId:N}", token);
                var policy = await _db.ModifiedCashRecognitionPolicies.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId && x.Status == ModifiedCashPolicyStatus.Approved, token)
                    ?? throw new InvalidOperationException("An approved modified-cash policy is required.");
                if (policy.Treatment == ModifiedCashBudgetTreatment.DiscloseSeparately &&
                    !await _db.ModifiedCashRecognitionMappings.AnyAsync(x => x.CompanyId == CompanyId && x.PolicyId == policy.Id, token))
                    throw new InvalidOperationException("A disclosure policy requires an approved event classification mapping.");
                if (string.IsNullOrWhiteSpace(req.EvidenceReference) || req.EvidenceReference.Trim().Length > 1000)
                    throw new InvalidOperationException("A source evidence reference of up to 1,000 characters is required.");
                var commitment = await _db.Commitments.SingleOrDefaultAsync(x =>
                    x.Id == req.CommitmentId && x.CompanyId == CompanyId && x.Status == CommitmentStatus.Active, token)
                    ?? throw new InvalidOperationException("An active commitment in this company is required.");
                var receipt = await _db.GoodsReceiptLines.Where(x => x.Id == req.GoodsReceiptLineId)
                    .Join(_db.GoodsReceipts.Where(x => x.CompanyId == CompanyId),
                        line => line.GoodsReceiptId, header => header.Id,
                        (line, header) => new { Line = line, Header = header })
                    .SingleOrDefaultAsync(token)
                    ?? throw new InvalidOperationException("The goods-receipt line was not found in this company.");
                if (receipt.Header.PurchaseOrderId != commitment.PurchaseOrderId)
                    throw new InvalidOperationException("The goods receipt does not belong to the purchase order underlying this commitment.");
                if (!await _db.PurchaseOrderLines.AnyAsync(x => x.Id == receipt.Line.PurchaseOrderLineId &&
                    x.PurchaseOrderId == commitment.PurchaseOrderId, token))
                    throw new InvalidOperationException("The goods-receipt line is not linked to the committed purchase order.");
                var eventDate = receipt.Header.ReceiptDate;
                var acceptedAt = DateTime.UtcNow;
                if (eventDate < policy.ReportingPeriodStart || eventDate >= policy.ReportingPeriodEnd ||
                    acceptedAt > policy.ReportingPeriodEnd.AddDays(policy.PostPeriodRecognitionWindowDays) ||
                    eventDate < policy.EffectiveFrom || (policy.EffectiveTo.HasValue && eventDate >= policy.EffectiveTo.Value))
                    throw new InvalidOperationException("The goods receipt falls outside the policy reporting/effective period or the permitted post-period evidence window.");
                var amount = decimal.Round(receipt.Line.QuantityReceived * receipt.Line.UnitCost, 6, MidpointRounding.AwayFromZero);
                if (amount <= 0m)
                    throw new InvalidOperationException("The goods-receipt line must have a positive evidenced obligation amount.");
                var mapping = await _db.ModifiedCashRecognitionMappings.SingleOrDefaultAsync(x =>
                    x.CompanyId == CompanyId && x.PolicyId == policy.Id &&
                    x.SourceEventKind == "GoodsReceived", token)
                    ?? throw new InvalidOperationException("No approved GoodsReceived budget classification mapping exists.");
                var budgetLine = await _db.PublicSectorBudgetVersionLines.SingleOrDefaultAsync(x =>
                    x.Id == mapping.BudgetVersionLineId && x.CompanyId == CompanyId &&
                    x.BudgetVersionId == policy.BudgetVersionId &&
                    x.FundCentreId == commitment.FundCentreId, token)
                    ?? throw new InvalidOperationException("The mapped budget line does not match the commitment fund centre and approved budget version.");
                var account = await _db.GlAccounts.SingleOrDefaultAsync(x =>
                    x.Id == budgetLine.GlAccountId && x.CompanyId == CompanyId &&
                    x.IsActive && x.Type == GlAccountType.Expense, token)
                    ?? throw new InvalidOperationException("The mapped expense GL account is no longer active.");
                if (await _db.ModifiedCashRecognitionLedgerEvents.AnyAsync(x => x.CompanyId == CompanyId &&
                    x.PolicyId == policy.Id && x.SourceEventId == req.GoodsReceiptLineId &&
                    x.EventKind == ModifiedCashLedgerEventKind.Recognition, token))
                    throw new InvalidOperationException("This goods-receipt line has already been recognised under this policy.");
                var ledger = await _db.ModifiedCashRecognitionLedgerEvents.Where(x =>
                    x.CompanyId == CompanyId && x.CommitmentId == commitment.Id &&
                    x.Status == ModifiedCashLedgerEventStatus.Certified).ToListAsync(token);
                var netRecognised = ledger.Sum(x => x.EventKind == ModifiedCashLedgerEventKind.Recognition ? x.Amount : -x.Amount);
                var remainingCommitment = commitment.Amount - commitment.RealisedAmount - netRecognised;
                if (amount > remainingCommitment)
                    throw new InvalidOperationException("The goods-receipt obligation exceeds the unrecognised, unliquidated commitment balance.");
                var result = new ModifiedCashRecognitionLedgerEvent
                {
                    CompanyId = CompanyId, PolicyId = policy.Id, CommitmentId = commitment.Id,
                    SourceEventId = req.GoodsReceiptLineId, SourceEventKind = "GoodsReceived",
                    EventKind = ModifiedCashLedgerEventKind.Recognition, Amount = amount,
                    EventDate = eventDate, EvidenceAcceptedAt = acceptedAt,
                    EvidenceReference = req.EvidenceReference.Trim(),
                    Status = ModifiedCashLedgerEventStatus.Draft, PreparedBy = UserId, CreatedBy = UserId
                };
                _db.ModifiedCashRecognitionLedgerEvents.Add(result);
                _audit.Record(CompanyId, UserId, "ModifiedCashRecognitionLedgerEvent.Created", "PublicSector",
                    nameof(ModifiedCashRecognitionLedgerEvent), result.Id, null,
                    new { result.PolicyId, result.CommitmentId, result.SourceEventId, result.Amount,
                        result.EventDate, result.EvidenceAcceptedAt, result.EvidenceReference });
                await _db.SaveChangesAsync(token);
                return result;
            }, ct);
            return Ok(item);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("ledger-events/{id:guid}/submit")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> SubmitLedgerEvent(Guid id, CancellationToken ct)
    {
        try
        {
            return await _db.InTransactionAsync<IActionResult>(async token =>
            {
                var item = await _db.ModifiedCashRecognitionLedgerEvents.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Modified-cash ledger event not found.");
                if (item.Status != ModifiedCashLedgerEventStatus.Draft || item.PreparedBy != UserId)
                    throw new InvalidOperationException("Only the event preparer may submit a draft event.");
                var workflow = await _approvals.SubmitForApprovalAsync(CompanyId,
                    "ModifiedCashRecognitionLedgerEvent", item.Id, nameof(ModifiedCashRecognitionLedgerEvent),
                    UserId, item.Amount, null, item.EvidenceReference, token);
                item.ApprovalWorkflowId = workflow.Id;
                item.SubmittedBy = UserId;
                item.SubmittedAt = DateTime.UtcNow;
                item.Status = ModifiedCashLedgerEventStatus.PendingCertification;
                await _db.SaveChangesAsync(token);
                return Ok(item);
            }, ct);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("ledger-events/{id:guid}/certify")]
    [HasPermission(Permissions.PublicSectorBudgetOverride)]
    public async Task<IActionResult> CertifyLedgerEvent(Guid id, CancellationToken ct)
    {
        try
        {
            var item = await _db.InTransactionAsync(async token =>
            {
                var ledger = await _db.ModifiedCashRecognitionLedgerEvents.SingleOrDefaultAsync(x =>
                    x.Id == id && x.CompanyId == CompanyId, token)
                    ?? throw new InvalidOperationException("Modified-cash ledger event not found.");
                if (ledger.Status != ModifiedCashLedgerEventStatus.PendingCertification ||
                    !ledger.ApprovalWorkflowId.HasValue)
                    throw new InvalidOperationException("Only a pending event with an approval workflow can be certified.");
                var workflow = await _db.WorkflowRequests.Include(x => x.Approvals).SingleOrDefaultAsync(x =>
                    x.Id == ledger.ApprovalWorkflowId.Value && x.CompanyId == CompanyId &&
                    x.RequestType == "ModifiedCashRecognitionLedgerEvent" && x.ReferenceId == ledger.Id &&
                    x.Status == WorkflowStatus.Approved && x.Approvals.Any(a => a.Approved && a.ApproverId == UserId), token)
                    ?? throw new InvalidOperationException("An independent approval decision is required before certification.");
                if (ledger.PreparedBy == UserId || workflow.SubmittedBy == UserId)
                    throw new InvalidOperationException("The event preparer cannot certify their own recognition.");
                var policy = await _db.ModifiedCashRecognitionPolicies.SingleOrDefaultAsync(x =>
                    x.Id == ledger.PolicyId && x.CompanyId == CompanyId &&
                    x.Status == ModifiedCashPolicyStatus.Approved, token)
                    ?? throw new InvalidOperationException("The recognition policy is no longer approved.");
                if (ledger.EventKind == ModifiedCashLedgerEventKind.Recognition)
                {
                    var commitment = await _db.Commitments.SingleOrDefaultAsync(x =>
                        x.Id == ledger.CommitmentId && x.CompanyId == CompanyId &&
                        x.Status == CommitmentStatus.Active, token)
                        ?? throw new InvalidOperationException("The source commitment is no longer active.");
                    var receipt = await _db.GoodsReceiptLines.Where(x => x.Id == ledger.SourceEventId)
                        .Join(_db.GoodsReceipts.Where(x => x.CompanyId == CompanyId),
                            line => line.GoodsReceiptId, header => header.Id,
                            (line, header) => new { Line = line, Header = header })
                        .SingleOrDefaultAsync(token);
                    if (receipt == null || receipt.Header.PurchaseOrderId != commitment.PurchaseOrderId ||
                        decimal.Round(receipt.Line.QuantityReceived * receipt.Line.UnitCost, 6,
                            MidpointRounding.AwayFromZero) != ledger.Amount ||
                        receipt.Header.ReceiptDate != ledger.EventDate ||
                        ledger.EvidenceAcceptedAt > policy.ReportingPeriodEnd.AddDays(policy.PostPeriodRecognitionWindowDays))
                        throw new InvalidOperationException("The goods-receipt source or post-period evidence window changed; recognition cannot be certified.");
                    var mapping = await _db.ModifiedCashRecognitionMappings.SingleOrDefaultAsync(x =>
                        x.CompanyId == CompanyId && x.PolicyId == policy.Id &&
                        x.SourceEventKind == ledger.SourceEventKind, token);
                    if (mapping == null || !await _db.PublicSectorBudgetVersionLines.AnyAsync(x =>
                        x.Id == mapping.BudgetVersionLineId && x.CompanyId == CompanyId &&
                        x.BudgetVersionId == policy.BudgetVersionId && x.FundCentreId == commitment.FundCentreId, token))
                        throw new InvalidOperationException("The approved budget classification mapping is no longer valid.");
                    var certified = await _db.ModifiedCashRecognitionLedgerEvents.Where(x =>
                        x.CompanyId == CompanyId && x.CommitmentId == commitment.Id &&
                        x.Status == ModifiedCashLedgerEventStatus.Certified).ToListAsync(token);
                    var recognisedBalance = certified.Sum(x =>
                        x.EventKind == ModifiedCashLedgerEventKind.Recognition ? x.Amount : -x.Amount);
                    if (ledger.Amount > commitment.Amount - commitment.RealisedAmount - recognisedBalance)
                        throw new InvalidOperationException("The live commitment balance no longer supports this recognition.");
                }
                ledger.Status = ModifiedCashLedgerEventStatus.Certified;
                ledger.CertifiedBy = UserId;
                ledger.CertifiedAt = DateTime.UtcNow;
                _audit.Record(CompanyId, UserId, "ModifiedCashRecognitionLedgerEvent.Certified", "PublicSector",
                    nameof(ModifiedCashRecognitionLedgerEvent), ledger.Id,
                    new { Status = ModifiedCashLedgerEventStatus.PendingCertification },
                    new { ledger.Status, ledger.CertifiedBy, ledger.CertifiedAt, ledger.ApprovalWorkflowId });
                await _db.SaveChangesAsync(token);
                return ledger;
            }, ct);
            return Ok(item);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
