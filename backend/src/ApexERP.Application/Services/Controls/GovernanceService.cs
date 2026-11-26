using System.Globalization;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>Typed per-company settings (tolerances, thresholds, switches). Missing keys fall back to the supplied default.</summary>
    public class SettingsService
    {
        private readonly ApexErpDbContext _db;
        public SettingsService(ApexErpDbContext db) { _db = db; }

        public async Task<string?> GetAsync(Guid companyId, string key, CancellationToken ct = default) =>
            (await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct))?.Value;

        public async Task<decimal> GetDecimalAsync(Guid companyId, string key, decimal fallback, CancellationToken ct = default) =>
            decimal.TryParse(await GetAsync(companyId, key, ct), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public async Task<bool> GetBoolAsync(Guid companyId, string key, bool fallback, CancellationToken ct = default) =>
            bool.TryParse(await GetAsync(companyId, key, ct), out var v) ? v : fallback;

        public async Task SetAsync(Guid companyId, string key, string value, Guid? userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("A setting key is required.");
            var row = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == key, ct);
            if (row == null) _db.CompanySettings.Add(new CompanySetting { CompanyId = companyId, Key = key, Value = value, CreatedBy = userId });
            else { row.Value = value; row.UpdatedBy = userId; row.UpdatedAt = DateTime.UtcNow; }
            await _db.SaveChangesAsync(ct);
        }

        public Task<List<CompanySetting>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            _db.CompanySettings.Where(s => s.CompanyId == companyId).OrderBy(s => s.Key).ToListAsync(ct);

        // Well-known keys
        public const string AllowSelfApproval = "controls.allowSelfApproval";
        public const string CapitalisationThreshold = "assets.capitalisationThreshold";
        public const string ThreeWayQtyTolerancePct = "ap.match.quantityTolerancePct";
        public const string ThreeWayPriceTolerancePct = "ap.match.priceTolerancePct";
        public const string WhtDefaultRatePct = "ap.wht.defaultRatePct";
        public const string PaymentRunAuthorityEntityType = "ApPayment";
    }

    public sealed record ApprovalDecision(bool Allowed, string? Reason = null, Guid? ViaDelegationFrom = null);
    public sealed record SoDFinding(string PermissionA, string PermissionB, string Reason);

    /// <summary>
    /// Segregation of duties, delegation of authority and the approval matrix (spec 7, 8). Pure rules live in
    /// ApexERP.Domain.Controls; this class supplies the tenant's configuration and records the audit trail.
    /// </summary>
    public class GovernanceService
    {
        private readonly ApexErpDbContext _db;
        private readonly AuditService _audit;
        private readonly SettingsService _settings;

        public GovernanceService(ApexErpDbContext db, AuditService audit, SettingsService settings)
        {
            _db = db; _audit = audit; _settings = settings;
        }

        // ---------------------------------------------------------------------------------------------
        // Segregation of duties (7.3)
        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<SoDFinding>> DetectConflictsAsync(Guid companyId, IEnumerable<string> permissions, CancellationToken ct = default)
        {
            var tenantRules = await _db.SoDConflictRules.Where(r => r.CompanyId == companyId && r.IsActive).ToListAsync(ct);
            var rules = SegregationOfDuties.DefaultRules
                .Concat(tenantRules.Select(r => new SoDConflict(r.PermissionA, r.PermissionB, r.Reason))).ToList();
            return SegregationOfDuties.Detect(permissions, rules).Select(c => new SoDFinding(c.PermissionA, c.PermissionB, c.Reason)).ToList();
        }

        /// <summary>Who currently holds conflicting access. Output for the access-recertification review (7.4).</summary>
        public async Task<List<object>> ConflictReportAsync(Guid companyId, CancellationToken ct = default)
        {
            var users = await _db.Users.Include(u => u.Role).Where(u => u.CompanyId == companyId && u.IsActive).ToListAsync(ct);
            var report = new List<object>();
            foreach (var u in users)
            {
                var perms = ApexERP.Application.Security.PermissionEvaluator.Parse(u.Role?.PermissionsJson);
                var conflicts = await DetectConflictsAsync(companyId, perms, ct);
                if (conflicts.Count > 0)
                    report.Add(new { u.Id, u.Email, Role = u.Role?.Name, conflicts });
            }
            return report;
        }

        public async Task<SoDConflictRule> AddRuleAsync(Guid companyId, string a, string b, string reason, Guid? userId, CancellationToken ct = default)
        {
            if (!ApexERP.Application.Security.Permissions.IsKnown(a) || !ApexERP.Application.Security.Permissions.IsKnown(b))
                throw new InvalidOperationException("Both permissions must be known permission keys.");
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A rule needs two different permissions.");
            var rule = new SoDConflictRule { CompanyId = companyId, PermissionA = a.Trim(), PermissionB = b.Trim(), Reason = reason, CreatedBy = userId };
            _db.SoDConflictRules.Add(rule);
            _audit.Record(companyId, userId, "SoD.RuleAdded", "Governance", nameof(SoDConflictRule), rule.Id, null, new { a, b, reason });
            await _db.SaveChangesAsync(ct);
            return rule;
        }

        // ---------------------------------------------------------------------------------------------
        // Delegation and emergency access (7.4)
        // ---------------------------------------------------------------------------------------------
        public async Task<ApprovalDelegation> CreateDelegationAsync(Guid companyId, Guid delegatorId, Guid delegateId, string? entityType,
            decimal? maxAmount, DateTime validFrom, DateTime validTo, string reason, bool isEmergency, Guid? actingUserId, CancellationToken ct = default)
        {
            if (delegatorId == delegateId) throw new InvalidOperationException("You cannot delegate to yourself.");
            if (validTo <= validFrom) throw new InvalidOperationException("The delegation must end after it starts.");
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required.");
            var maxDays = isEmergency ? 3 : 90;
            if ((validTo - validFrom).TotalDays > maxDays)
                throw new InvalidOperationException($"{(isEmergency ? "Emergency access" : "A delegation")} may last at most {maxDays} days.");
            if (maxAmount is < 0) throw new InvalidOperationException("The limit cannot be negative.");

            var users = await _db.Users.Where(u => u.CompanyId == companyId && (u.Id == delegatorId || u.Id == delegateId) && u.IsActive).CountAsync(ct);
            if (users != 2) throw new InvalidOperationException("Both users must be active users of this company.");

            var d = new ApprovalDelegation
            {
                CompanyId = companyId, DelegatorUserId = delegatorId, DelegateUserId = delegateId, EntityType = entityType,
                MaxAmount = maxAmount, ValidFrom = validFrom, ValidTo = validTo, Reason = reason, IsEmergency = isEmergency, CreatedBy = actingUserId
            };
            _db.ApprovalDelegations.Add(d);
            _audit.Record(companyId, actingUserId, isEmergency ? "Delegation.EmergencyGranted" : "Delegation.Granted", "Governance",
                nameof(ApprovalDelegation), d.Id, null, new { delegatorId, delegateId, entityType, maxAmount, validFrom, validTo, reason });
            await _db.SaveChangesAsync(ct);
            return d;
        }

        public async Task RevokeDelegationAsync(Guid companyId, Guid id, Guid? userId, CancellationToken ct = default)
        {
            var d = await _db.ApprovalDelegations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Delegation not found.");
            if (d.RevokedAt != null) return;
            d.RevokedAt = DateTime.UtcNow; d.UpdatedBy = userId; d.UpdatedAt = DateTime.UtcNow;
            _audit.Record(companyId, userId, "Delegation.Revoked", "Governance", nameof(ApprovalDelegation), d.Id, null, new { d.DelegatorUserId, d.DelegateUserId });
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Emergency access must be reviewed by someone other than the person who used it.</summary>
        public async Task ReviewEmergencyAsync(Guid companyId, Guid id, Guid reviewerId, CancellationToken ct = default)
        {
            var d = await _db.ApprovalDelegations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && x.IsEmergency, ct)
                ?? throw new InvalidOperationException("Emergency delegation not found.");
            if (reviewerId == d.DelegateUserId) throw new InvalidOperationException("The person who used emergency access cannot review it.");
            d.ReviewedBy = reviewerId; d.ReviewedAt = DateTime.UtcNow;
            _audit.Record(companyId, reviewerId, "Delegation.EmergencyReviewed", "Governance", nameof(ApprovalDelegation), d.Id, null, new { d.DelegateUserId });
            await _db.SaveChangesAsync(ct);
        }

        public Task<List<ApprovalDelegation>> ListDelegationsAsync(Guid companyId, bool activeOnly, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var q = _db.ApprovalDelegations.Where(d => d.CompanyId == companyId);
            if (activeOnly) q = q.Where(d => d.RevokedAt == null && d.ValidFrom <= now && d.ValidTo >= now);
            return q.OrderByDescending(d => d.ValidFrom).Take(500).ToListAsync(ct);
        }

        // ---------------------------------------------------------------------------------------------
        // Approval matrix (8.3 - 8.6)
        // ---------------------------------------------------------------------------------------------
        public async Task<List<ApprovalMatrixTier>> GetTiersAsync(Guid companyId, string entityType, CancellationToken ct = default) =>
            await _db.ApprovalMatrixTiers.Where(t => t.CompanyId == companyId && t.EntityType == entityType && t.IsActive)
                .OrderBy(t => t.UpToAmount).ToListAsync(ct);

        public async Task<List<ApprovalMatrixTier>> ReplaceTiersAsync(Guid companyId, string entityType, IEnumerable<ApprovalMatrixTier> tiers, Guid? userId, CancellationToken ct = default)
        {
            var list = tiers.OrderBy(t => t.UpToAmount).ToList();
            if (list.Count == 0) throw new InvalidOperationException("At least one tier is required.");
            if (list.Any(t => string.IsNullOrWhiteSpace(t.RequiredRoleName))) throw new InvalidOperationException("Every tier needs a role.");
            if (list.Select(t => t.UpToAmount).Distinct().Count() != list.Count) throw new InvalidOperationException("Tier limits must be distinct.");
            var roleNames = await _db.Roles.Where(r => r.CompanyId == companyId).Select(r => r.Name).ToListAsync(ct);
            var unknown = list.Select(t => t.RequiredRoleName).Where(n => !roleNames.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            if (unknown.Count > 0) throw new InvalidOperationException("Unknown role(s): " + string.Join(", ", unknown));

            var old = await _db.ApprovalMatrixTiers.Where(t => t.CompanyId == companyId && t.EntityType == entityType).ToListAsync(ct);
            _db.ApprovalMatrixTiers.RemoveRange(old);
            foreach (var t in list)
                _db.ApprovalMatrixTiers.Add(new ApprovalMatrixTier
                {
                    CompanyId = companyId, EntityType = entityType, UpToAmount = t.UpToAmount, RequiredRoleName = t.RequiredRoleName.Trim(),
                    SlaHours = t.SlaHours <= 0 ? 24 : t.SlaHours, EscalateToRoleName = t.EscalateToRoleName, CreatedBy = userId
                });
            _audit.Record(companyId, userId, "ApprovalMatrix.Replaced", "Governance", nameof(ApprovalMatrixTier), Guid.Empty,
                old.Select(o => new { o.UpToAmount, o.RequiredRoleName }), list.Select(o => new { o.UpToAmount, o.RequiredRoleName }));
            await _db.SaveChangesAsync(ct);
            return await GetTiersAsync(companyId, entityType, ct);
        }

        /// <summary>
        /// May this user approve this transaction? Checks (1) no self-approval, (2) authority for the amount from the configured
        /// matrix (when no matrix exists for the type the other controls still apply), (3) otherwise an active delegation from
        /// someone who has that authority. Material-field changes after approval are handled by <see cref="ApprovalMatrix.ApprovalInvalidated"/>.
        /// </summary>
        public async Task<ApprovalDecision> CheckApprovalAuthorityAsync(Guid companyId, Guid approverId, string entityType, decimal amount,
            Guid? preparedBy, CancellationToken ct = default)
        {
            var allowSelf = await _settings.GetBoolAsync(companyId, SettingsService.AllowSelfApproval, false, ct);
            if (!SegregationOfDuties.MayApprove(preparedBy, approverId, allowSelf))
                return new ApprovalDecision(false, "The person who prepared this cannot approve it.");

            var tiers = await GetTiersAsync(companyId, entityType, ct);
            if (tiers.Count == 0) return new ApprovalDecision(true);

            var domainTiers = tiers.Select(t => new ApprovalTier(t.UpToAmount, t.RequiredRoleName)).ToList();
            var eligible = ApprovalMatrix.EligibleRoles(domainTiers, Math.Abs(amount));
            if (eligible.Count == 0)
                return new ApprovalDecision(false, "No approval tier covers this amount. Add a catch-all tier.");

            var approver = await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == approverId && u.CompanyId == companyId && u.IsActive, ct);
            if (approver == null) return new ApprovalDecision(false, "Approver not found or inactive.");
            if (approver.Role != null && eligible.Contains(approver.Role.Name, StringComparer.OrdinalIgnoreCase))
                return new ApprovalDecision(true);

            var now = DateTime.UtcNow;
            var delegations = await _db.ApprovalDelegations.Where(d => d.CompanyId == companyId && d.DelegateUserId == approverId
                && d.RevokedAt == null && d.ValidFrom <= now && d.ValidTo >= now
                && (d.EntityType == null || d.EntityType == entityType)
                && (d.MaxAmount == null || d.MaxAmount >= Math.Abs(amount))).ToListAsync(ct);
            foreach (var d in delegations)
            {
                var delegator = await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == d.DelegatorUserId && u.CompanyId == companyId && u.IsActive, ct);
                if (delegator?.Role != null && eligible.Contains(delegator.Role.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _audit.Record(companyId, approverId, d.IsEmergency ? "Approval.UsedEmergencyAccess" : "Approval.UsedDelegation", "Governance",
                        nameof(ApprovalDelegation), d.Id, null, new { entityType, amount, d.DelegatorUserId });
                    return new ApprovalDecision(true, null, d.DelegatorUserId);
                }
            }
            return new ApprovalDecision(false, $"Authority for this amount requires one of: {string.Join(", ", eligible)}.");
        }

        // ---------------------------------------------------------------------------------------------
        // Materiality (9.1)
        // ---------------------------------------------------------------------------------------------
        public async Task<MaterialityThresholds> GetActiveMaterialityAsync(Guid companyId, CancellationToken ct = default)
        {
            var m = await _db.MaterialityConfigurations.Where(x => x.CompanyId == companyId && x.IsActive).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            return m == null ? new MaterialityThresholds(1000m, 10000m, 100000m) : new MaterialityThresholds(m.MediumThreshold, m.HighThreshold, m.CriticalThreshold);
        }

        public async Task<MaterialityConfiguration> ProposeMaterialityAsync(Guid companyId, decimal medium, decimal high, decimal critical, string currency, Guid? userId, CancellationToken ct = default)
        {
            if (!(0 < medium && medium < high && high < critical)) throw new InvalidOperationException("Thresholds must satisfy 0 < medium < high < critical.");
            var last = await _db.MaterialityConfigurations.Where(x => x.CompanyId == companyId).MaxAsync(x => (int?)x.Version, ct) ?? 0;
            var cfg = new MaterialityConfiguration { CompanyId = companyId, Version = last + 1, Currency = currency, MediumThreshold = medium, HighThreshold = high, CriticalThreshold = critical, CreatedBy = userId, IsActive = false };
            _db.MaterialityConfigurations.Add(cfg);
            _audit.Record(companyId, userId, "Materiality.Proposed", "Governance", nameof(MaterialityConfiguration), cfg.Id, null, new { cfg.Version, medium, high, critical });
            await _db.SaveChangesAsync(ct);
            return cfg;
        }

        public async Task<MaterialityConfiguration> ApproveMaterialityAsync(Guid companyId, Guid id, Guid approverId, CancellationToken ct = default)
        {
            var cfg = await _db.MaterialityConfigurations.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
                ?? throw new InvalidOperationException("Materiality configuration not found.");
            if (cfg.IsActive) return cfg;
            if (cfg.CreatedBy == approverId) throw new InvalidOperationException("Thresholds must be approved by someone other than the proposer.");
            foreach (var other in await _db.MaterialityConfigurations.Where(x => x.CompanyId == companyId && x.IsActive).ToListAsync(ct)) other.IsActive = false;
            cfg.IsActive = true; cfg.ApprovedBy = approverId; cfg.ApprovedAt = DateTime.UtcNow; cfg.EffectiveFrom = DateTime.UtcNow;
            _audit.Record(companyId, approverId, "Materiality.Approved", "Governance", nameof(MaterialityConfiguration), cfg.Id, null, new { cfg.Version });
            await _db.SaveChangesAsync(ct);
            return cfg;
        }

        public Task<List<MaterialityConfiguration>> ListMaterialityAsync(Guid companyId, CancellationToken ct = default) =>
            _db.MaterialityConfigurations.Where(x => x.CompanyId == companyId).OrderByDescending(x => x.Version).ToListAsync(ct);

        // ---------------------------------------------------------------------------------------------
        // Accounting policy versions (13.5)
        // ---------------------------------------------------------------------------------------------
        public async Task<AccountingPolicyVersion> ProposePolicyAsync(Guid companyId, string area, string description, DateTime effectiveFrom, Guid? userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("Area and description are required.");
            var last = await _db.AccountingPolicyVersions.Where(p => p.CompanyId == companyId && p.Area == area).MaxAsync(p => (int?)p.Version, ct) ?? 0;
            var p = new AccountingPolicyVersion { CompanyId = companyId, Area = area, Description = description, EffectiveFrom = effectiveFrom, Version = last + 1, CreatedBy = userId };
            _db.AccountingPolicyVersions.Add(p);
            _audit.Record(companyId, userId, "Policy.Proposed", "Governance", nameof(AccountingPolicyVersion), p.Id, null, new { area, p.Version });
            await _db.SaveChangesAsync(ct);
            return p;
        }

        public async Task<AccountingPolicyVersion> ApprovePolicyAsync(Guid companyId, Guid id, Guid approverId, CancellationToken ct = default)
        {
            var p = await _db.AccountingPolicyVersions.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Policy version not found.");
            if (p.CreatedBy == approverId) throw new InvalidOperationException("A policy must be approved by someone other than its author.");
            foreach (var o in await _db.AccountingPolicyVersions.Where(x => x.CompanyId == companyId && x.Area == p.Area && x.IsActive).ToListAsync(ct)) o.IsActive = false;
            p.IsActive = true; p.ApprovedBy = approverId; p.ApprovedAt = DateTime.UtcNow;
            _audit.Record(companyId, approverId, "Policy.Approved", "Governance", nameof(AccountingPolicyVersion), p.Id, null, new { p.Area, p.Version });
            await _db.SaveChangesAsync(ct);
            return p;
        }

        /// <summary>The policy versions in force on a date, stamped onto reports so a comparative can be reproduced (13.5).</summary>
        public async Task<List<object>> PolicyStampAsync(Guid companyId, DateTime asOf, CancellationToken ct = default) =>
            (await _db.AccountingPolicyVersions.Where(p => p.CompanyId == companyId && p.ApprovedAt != null && p.EffectiveFrom <= asOf).ToListAsync(ct))
                .GroupBy(p => p.Area).Select(g => g.OrderByDescending(p => p.Version).First())
                .Select(p => (object)new { p.Area, p.Version, p.EffectiveFrom }).ToList();
    }
}
