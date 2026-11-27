using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record ConsolidatedAccountLine(string Code, string Name, GlAccountType Type, decimal GroupAmount, decimal EliminatedAmount, decimal ConsolidatedAmount);
    public record ConsolidationResult(
        Guid ParentCompanyId, List<Guid> SubsidiaryCompanyIds, DateTime AsOfDate,
        List<ConsolidatedAccountLine> Lines,
        decimal TotalAssets, decimal TotalLiabilities, decimal TotalGroupEquity, decimal TotalNonControllingInterest,
        decimal TotalEliminations);

    /// <summary>
    /// PRD Module 2 ("As a Group Accountant, I want to consolidate the GL of multiple companies
    /// with automatic elimination entries... and calculate minority interest"). Consolidation is
    /// computed as a presentation-layer worksheet -- it does not post anything into any single
    /// legal entity's own GL, which is the correct accounting treatment: consolidated financial
    /// statements are prepared FOR the group, not posted INTO any one entity's books.
    /// </summary>
    public class ConsolidationService
    {
        private readonly ApexErpDbContext _db;
        private readonly ReportingService _reporting;
        private readonly GroupScopeService _scope;

        public ConsolidationService(ApexErpDbContext db, ReportingService reporting, GroupScopeService scope)
        {
            _db = db;
            _reporting = reporting;
            _scope = scope;
        }

        /// <summary>Returns every company in the group rooted at parentCompanyId (parent + all
        /// direct and indirect subsidiaries), each with its ownership percentage from its immediate parent.</summary>
        public async Task<List<Company>> GetGroupStructureAsync(Guid parentCompanyId, CancellationToken ct = default)
        {
            var callerCompanyId = _db.CurrentCompanyId
                ?? throw new InvalidOperationException("A company context is required for group consolidation.");
            var visibleCompanyIds = await _scope.GetVisibleCompanyIdsAsync(callerCompanyId, ct);
            if (!visibleCompanyIds.Contains(parentCompanyId))
                throw new InvalidOperationException("Parent company is outside the caller's tenant-scoped group.");

            // Keep the group graph tenant-scoped. Cross-company financial reads are opened only
            // after this allow-list has been established in ConsolidateAsync.
            var all = await _db.Companies
                .Where(c => visibleCompanyIds.Contains(c.Id))
                .ToListAsync(ct);
            var group = new List<Company>();
            void AddChildren(Guid id)
            {
                var children = all.Where(c => c.ParentCompanyId == id).ToList();
                foreach (var child in children)
                {
                    group.Add(child);
                    AddChildren(child.Id);
                }
            }
            var parent = all.FirstOrDefault(c => c.Id == parentCompanyId)
                ?? throw new InvalidOperationException("Parent company not found.");
            group.Add(parent);
            AddChildren(parentCompanyId);
            return group;
        }

        public async Task<ConsolidationResult> ConsolidateAsync(Guid parentCompanyId, DateTime asOfDate, CancellationToken ct = default)
        {
            var group = await GetGroupStructureAsync(parentCompanyId, ct);
            if (group.Count == 0)
                throw new InvalidOperationException("The requested group is empty.");
            var subsidiaries = group.Where(c => c.Id != parentCompanyId).ToList();

            // Financial rows are read across legal companies only after the tenant-scoped group
            // allow-list has been established. Every query below retains an explicit CompanyId predicate.
            using var crossTenant = _db.CrossTenantScope();

            // Aggregate each entity's trial balance line-by-line (by account code, since a well-run
            // group typically shares a common CoA structure across entities; accounts unique to one
            // entity simply appear with that entity's amount only).
            var aggregated = new Dictionary<string, ConsolidatedAccountLine>();
            var accountsById = new Dictionary<Guid, GlAccount>();

            foreach (var company in group)
            {
                var tb = await GetTrialBalanceIgnoringFiltersAsync(company.Id, asOfDate, ct);
                var companyAccounts = await _db.GlAccounts.IgnoreQueryFilters().Where(a => a.CompanyId == company.Id).ToListAsync(ct);
                foreach (var a in companyAccounts) accountsById[a.Id] = a;

                // Ownership adjustment: for a subsidiary, only the parent's share of equity rolls
                // into "Total Group Equity"; the rest becomes Non-Controlling Interest (computed below).
                foreach (var line in tb)
                {
                    var net = line.Debit - line.Credit; // net debit balance, consistent sign convention
                    if (!aggregated.TryGetValue(line.Code, out var existing))
                    {
                        aggregated[line.Code] = new ConsolidatedAccountLine(line.Code, line.Name, line.Type, net, 0, net);
                    }
                    else
                    {
                        aggregated[line.Code] = existing with { GroupAmount = existing.GroupAmount + net, ConsolidatedAmount = existing.ConsolidatedAmount + net };
                    }
                }
            }

            // ---- Eliminate inter-company balances between entities in this consolidation set ----
            var groupIds = group.Select(c => c.Id).ToList();
            var intercoAccounts = accountsById.Values.Where(a => a.IsIntercompanyAccount && groupIds.Contains(a.CompanyId)
                                                                   && a.IntercompanyCounterpartyCompanyId.HasValue
                                                                   && groupIds.Contains(a.IntercompanyCounterpartyCompanyId.Value)).ToList();
            var companiesById = group.ToDictionary(c => c.Id);
            var eliminatedAccountIds = new HashSet<Guid>();
            var accountBalances = new Dictionary<Guid, decimal>();
            decimal totalEliminations = 0;

            // Never eliminate a one-sided or unmatched intercompany balance. A valid elimination
            // requires a reciprocal account, the same functional currency, and equal/opposite
            // posted balances as at the reporting date. This prevents an unmatched receivable or
            // payable from disappearing from the consolidated statements.
            foreach (var account in intercoAccounts.OrderBy(a => a.CompanyId).ThenBy(a => a.Code))
            {
                if (eliminatedAccountIds.Contains(account.Id) ||
                    !companiesById.TryGetValue(account.CompanyId, out var owner) ||
                    !companiesById.TryGetValue(account.IntercompanyCounterpartyCompanyId!.Value, out var counterparty) ||
                    !string.Equals(owner.FunctionalCurrency, counterparty.FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
                    continue;

                var reciprocal = intercoAccounts.FirstOrDefault(candidate =>
                    candidate.Id != account.Id &&
                    candidate.CompanyId == account.IntercompanyCounterpartyCompanyId.Value &&
                    candidate.IntercompanyCounterpartyCompanyId == account.CompanyId &&
                    string.Equals(candidate.Code, account.Code, StringComparison.OrdinalIgnoreCase));
                if (reciprocal == null || eliminatedAccountIds.Contains(reciprocal.Id))
                    continue;

                if (!accountBalances.TryGetValue(account.Id, out var accountBalance))
                {
                    accountBalance = await GetAccountBalanceAsync(account.Id, account.CompanyId, asOfDate, ct);
                    accountBalances[account.Id] = accountBalance;
                }
                if (!accountBalances.TryGetValue(reciprocal.Id, out var reciprocalBalance))
                {
                    reciprocalBalance = await GetAccountBalanceAsync(reciprocal.Id, reciprocal.CompanyId, asOfDate, ct);
                    accountBalances[reciprocal.Id] = reciprocalBalance;
                }

                if (!AreReciprocalBalancesMatched(accountBalance, reciprocalBalance))
                    continue;

                foreach (var matched in new[] { (Account: account, Balance: accountBalance), (Account: reciprocal, Balance: reciprocalBalance) })
                {
                    if (aggregated.TryGetValue(matched.Account.Code, out var line))
                    {
                        var elimination = -matched.Balance;
                        aggregated[matched.Account.Code] = line with
                        {
                            EliminatedAmount = line.EliminatedAmount + elimination,
                            ConsolidatedAmount = line.ConsolidatedAmount + elimination
                        };
                    }
                }

                eliminatedAccountIds.Add(account.Id);
                eliminatedAccountIds.Add(reciprocal.Id);
                totalEliminations += Math.Abs(accountBalance);
            }

            var lines = aggregated.Values.OrderBy(l => l.Code).ToList();
            var totalAssets = lines.Where(l => l.Type == GlAccountType.Asset).Sum(l => l.ConsolidatedAmount);
            var totalLiabilities = lines.Where(l => l.Type == GlAccountType.Liability).Sum(l => -l.ConsolidatedAmount);

            // ---- Non-controlling interest: for each <100%-owned subsidiary, split its net equity ----
            decimal totalNci = 0, totalGroupEquityAdj = 0;
            foreach (var sub in subsidiaries.Where(s => s.OwnershipPercentage.HasValue && s.OwnershipPercentage < 100))
            {
                var subEquity = await GetNetEquityAsync(sub.Id, asOfDate, ct);
                var ownership = sub.OwnershipPercentage!.Value / 100m;
                var nciShare = subEquity * (1 - ownership);
                totalNci += nciShare;
                totalGroupEquityAdj -= nciShare; // remove NCI's share from the raw aggregated equity total
            }

            var rawEquity = lines.Where(l => l.Type == GlAccountType.Equity).Sum(l => -l.ConsolidatedAmount);
            var totalGroupEquity = rawEquity + totalGroupEquityAdj;

            return new ConsolidationResult(parentCompanyId, subsidiaries.Select(s => s.Id).ToList(), asOfDate,
                lines, totalAssets, totalLiabilities, totalGroupEquity, totalNci, totalEliminations);
        }

        /// <summary>Returns the signed functional-currency balance for one GL account, including
        /// both sides of a reversed journal so the original and reversal net to zero.</summary>
        private async Task<decimal> GetAccountBalanceAsync(Guid accountId, Guid companyId, DateTime asOfDate, CancellationToken ct)
        {
            var asOfExclusive = ReportingDates.EndExclusive(asOfDate);
            return await _db.JournalLines.IgnoreQueryFilters()
                .Where(l => l.GlAccountId == accountId && l.JournalEntry!.CompanyId == companyId &&
                    (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed) &&
                    l.JournalEntry.EntryDate < asOfExclusive)
                .SumAsync(l => l.BaseDebit - l.BaseCredit, ct);
        }

        /// <summary>Only exact reciprocal balances (within one cent) are eligible for elimination.</summary>
        public static bool AreReciprocalBalancesMatched(decimal left, decimal right, decimal tolerance = 0.01m) =>
            tolerance >= 0m && Math.Abs(left + right) <= tolerance;

        private async Task<decimal> GetNetEquityAsync(Guid companyId, DateTime asOfDate, CancellationToken ct)
        {
            var tb = await GetTrialBalanceIgnoringFiltersAsync(companyId, asOfDate, ct);
            return tb.Where(l => l.Type == GlAccountType.Equity).Sum(l => l.Credit - l.Debit);
        }

        /// <summary>Same computation as ReportingService.GetTrialBalanceAsync but safe to call for
        /// any entity in the group regardless of the current request's own tenant context.</summary>
        private async Task<List<TrialBalanceLine>> GetTrialBalanceIgnoringFiltersAsync(Guid companyId, DateTime asOfDate, CancellationToken ct)
        {
            // Inactive accounts are included: their historical balances still belong in the consolidation (audit finding 3).
            var asOfExclusive = ReportingDates.EndExclusive(asOfDate);
            var accounts = await _db.GlAccounts.IgnoreQueryFilters().Where(a => a.CompanyId == companyId).ToListAsync(ct);
            var movementsByAccount = await _db.JournalLines.IgnoreQueryFilters()
                .Where(l => l.JournalEntry!.CompanyId == companyId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed) && l.JournalEntry.EntryDate < asOfExclusive)
                .GroupBy(l => l.GlAccountId)
                .Select(g => new { AccountId = g.Key, Debit = g.Sum(x => x.BaseDebit), Credit = g.Sum(x => x.BaseCredit) })
                .ToListAsync(ct);
            var lookup = movementsByAccount.ToDictionary(m => m.AccountId, m => (m.Debit, m.Credit));

            var result = new List<TrialBalanceLine>();
            foreach (var acc in accounts.OrderBy(a => a.Code))
            {
                var (debit, credit) = lookup.TryGetValue(acc.Id, out var m) ? m : (0m, 0m);
                var isDebitNormal = acc.Type is GlAccountType.Asset or GlAccountType.Expense;
                var net = isDebitNormal ? debit - credit : credit - debit;
                if (net == 0 && debit == 0 && credit == 0) continue;
                result.Add(isDebitNormal
                    ? new TrialBalanceLine(acc.Code, acc.Name, acc.Type, Math.Max(net, 0), Math.Max(-net, 0))
                    : new TrialBalanceLine(acc.Code, acc.Name, acc.Type, Math.Max(-net, 0), Math.Max(net, 0)));
            }
            return result;
        }
    }
}
