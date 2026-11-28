using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record ConsolidatedAccountLine(string Code, string Name, GlAccountType Type, decimal GroupAmount, decimal EliminatedAmount, decimal ConsolidatedAmount);
    public record IntercompanyEliminationException(Guid CompanyAId, Guid CompanyBId, string AccountCode,
        decimal CompanyABalance, decimal CompanyBBalance, decimal Difference, string Reason);
    public record ConsolidationRateEvidence(
        Guid CompanyId, string CompanyName, string FromCurrency, string ToCurrency,
        DateTime EffectiveDate, DateTime RateDate, decimal QuotedRate, decimal AppliedRate,
        bool IsInverse, bool IsManualOverride, string RateSource, string? SourceReference);

    public record ConsolidationPeriodEvidence(
        Guid CompanyId, string CompanyName, int? Year, int? PeriodNo,
        DateTime? PeriodStart, DateTime? PeriodEnd, string Status,
        bool IsPeriodEndAligned, bool HasOverlappingPeriods, bool IsGroupPeriodAligned);

    public record ConsolidationResult(
        Guid ParentCompanyId, List<Guid> SubsidiaryCompanyIds, DateTime AsOfDate,
        List<ConsolidatedAccountLine> Lines,
        decimal TotalAssets, decimal TotalLiabilities, decimal TotalGroupEquity, decimal TotalNonControllingInterest,
        decimal TotalEliminations, IReadOnlyList<IntercompanyEliminationException> IntercompanyExceptions,
        IReadOnlyList<ConsolidationRateEvidence> RateEvidence,
        IReadOnlyList<ConsolidationPeriodEvidence> PeriodEvidence);

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

            // The group allow-list is established before opening cross-company reads. Keep every
            // subsequent query constrained to these verified legal entities.
            using var crossTenant = _db.CrossTenantScope();
            var subsidiaries = group.Where(c => c.Id != parentCompanyId).ToList();
            var groupCompanyIds = group.Select(c => c.Id).ToList();
            var periodsCoveringReportDate = await _db.AccountingPeriods.IgnoreQueryFilters()
                .Where(p => groupCompanyIds.Contains(p.CompanyId) &&
                    p.StartDate <= asOfDate.Date && p.EndDate >= asOfDate.Date)
                .ToListAsync(ct);
            var periodEvidence = group.Select(company =>
            {
                var matches = periodsCoveringReportDate.Where(p => p.CompanyId == company.Id)
                    .OrderBy(p => p.Year).ThenBy(p => p.PeriodNo).ToList();
                if (matches.Count == 0)
                    return new ConsolidationPeriodEvidence(company.Id, company.Name, null, null,
                        null, null, "No accounting period covers the reporting date", false, false, false);
                if (matches.Count > 1)
                    return new ConsolidationPeriodEvidence(company.Id, company.Name, null, null,
                        null, null, "Multiple accounting periods overlap the reporting date", false, true, false);

                var period = matches[0];
                var aligned = period.EndDate.Date == asOfDate.Date;
                return new ConsolidationPeriodEvidence(company.Id, company.Name, period.Year, period.PeriodNo,
                    period.StartDate.Date, period.EndDate.Date,
                    aligned ? "Period end aligned" : "Reporting date falls before the period end",
                    aligned, false, false);
            }).OrderBy(x => x.CompanyName).ToList();

            // A company can individually cover the report date while still using a different
            // reporting window from the parent. Surface both period-end and group-window alignment.
            var parentPeriod = periodEvidence.FirstOrDefault(x => x.CompanyId == parentCompanyId);
            periodEvidence = periodEvidence.Select(evidence =>
            {
                var alignedToParent = parentPeriod?.PeriodStart != null && parentPeriod.PeriodEnd != null &&
                    parentPeriod.IsPeriodEndAligned && !parentPeriod.HasOverlappingPeriods &&
                    evidence.PeriodStart == parentPeriod.PeriodStart &&
                    evidence.PeriodEnd == parentPeriod.PeriodEnd &&
                    evidence.IsPeriodEndAligned && !evidence.HasOverlappingPeriods;
                var status = evidence.Status;
                if (evidence.IsPeriodEndAligned && !alignedToParent)
                    status = parentPeriod?.PeriodStart == null
                        ? "Period end aligned; parent period unavailable"
                        : "Period end aligned; reporting window differs from parent";
                return evidence with { Status = status, IsGroupPeriodAligned = alignedToParent };
            }).ToList();

            // Group reporting policy is strict period alignment: every entity must have exactly
            // one accounting period covering the report date, and its start/end dates must match
            // the parent's period with the report date at period end. Consolidation never silently
            // shifts dates or manufactures adjusting entries; entity adjustments must be posted
            // and approved in the relevant ledgers before the group worksheet is produced.
            var misalignedPeriods = periodEvidence.Where(x => !x.IsGroupPeriodAligned).ToList();
            if (misalignedPeriods.Count > 0)
            {
                var details = string.Join("; ", misalignedPeriods.Select(x =>
                    $"{x.CompanyName}: {x.Status}"));
                throw new InvalidOperationException(
                    $"Consolidation is blocked by the group reporting-period policy. " +
                    $"All entities must have one matching period ending on {asOfDate:yyyy-MM-dd}. {details}");
            }

            // Financial rows are read across legal companies only after the tenant-scoped group
            // allow-list has been established. Every query below retains an explicit CompanyId predicate.

            // Aggregate each entity's trial balance line-by-line (by account code, since a well-run
            // group typically shares a common CoA structure across entities; accounts unique to one
            // entity simply appear with that entity's amount only).
            var aggregated = new Dictionary<string, ConsolidatedAccountLine>();
            var accountsById = new Dictionary<Guid, GlAccount>();
            var translatedBalancesByAccount = new Dictionary<Guid, decimal>();
            var translatedTrialBalancesByCompany = new Dictionary<Guid, List<TrialBalanceLine>>();
            var rateEvidence = new List<ConsolidationRateEvidence>();
            var parentCurrency = group.First(c => c.Id == parentCompanyId).FunctionalCurrency;

            foreach (var company in group)
            {
                var companyAccounts = await _db.GlAccounts.IgnoreQueryFilters()
                    .Where(a => a.CompanyId == company.Id).ToListAsync(ct);
                foreach (var account in companyAccounts) accountsById[account.Id] = account;

                // IAS 21 foreign-operation translation: assets/liabilities use the closing rate;
                // equity and income/expense movements use the rate effective on each posting date.
                // The latter is more precise than an average-rate approximation when daily rates exist.
                var translated = await TranslateTrialBalanceAsync(company, parentCurrency, asOfDate, companyAccounts, ct);
                translatedTrialBalancesByCompany[company.Id] = translated.Lines;
                rateEvidence.AddRange(translated.RateEvidence);
                foreach (var balance in translated.AccountBalances)
                    translatedBalancesByAccount[balance.Key] = balance.Value;

                foreach (var line in translated.Lines)
                {
                    var net = line.Debit - line.Credit;
                    if (!aggregated.TryGetValue(line.Code, out var existing))
                    {
                        aggregated[line.Code] = new ConsolidatedAccountLine(line.Code, line.Name, line.Type, net, 0, net);
                    }
                    else
                    {
                        aggregated[line.Code] = existing with
                        {
                            GroupAmount = existing.GroupAmount + net,
                            ConsolidatedAmount = existing.ConsolidatedAmount + net
                        };
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
            var intercompanyExceptions = new List<IntercompanyEliminationException>();
            var reportedExceptionKeys = new HashSet<(Guid CompanyA, Guid CompanyB, string AccountCode)>();
            decimal totalEliminations = 0;

            // Load the transaction register before balance elimination so journal activity can be
            // reconciled in both directions: recorded transactions -> journals, and posted
            // intercompany journals -> transaction records.
            var groupIntercompanyTransactions = await _db.IntercompanyTransactions.IgnoreQueryFilters()
                .Where(t => groupIds.Contains(t.FromCompanyId) && groupIds.Contains(t.ToCompanyId))
                .OrderBy(t => t.TransactionDate).ThenBy(t => t.Id)
                .ToListAsync(ct);
            var transactionJournalIds = new HashSet<Guid>(groupIntercompanyTransactions
                .SelectMany(t => new[] { t.SourceJournalEntryId, t.MirrorJournalEntryId })
                .Where(id => id.HasValue).Select(id => id!.Value));

            // Discover posted intercompany-account journal activity that has no transaction
            // register entry. Such activity is an exception and must not be silently eliminated.
            var reportDateExclusive = ReportingDates.EndExclusive(asOfDate);
            var orphanedIntercompanyLines = await (
                from journalLine in _db.JournalLines.IgnoreQueryFilters()
                join journal in _db.JournalEntries.IgnoreQueryFilters()
                    on journalLine.JournalEntryId equals journal.Id
                join account in _db.GlAccounts.IgnoreQueryFilters()
                    on journalLine.GlAccountId equals account.Id
                where groupIds.Contains(journal.CompanyId) &&
                    account.CompanyId == journal.CompanyId &&
                    account.IsIntercompanyAccount &&
                    account.IntercompanyCounterpartyCompanyId.HasValue &&
                    groupIds.Contains(account.IntercompanyCounterpartyCompanyId.Value) &&
                    (journal.Status == JournalStatus.Posted || journal.Status == JournalStatus.Reversed) &&
                    journal.EntryDate < reportDateExclusive
                select new
                {
                    JournalEntryId = journal.Id,
                    journal.EntryNo,
                    journal.EntryDate,
                    journal.CompanyId,
                    CounterpartyCompanyId = account.IntercompanyCounterpartyCompanyId!.Value,
                    AccountId = account.Id,
                    AccountCode = account.Code,
                    journalLine.BaseDebit,
                    journalLine.BaseCredit
                }).ToListAsync(ct);

            var orphanedIntercompanyAccountIds = new HashSet<Guid>();
            foreach (var orphan in orphanedIntercompanyLines
                .GroupBy(x => new { x.JournalEntryId, x.EntryNo, x.EntryDate, x.CompanyId,
                    x.CounterpartyCompanyId, x.AccountId, x.AccountCode })
                .Where(g => !transactionJournalIds.Contains(g.Key.JournalEntryId)))
            {
                var signedAmount = orphan.Sum(x => x.BaseDebit - x.BaseCredit);
                var companyAId = orphan.Key.CompanyId.CompareTo(orphan.Key.CounterpartyCompanyId) < 0
                    ? orphan.Key.CompanyId : orphan.Key.CounterpartyCompanyId;
                var companyBId = companyAId == orphan.Key.CompanyId
                    ? orphan.Key.CounterpartyCompanyId : orphan.Key.CompanyId;
                orphanedIntercompanyAccountIds.Add(orphan.Key.AccountId);
                intercompanyExceptions.Add(new IntercompanyEliminationException(
                    companyAId, companyBId,
                    $"IC-ORPHAN-{orphan.Key.JournalEntryId:N}-{orphan.Key.AccountCode}",
                    orphan.Key.CompanyId == companyAId ? signedAmount : 0m,
                    orphan.Key.CompanyId == companyBId ? signedAmount : 0m,
                    signedAmount,
                    $"Posted intercompany-account journal {orphan.Key.EntryNo} dated {orphan.Key.EntryDate:yyyy-MM-dd} has no IntercompanyTransaction source/mirror link; affected account balances were not eliminated."));
            }

            // Never eliminate a one-sided or unmatched intercompany balance. A valid elimination
            // requires reciprocal accounts and equal/opposite balances translated into the
            // parent's functional currency as at the reporting date. This prevents an unmatched
            // receivable or payable from disappearing from the consolidated statements.
            foreach (var account in intercoAccounts.OrderBy(a => a.CompanyId).ThenBy(a => a.Code))
            {
                if (eliminatedAccountIds.Contains(account.Id) ||
                    !companiesById.ContainsKey(account.CompanyId) ||
                    !companiesById.ContainsKey(account.IntercompanyCounterpartyCompanyId!.Value))
                    continue;

                if (!accountBalances.TryGetValue(account.Id, out var accountBalance))
                {
                    accountBalance = translatedBalancesByAccount.TryGetValue(account.Id, out var translated)
                        ? translated : 0m;
                    accountBalances[account.Id] = accountBalance;
                }
                if (accountBalance == 0m) continue;

                var reciprocal = intercoAccounts.FirstOrDefault(candidate =>
                    candidate.Id != account.Id &&
                    candidate.CompanyId == account.IntercompanyCounterpartyCompanyId.Value &&
                    candidate.IntercompanyCounterpartyCompanyId == account.CompanyId &&
                    string.Equals(candidate.Code, account.Code, StringComparison.OrdinalIgnoreCase));
                var companyA = account.CompanyId.CompareTo(account.IntercompanyCounterpartyCompanyId.Value) < 0
                    ? account.CompanyId : account.IntercompanyCounterpartyCompanyId.Value;
                var companyB = companyA == account.CompanyId ? account.IntercompanyCounterpartyCompanyId.Value : account.CompanyId;
                var exceptionKey = (companyA, companyB, account.Code.ToUpperInvariant());

                if (reciprocal == null)
                {
                    if (reportedExceptionKeys.Add(exceptionKey))
                    {
                        intercompanyExceptions.Add(new IntercompanyEliminationException(
                            companyA, companyB, account.Code,
                            account.CompanyId == companyA ? accountBalance : 0m,
                            account.CompanyId == companyB ? accountBalance : 0m,
                            accountBalance,
                            "No reciprocal intercompany account with the same code and counterparty was found."));
                    }
                    continue;
                }
                if (eliminatedAccountIds.Contains(reciprocal.Id))
                    continue;

                if (!accountBalances.TryGetValue(reciprocal.Id, out var reciprocalBalance))
                {
                    reciprocalBalance = translatedBalancesByAccount.TryGetValue(reciprocal.Id, out var translated)
                        ? translated : 0m;
                    accountBalances[reciprocal.Id] = reciprocalBalance;
                }

                if (!AreReciprocalBalancesMatched(accountBalance, reciprocalBalance))
                {
                    if (reportedExceptionKeys.Add(exceptionKey))
                    {
                        intercompanyExceptions.Add(new IntercompanyEliminationException(
                            companyA, companyB, account.Code,
                            account.CompanyId == companyA ? accountBalance : reciprocalBalance,
                            account.CompanyId == companyB ? accountBalance : reciprocalBalance,
                            accountBalance + reciprocalBalance,
                            "Reciprocal balances do not agree within the one-cent tolerance; neither side was eliminated."));
                    }
                    continue;
                }

                // A reciprocal account pair can still be arithmetically matched while its
                // underlying posted journal activity is absent from the transaction register.
                // Keep the balances visible until that source-to-mirror control gap is resolved.
                if (orphanedIntercompanyAccountIds.Contains(account.Id) ||
                    orphanedIntercompanyAccountIds.Contains(reciprocal.Id))
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

            // Eliminate the income/expense sides of matched mirror postings by their linked
            // source and mirror journals. These accounts are not necessarily the same GL code
            // (revenue versus expense), so balance-sheet reciprocal-account matching above cannot
            // safely identify them. Translate each P&L line at its own posting-date rate first.
            var journalIds = transactionJournalIds.ToList();
            var journalHeaders = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => journalIds.Contains(j.Id))
                .Select(j => new { j.Id, j.CompanyId, j.Status })
                .ToDictionaryAsync(j => j.Id, ct);

            foreach (var transaction in groupIntercompanyTransactions)
            {
                var transactionCode = $"IC-TXN-{transaction.Id:N}";
                if (transaction.Status == IntercompanyStatus.Unmatched ||
                    !transaction.SourceJournalEntryId.HasValue || !transaction.MirrorJournalEntryId.HasValue)
                {
                    intercompanyExceptions.Add(new IntercompanyEliminationException(
                        transaction.FromCompanyId, transaction.ToCompanyId, transactionCode,
                        transaction.Amount, 0m, transaction.Amount,
                        "Intercompany transaction is unmatched or is missing its source/mirror journal link; no elimination was performed."));
                    continue;
                }

                var sourceJournalId = transaction.SourceJournalEntryId.Value;
                var mirrorJournalId = transaction.MirrorJournalEntryId.Value;
                var sourceValid = journalHeaders.TryGetValue(sourceJournalId, out var sourceHeader) &&
                    sourceHeader.CompanyId == transaction.FromCompanyId &&
                    sourceHeader.Status is JournalStatus.Posted or JournalStatus.Reversed;
                var mirrorValid = journalHeaders.TryGetValue(mirrorJournalId, out var mirrorHeader) &&
                    mirrorHeader.CompanyId == transaction.ToCompanyId &&
                    mirrorHeader.Status is JournalStatus.Posted or JournalStatus.Reversed;
                if (!sourceValid || !mirrorValid)
                {
                    intercompanyExceptions.Add(new IntercompanyEliminationException(
                        transaction.FromCompanyId, transaction.ToCompanyId, transactionCode,
                        transaction.Amount, 0m, transaction.Amount,
                        "Source or mirror journal is missing, belongs to the wrong company, or is not posted; no elimination was performed."));
                    continue;
                }
                var pnlLines = await (
                    from journalLine in _db.JournalLines.IgnoreQueryFilters()
                    join journal in _db.JournalEntries.IgnoreQueryFilters()
                        on journalLine.JournalEntryId equals journal.Id
                    join account in _db.GlAccounts.IgnoreQueryFilters()
                        on journalLine.GlAccountId equals account.Id
                    where ((journal.Id == sourceJournalId && journal.CompanyId == transaction.FromCompanyId) ||
                           (journal.Id == mirrorJournalId && journal.CompanyId == transaction.ToCompanyId)) &&
                          (journal.Status == JournalStatus.Posted || journal.Status == JournalStatus.Reversed) &&
                          (account.Type == GlAccountType.Income || account.Type == GlAccountType.Expense)
                    select new
                    {
                        journal.CompanyId,
                        journal.EntryDate,
                        AccountCode = account.Code,
                        SignedAmount = journalLine.BaseDebit - journalLine.BaseCredit
                    }).ToListAsync(ct);

                if (pnlLines.Count == 0)
                {
                    intercompanyExceptions.Add(new IntercompanyEliminationException(
                        transaction.FromCompanyId, transaction.ToCompanyId, transactionCode,
                        0m, 0m, 0m,
                        "Linked journals contain no posted income or expense lines to reconcile; no P&L elimination was performed."));
                    continue;
                }

                decimal TranslatePnlLine(Guid companyId, DateTime entryDate, decimal signedAmount)
                {
                    var companyCurrency = companiesById[companyId].FunctionalCurrency.Trim();
                    if (string.Equals(companyCurrency, parentCurrency, StringComparison.OrdinalIgnoreCase))
                        return signedAmount;

                    var selectedRate = rateEvidence.FirstOrDefault(r =>
                        r.CompanyId == companyId && r.EffectiveDate == entryDate.Date);
                    if (selectedRate == null)
                        throw new InvalidOperationException(
                            $"Missing posting-date translation evidence for intercompany journal {companyId:N} on {entryDate:yyyy-MM-dd}.");
                    return signedAmount * selectedRate.AppliedRate;
                }

                var sourceTotal = pnlLines.Where(l => l.CompanyId == transaction.FromCompanyId)
                    .Sum(l => TranslatePnlLine(l.CompanyId, l.EntryDate, l.SignedAmount));
                var mirrorTotal = pnlLines.Where(l => l.CompanyId == transaction.ToCompanyId)
                    .Sum(l => TranslatePnlLine(l.CompanyId, l.EntryDate, l.SignedAmount));

                if (!AreReciprocalBalancesMatched(sourceTotal, mirrorTotal))
                {
                    intercompanyExceptions.Add(new IntercompanyEliminationException(
                        transaction.FromCompanyId, transaction.ToCompanyId,
                        $"IC-TXN-{transaction.Id:N}", sourceTotal, mirrorTotal, sourceTotal + mirrorTotal,
                        "Linked intercompany income/expense journal amounts do not agree in group currency; neither side was eliminated."));
                    continue;
                }

                foreach (var line in pnlLines)
                {
                    if (!aggregated.TryGetValue(line.AccountCode, out var consolidatedLine)) continue;
                    var translatedAmount = TranslatePnlLine(line.CompanyId, line.EntryDate, line.SignedAmount);
                    var elimination = -translatedAmount;
                    aggregated[line.AccountCode] = consolidatedLine with
                    {
                        EliminatedAmount = consolidatedLine.EliminatedAmount + elimination,
                        ConsolidatedAmount = consolidatedLine.ConsolidatedAmount + elimination
                    };
                }

                totalEliminations += Math.Abs(sourceTotal);
            }

            // The difference caused by translating foreign operations at closing and historical
            // rates is presented in equity as the foreign-currency translation reserve (CTA).
            var translationReserve = -aggregated.Values.Sum(l => l.ConsolidatedAmount);
            if (translationReserve != 0m)
            {
                const string ctaCode = "FX-CTA";
                if (aggregated.ContainsKey(ctaCode))
                    throw new InvalidOperationException("The chart of accounts uses reserved code FX-CTA; rename that account before group translation.");
                aggregated[ctaCode] = new ConsolidatedAccountLine(ctaCode,
                    "Foreign currency translation reserve", GlAccountType.Equity,
                    0m, 0m, translationReserve);
            }

            var lines = aggregated.Values.OrderBy(l => l.Code).ToList();
            var totalAssets = lines.Where(l => l.Type == GlAccountType.Asset).Sum(l => l.ConsolidatedAmount);
            var totalLiabilities = lines.Where(l => l.Type == GlAccountType.Liability).Sum(l => -l.ConsolidatedAmount);

            // ---- Non-controlling interest: for each <100%-owned subsidiary, split its net equity ----
            decimal totalNci = 0, totalGroupEquityAdj = 0;
            foreach (var sub in subsidiaries)
            {
                // NCI in a lower-tier subsidiary includes both the immediate minority interests
                // and the indirect minority interests held through intermediate subsidiaries.
                // Multiply ownership along the complete path from the consolidation parent.
                var effectiveGroupOwnership = CalculateEffectiveGroupOwnershipPercentage(
                    group, parentCompanyId, sub.Id);
                if (!effectiveGroupOwnership.HasValue || effectiveGroupOwnership.Value >= 1m)
                    continue;

                var subTrialBalance = translatedTrialBalancesByCompany[sub.Id];
                // Net assets includes current-period earnings and the subsidiary's translated
                // equity reserve, even where the local year-end close has not yet transferred P&L.
                var subAssets = subTrialBalance.Where(l => l.Type == GlAccountType.Asset).Sum(l => l.Debit - l.Credit);
                var subLiabilities = subTrialBalance.Where(l => l.Type == GlAccountType.Liability).Sum(l => l.Credit - l.Debit);
                var subEquity = subAssets - subLiabilities;
                var subsidiaryEliminationAdjustment = eliminatedAccountIds
                    .Where(id => accountsById[id].CompanyId == sub.Id && translatedBalancesByAccount.ContainsKey(id))
                    .Sum(id =>
                    {
                        var account = accountsById[id];
                        var signedBalance = translatedBalancesByAccount[id];
                        return account.Type is GlAccountType.Asset or GlAccountType.Liability
                            ? -signedBalance
                            : account.Type is GlAccountType.Income or GlAccountType.Expense
                                ? signedBalance
                                : 0m;
                    });
                subEquity += subsidiaryEliminationAdjustment;
                var nciShare = subEquity * (1 - effectiveGroupOwnership.Value);
                totalNci += nciShare;
                totalGroupEquityAdj -= nciShare; // remove NCI's share from the raw aggregated equity total
            }

            var rawEquity = lines.Where(l => l.Type == GlAccountType.Equity).Sum(l => -l.ConsolidatedAmount);
            var currentPeriodProfit = -lines.Where(l => l.Type is GlAccountType.Income or GlAccountType.Expense)
                .Sum(l => l.ConsolidatedAmount);
            var totalGroupEquity = rawEquity + currentPeriodProfit + totalGroupEquityAdj;

            return new ConsolidationResult(parentCompanyId, subsidiaries.Select(s => s.Id).ToList(), asOfDate,
                lines, totalAssets, totalLiabilities, totalGroupEquity, totalNci, totalEliminations,
                intercompanyExceptions.OrderBy(x => x.CompanyAId).ThenBy(x => x.CompanyBId).ThenBy(x => x.AccountCode).ToList(),
                rateEvidence.OrderBy(x => x.CompanyName).ThenBy(x => x.EffectiveDate).ToList(),
                periodEvidence);
        }

        private sealed record TranslatedTrialBalance(List<TrialBalanceLine> Lines,
            Dictionary<Guid, decimal> AccountBalances, List<ConsolidationRateEvidence> RateEvidence);

        private async Task<TranslatedTrialBalance> TranslateTrialBalanceAsync(Company company, string groupCurrency,
            DateTime asOfDate, List<GlAccount> accounts, CancellationToken ct)
        {
            var accountIds = accounts.Select(a => a.Id).ToList();
            var asOfExclusive = ReportingDates.EndExclusive(asOfDate);
            var movements = await _db.JournalLines.IgnoreQueryFilters()
                .Where(l => accountIds.Contains(l.GlAccountId) && l.JournalEntry!.CompanyId == company.Id &&
                    (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed) &&
                    l.JournalEntry.EntryDate < asOfExclusive)
                .Select(l => new { l.GlAccountId, l.BaseDebit, l.BaseCredit, EntryDate = l.JournalEntry!.EntryDate })
                .ToListAsync(ct);

            var functionalCurrency = company.FunctionalCurrency.Trim().ToUpperInvariant();
            var targetCurrency = groupCurrency.Trim().ToUpperInvariant();
            var sameCurrency = string.Equals(functionalCurrency, targetCurrency, StringComparison.OrdinalIgnoreCase);
            var directRates = new List<ExchangeRate>();
            var inverseRateRecords = new List<ExchangeRate>();
            Func<DateTime, decimal> rateAt = _ => 1m;
            var selectedRatesByDate = new Dictionary<DateTime, SelectedExchangeRate>();
            if (!sameCurrency)
            {
                directRates = await _db.ExchangeRates.IgnoreQueryFilters()
                    .Where(r => r.CompanyId == company.Id && r.FromCurrency.ToUpper() == functionalCurrency &&
                        r.ToCurrency.ToUpper() == targetCurrency && r.RateDate < asOfExclusive && r.Rate > 0)
                    .ToListAsync(ct);
                inverseRateRecords = await _db.ExchangeRates.IgnoreQueryFilters()
                    .Where(r => r.CompanyId == company.Id && r.FromCurrency.ToUpper() == targetCurrency &&
                        r.ToCurrency.ToUpper() == functionalCurrency && r.RateDate < asOfExclusive && r.Rate > 0)
                    .ToListAsync(ct);
                if (directRates.Count == 0 && inverseRateRecords.Count == 0)
                    throw new InvalidOperationException($"Missing exchange rates to translate subsidiary {company.Name} from {functionalCurrency} to group currency {targetCurrency}.");

                rateAt = date =>
                {
                    var effectiveDate = date.Date;
                    if (!selectedRatesByDate.TryGetValue(effectiveDate, out var selected))
                    {
                        selected = ExchangeRateResolver.SelectEffectiveRate(directRates, inverseRateRecords, effectiveDate)
                            ?? throw new InvalidOperationException($"No {functionalCurrency}/{targetCurrency} exchange rate is available on or before {effectiveDate:yyyy-MM-dd} for subsidiary {company.Name}.");
                        selectedRatesByDate[effectiveDate] = selected;
                    }
                    return selected.IsInverse ? 1m / selected.Rate.Rate : selected.Rate.Rate;
                };
            }

            var accountBalances = new Dictionary<Guid, decimal>();
            var movementGroups = movements.GroupBy(m => m.GlAccountId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var account in accounts)
            {
                if (!movementGroups.TryGetValue(account.Id, out var accountMovements))
                    continue;

                decimal translatedBalance;
                if (sameCurrency)
                {
                    translatedBalance = accountMovements.Sum(m => m.BaseDebit - m.BaseCredit);
                }
                else if (account.Type is GlAccountType.Asset or GlAccountType.Liability)
                {
                    var localBalance = accountMovements.Sum(m => m.BaseDebit - m.BaseCredit);
                    translatedBalance = localBalance * rateAt(asOfDate);
                }
                else
                {
                    // IAS 21 transaction-date rates for equity and profit-or-loss movements.
                    translatedBalance = accountMovements.Sum(m =>
                        (m.BaseDebit - m.BaseCredit) * rateAt(m.EntryDate));
                }
                accountBalances[account.Id] = translatedBalance;
            }

            var lines = new List<TrialBalanceLine>();
            foreach (var group in accounts.GroupBy(a => a.Code))
            {
                var signed = group.Where(a => accountBalances.ContainsKey(a.Id))
                    .Sum(a => accountBalances[a.Id]);
                if (signed == 0m) continue;
                var representative = group.First();
                // Account movements are already stored as debit-minus-credit in functional
                // currency. Preserve that sign when presenting debit/credit columns for every
                // account type; flipping credit-normal balances here reverses equity/liabilities.
                lines.Add(new TrialBalanceLine(representative.Code, representative.Name, representative.Type,
                    Math.Max(signed, 0m), Math.Max(-signed, 0m)));
            }

            var evidence = selectedRatesByDate.Select(selection =>
            {
                var selected = selection.Value;
                var appliedRate = selected.IsInverse ? 1m / selected.Rate.Rate : selected.Rate.Rate;
                return new ConsolidationRateEvidence(company.Id, company.Name, functionalCurrency, targetCurrency,
                    selection.Key, selected.Rate.RateDate.Date, selected.Rate.Rate, appliedRate,
                    selected.IsInverse, selected.Rate.IsManualOverride, selected.Rate.RateSource,
                    selected.Rate.SourceReference);
            }).OrderBy(x => x.EffectiveDate).ToList();

            return new TranslatedTrialBalance(lines, accountBalances, evidence);
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

        /// <summary>
        /// Calculates the consolidation parent's effective ownership in a subsidiary by
        /// multiplying ownership percentages along the complete parent chain. Returns null
        /// when the subsidiary is outside the supplied group, the chain is cyclic/incomplete,
        /// or any required ownership percentage is unspecified or invalid.
        /// </summary>
        public static decimal? CalculateEffectiveGroupOwnershipPercentage(
            IEnumerable<Company> group, Guid parentCompanyId, Guid subsidiaryCompanyId)
        {
            var companies = group.ToDictionary(c => c.Id);
            if (!companies.ContainsKey(parentCompanyId) || !companies.ContainsKey(subsidiaryCompanyId))
                return null;
            if (parentCompanyId == subsidiaryCompanyId)
                return 1m;

            decimal effectiveOwnership = 1m;
            var current = companies[subsidiaryCompanyId];
            var visited = new HashSet<Guid>();
            while (current.Id != parentCompanyId)
            {
                if (!visited.Add(current.Id) ||
                    !current.OwnershipPercentage.HasValue ||
                    current.OwnershipPercentage.Value < 0m ||
                    current.OwnershipPercentage.Value > 100m ||
                    !current.ParentCompanyId.HasValue ||
                    !companies.TryGetValue(current.ParentCompanyId.Value, out var parent))
                    return null;

                effectiveOwnership *= current.OwnershipPercentage.Value / 100m;
                current = parent;
            }

            return effectiveOwnership;
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
