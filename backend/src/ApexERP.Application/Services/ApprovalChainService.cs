using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>PRD Module 16 ("multi-level approval routing... up to 5 tiers... escalation on SLA
    /// breach"), applied generically to any entity type. Actual SLA-breach escalation requires a
    /// background scheduler (Hangfire) to periodically check CurrentTierStartedAt against each
    /// tier's SlaHours -- that scheduled job isn't wired in this pass (see README), but the data
    /// model and the tier-advance/escalation-check logic itself are real and callable on demand
    /// (e.g. from a manual "check for overdue approvals" endpoint, or once a scheduler is added).</summary>
    public class ApprovalChainService
    {
        private readonly ApexErpDbContext _db;

        public ApprovalChainService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<ApprovalChain> CreateChainAsync(Guid companyId, string name, string entityType, decimal amountThreshold,
            Guid? fundCentreId, List<(int TierOrder, Guid ApproverRoleId, int SlaHours)> tiers, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(entityType))
                throw new InvalidOperationException("An approval-chain name and entity type are required.");
            if (amountThreshold < 0m)
                throw new InvalidOperationException("An approval threshold cannot be negative.");
            if (tiers == null || tiers.Count is < 1 or > 5)
                throw new InvalidOperationException("An approval chain must contain between one and five tiers.");
            var orderedTiers = tiers.OrderBy(t => t.TierOrder).ToList();
            if (orderedTiers.Select(t => t.TierOrder).Where((order, index) => order != index + 1).Any() ||
                orderedTiers.Any(t => t.SlaHours <= 0))
                throw new InvalidOperationException("Approval tiers must be sequentially numbered from 1 and have a positive SLA.");
            var roleIds = orderedTiers.Select(t => t.ApproverRoleId).Distinct().ToList();
            var validRoleCount = await _db.Roles.CountAsync(r => r.CompanyId == companyId && roleIds.Contains(r.Id), ct);
            if (validRoleCount != roleIds.Count)
                throw new InvalidOperationException("Every approval tier must reference a role in this company.");
            if (fundCentreId.HasValue && !await _db.FundCentres.AnyAsync(f => f.CompanyId == companyId && f.Id == fundCentreId.Value, ct))
                throw new InvalidOperationException("The approval chain fund centre was not found in this company.");

            var chain = new ApprovalChain
            {
                CompanyId = companyId,
                Name = name,
                EntityType = entityType,
                AmountThreshold = amountThreshold,
                FundCentreId = fundCentreId,
                Tiers = orderedTiers.Select(t => new ApprovalChainTier
                {
                    TierOrder = t.TierOrder,
                    ApproverRoleId = t.ApproverRoleId,
                    SlaHours = t.SlaHours
                }).ToList()
            };
            _db.ApprovalChains.Add(chain);
            await _db.SaveChangesAsync(ct);
            return chain;
        }

        /// <summary>Finds the applicable chain for an entity type + amount (the highest threshold
        /// at or below the amount) and starts a WorkflowRequest routed through its first tier.</summary>
        public async Task<WorkflowRequest> SubmitForApprovalAsync(Guid companyId, string entityType, Guid referenceId,
            string referenceTable, Guid submittedBy, decimal amount, Guid? fundCentreId, string? notes = null, CancellationToken ct = default)
        {
            var chain = await _db.ApprovalChains.Include(c => c.Tiers)
                .Where(c => c.CompanyId == companyId && c.EntityType == entityType && c.IsActive && c.AmountThreshold <= amount
                            && (c.FundCentreId == null || c.FundCentreId == fundCentreId))
                .OrderByDescending(c => c.AmountThreshold)
                .FirstOrDefaultAsync(ct);

            if (chain == null || chain.Tiers.Count == 0)
                throw new InvalidOperationException($"No active approval chain is configured for {entityType} at amount {amount:N2}. Configure an approval chain before submitting this request.");

            var request = new WorkflowRequest
            {
                CompanyId = companyId,
                RequestType = entityType,
                ReferenceId = referenceId,
                ReferenceTable = referenceTable,
                SubmittedBy = submittedBy,
                Amount = amount,
                FundCentreId = fundCentreId,
                Status = WorkflowStatus.Pending,
                ApprovalChainId = chain.Id,
                Notes = notes,
                CurrentTier = 1,
                CurrentTierStartedAt = DateTime.UtcNow
            };
            _db.WorkflowRequests.Add(request);
            await _db.SaveChangesAsync(ct);
            return request;
        }

        /// <summary>Records one tier's decision. On approval, advances to the next configured tier
        /// (or marks the whole request Approved if this was the last tier). On rejection, the whole
        /// request is Rejected immediately regardless of tier.</summary>
        public Task<WorkflowRequest> DecideAsync(Guid companyId, Guid workflowRequestId, Guid approverId,
            bool approved, string? comments, CancellationToken ct = default)
        {
            return _db.InTransactionAsync(async token =>
            {
                await _db.AcquireTransactionLockAsync($"workflow-request:{companyId:N}:{workflowRequestId:N}", token);
                var request = await _db.WorkflowRequests.Include(r => r.Approvals)
                    .FirstOrDefaultAsync(r => r.Id == workflowRequestId && r.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("Workflow request not found.");
                if (request.Status != WorkflowStatus.Pending)
                    throw new InvalidOperationException("This request has already been decided.");

                if (request.SubmittedBy == approverId)
                    throw new InvalidOperationException("You submitted this request, so someone else must decide it.");
                if (request.Approvals.Any(a => a.ApproverId == approverId))
                    throw new InvalidOperationException("You have already recorded a decision on this request; a different approver must handle the next tier.");

                if (!request.ApprovalChainId.HasValue)
                    throw new InvalidOperationException("This multi-tier request has no approval chain. Use the assigned single-approver workflow for unchained requests.");
                var approvalChain = await _db.ApprovalChains.Include(c => c.Tiers)
                    .FirstOrDefaultAsync(c => c.Id == request.ApprovalChainId.Value && c.CompanyId == companyId, token)
                    ?? throw new InvalidOperationException("The configured approval chain is no longer available.");
                var currentTier = approvalChain.Tiers.FirstOrDefault(t => t.TierOrder == request.CurrentTier)
                    ?? throw new InvalidOperationException("The current approval tier is missing from its configured chain.");

                var approver = await _db.Users.FirstOrDefaultAsync(u => u.Id == approverId && u.CompanyId == companyId && u.IsActive, token)
                    ?? throw new InvalidOperationException("Approver not found.");
                if (approver.RoleId != currentTier.ApproverRoleId)
                    throw new InvalidOperationException($"Tier {request.CurrentTier} must be decided by a user holding the required approver role.");

                _db.WorkflowApprovals.Add(new WorkflowApproval
                {
                    CompanyId = companyId,
                    WorkflowRequestId = request.Id,
                    Tier = request.CurrentTier,
                    ApproverId = approverId,
                    Approved = approved,
                    Comments = comments,
                    DecidedAt = DateTime.UtcNow
                });
                // Persist the immutable decision first, then atomically advance the request row.
                // This avoids EF attempting a stale aggregate update when the approval collection
                // and request status are changed in the same batch.
                await _db.SaveChangesAsync(token);

                var isFinalTier = !approved || request.CurrentTier >= approvalChain.Tiers.Count;
                var nextStatus = !approved ? WorkflowStatus.Rejected :
                    isFinalTier ? WorkflowStatus.Approved : WorkflowStatus.Pending;
                var nextTier = isFinalTier ? request.CurrentTier : request.CurrentTier + 1;
                var nextStartedAt = isFinalTier ? request.CurrentTierStartedAt : DateTime.UtcNow;
                var affected = await _db.WorkflowRequests
                    .Where(w => w.Id == workflowRequestId && w.CompanyId == companyId && w.Status == WorkflowStatus.Pending)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(w => w.Status, nextStatus)
                        .SetProperty(w => w.CurrentTier, nextTier)
                        .SetProperty(w => w.CurrentTierStartedAt, nextStartedAt), token);
                if (affected != 1)
                    throw new InvalidOperationException("The workflow request changed while this decision was being recorded; reload and retry.");

                await _db.Entry(request).ReloadAsync(token);
                return request;
            }, ct);
        }

        /// <summary>Returns every pending request whose current tier has exceeded its configured
        /// SLA -- call this periodically (or from a scheduled job once one is wired up) to drive
        /// escalation notifications.</summary>
        public async Task<List<WorkflowRequest>> GetOverdueRequestsAsync(Guid companyId, CancellationToken ct = default)
        {
            var pending = await _db.WorkflowRequests
                .Where(r => r.CompanyId == companyId && r.Status == WorkflowStatus.Pending && r.ApprovalChainId != null)
                .ToListAsync(ct);

            var chainIds = pending.Select(r => r.ApprovalChainId!.Value).Distinct().ToList();
            var chains = await _db.ApprovalChains.Include(c => c.Tiers).Where(c => chainIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

            return pending.Where(r =>
            {
                if (!chains.TryGetValue(r.ApprovalChainId!.Value, out var chain)) return false;
                var tier = chain.Tiers.FirstOrDefault(t => t.TierOrder == r.CurrentTier);
                if (tier == null || !r.CurrentTierStartedAt.HasValue) return false;
                return DateTime.UtcNow - r.CurrentTierStartedAt.Value > TimeSpan.FromHours(tier.SlaHours);
            }).ToList();
        }
    }
}
