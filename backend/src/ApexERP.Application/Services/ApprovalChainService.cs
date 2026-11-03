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
            if (tiers.Count > 5) throw new InvalidOperationException("A maximum of 5 approval tiers is supported.");

            var chain = new ApprovalChain
            {
                CompanyId = companyId,
                Name = name,
                EntityType = entityType,
                AmountThreshold = amountThreshold,
                FundCentreId = fundCentreId,
                Tiers = tiers.OrderBy(t => t.TierOrder).Select(t => new ApprovalChainTier
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
            string referenceTable, Guid submittedBy, decimal amount, Guid? fundCentreId, CancellationToken ct = default)
        {
            var chain = await _db.ApprovalChains.Include(c => c.Tiers)
                .Where(c => c.CompanyId == companyId && c.EntityType == entityType && c.IsActive && c.AmountThreshold <= amount
                            && (c.FundCentreId == null || c.FundCentreId == fundCentreId))
                .OrderByDescending(c => c.AmountThreshold)
                .FirstOrDefaultAsync(ct);

            var request = new WorkflowRequest
            {
                CompanyId = companyId,
                RequestType = entityType,
                ReferenceId = referenceId,
                ReferenceTable = referenceTable,
                SubmittedBy = submittedBy,
                Status = WorkflowStatus.Pending,
                ApprovalChainId = chain?.Id,
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
        public async Task<WorkflowRequest> DecideAsync(Guid companyId, Guid workflowRequestId, Guid approverId, bool approved, string? comments, CancellationToken ct = default)
        {
            var request = await _db.WorkflowRequests.Include(r => r.Approvals)
                .FirstOrDefaultAsync(r => r.Id == workflowRequestId && r.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Workflow request not found.");
            if (request.Status != WorkflowStatus.Pending) throw new InvalidOperationException("This request has already been decided.");

            // ---- Approval integrity (segregation of duties) ----
            if (request.SubmittedBy == approverId)
                throw new InvalidOperationException("You submitted this request, so someone else must decide it.");
            if (request.Approvals.Any(a => a.ApproverId == approverId))
                throw new InvalidOperationException("You have already recorded a decision on this request; a different approver must handle the next tier.");

            var approvalChain = request.ApprovalChainId.HasValue
                ? await _db.ApprovalChains.Include(c => c.Tiers).FirstOrDefaultAsync(c => c.Id == request.ApprovalChainId, ct)
                : null;
            var currentTier = approvalChain?.Tiers.FirstOrDefault(t => t.TierOrder == request.CurrentTier);
            if (currentTier != null)
            {
                // The approver must hold the role the chain requires for this tier.
                var approver = await _db.Users.FirstOrDefaultAsync(u => u.Id == approverId && u.CompanyId == companyId && u.IsActive, ct)
                    ?? throw new InvalidOperationException("Approver not found.");
                if (approver.RoleId != currentTier.ApproverRoleId)
                    throw new InvalidOperationException($"Tier {request.CurrentTier} must be decided by a user holding the required approver role.");
            }
            else if (request.CurrentApproverId.HasValue && request.CurrentApproverId.Value != approverId)
            {
                throw new InvalidOperationException("This request is assigned to a different approver.");
            }

            request.Approvals.Add(new WorkflowApproval
            {
                WorkflowRequestId = request.Id,
                Tier = request.CurrentTier,
                ApproverId = approverId,
                Approved = approved,
                Comments = comments,
                DecidedAt = DateTime.UtcNow
            });

            if (!approved)
            {
                request.Status = WorkflowStatus.Rejected;
                await _db.SaveChangesAsync(ct);
                return request;
            }

            var maxTier = approvalChain?.Tiers.Count ?? 1;

            if (request.CurrentTier >= maxTier)
            {
                request.Status = WorkflowStatus.Approved;
            }
            else
            {
                request.CurrentTier += 1;
                request.CurrentTierStartedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            return request;
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
