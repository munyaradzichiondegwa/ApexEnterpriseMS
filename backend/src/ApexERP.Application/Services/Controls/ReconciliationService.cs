using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    /// <summary>
    /// Sub-ledger to general-ledger reconciliation (spec 13.3), bank reconciliation certification and petty cash (spec 18),
    /// and the cash-flow forecast. A difference is never hidden: it is stored, must be explained, and must be signed off by a second person.
    /// </summary>
    public class ReconciliationService
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;
        private readonly GovernanceService _gov;
        private readonly SettingsService _settings;
        private readonly AuditService _audit;
        private const decimal Tolerance = 0.005m;

        public ReconciliationService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, SettingsService settings, AuditService audit)
        { _db = db; _gl = gl; _gov = gov; _settings = settings; _audit = audit; }

        public static readonly string[] Areas = { "AP", "AR", "Inventory", "FixedAssets", "Payroll", "Bank" };

        // ---------------------------------------------------------------------------------------------
        // GL balance helper
        // ---------------------------------------------------------------------------------------------
        public async Task<decimal> GlDebitBalanceAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, DateTime asOf, CancellationToken ct = default)
        {
            if (accountIds.Count == 0) return 0m;
            var lines = _db.JournalEntries
                .Where(j => j.CompanyId == companyId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed) && j.EntryDate <= asOf)
                .SelectMany(j => j.Lines).Where(l => accountIds.Contains(l.GlAccountId));
            var d = await lines.SumAsync(l => (decimal?)l.BaseDebit, ct) ?? 0m;
            var c = await lines.SumAsync(l => (decimal?)l.BaseCredit, ct) ?? 0m;
            return d - c;
        }

        /// <summary>Control accounts come from the company setting "recon.&lt;area&gt;.accounts" (comma-separated ids); otherwise the account most used by that module's journals.</summary>
        private async Task<List<Guid>> ControlAccountsAsync(Guid companyId, string area, JournalSource source, bool creditSide, GlAccountType type, CancellationToken ct)
        {
            var configured = await _settings.GetAsync(companyId, $"recon.{area.ToLowerInvariant()}.accounts", ct);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToList();
            var q = _db.JournalEntries.Where(j => j.CompanyId == companyId && j.Source == source && j.Status != JournalStatus.Draft).SelectMany(j => j.Lines);
            q = creditSide ? q.Where(l => l.BaseCredit > 0) : q.Where(l => l.BaseDebit > 0);
            var typed = _db.GlAccounts.Where(a => a.CompanyId == companyId && a.Type == type).Select(a => a.Id);
            var top = await q.Where(l => typed.Contains(l.GlAccountId)).GroupBy(l => l.GlAccountId).Select(g => new { g.Key, N = g.Count() }).OrderByDescending(x => x.N).Take(1).ToListAsync(ct);
            return top.Select(t => t.Key).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // Runs
        // ---------------------------------------------------------------------------------------------
        public async Task<ReconciliationRun> RunAsync(Guid companyId, string area, DateTime asOf, Guid? userId, CancellationToken ct = default)
        {
            asOf = asOf.Date;
            decimal sub, gl; string basis;
            switch (area)
            {
                case "AP":
                {
                    var invoices = await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled && i.InvoiceDate <= asOf).ToListAsync(ct);
                    var ids = invoices.Select(i => i.Id).ToList();
                    var allocs = await _db.ApPaymentAllocations.Where(a => ids.Contains(a.ApInvoiceId))
                        .Join(_db.ApPayments.Where(p => p.PaymentDate <= asOf), a => a.ApPaymentId, p => p.Id, (a, p) => new { a.ApInvoiceId, a.AmountApplied }).ToListAsync(ct);
                    sub = invoices.Sum(i => (i.Total - i.WhtAmount) * i.ExchangeRate) - allocs.Sum(a => a.AmountApplied * (invoices.First(i => i.Id == a.ApInvoiceId).ExchangeRate));
                    var accts = await ControlAccountsAsync(companyId, "ap", JournalSource.AP, true, GlAccountType.Liability, ct);
                    gl = -await GlDebitBalanceAsync(companyId, accts, asOf, ct);
                    basis = $"AP control account(s): {string.Join(",", accts.Select(a => a.ToString("N")[..8]))}";
                    break;
                }
                case "AR":
                {
                    var invoices = await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled && i.InvoiceDate <= asOf).ToListAsync(ct);
                    var ids = invoices.Select(i => i.Id).ToList();
                    var allocs = await _db.ArReceiptAllocations.Where(a => ids.Contains(a.ArInvoiceId))
                        .Join(_db.ArReceipts.Where(r => r.ReceiptDate <= asOf), a => a.ArReceiptId, r => r.Id, (a, r) => new { a.ArInvoiceId, a.AmountApplied }).ToListAsync(ct);
                    var credited = await _db.ArCreditNotes.Where(n => n.CompanyId == companyId && n.Status == "Posted").ToListAsync(ct);
                    sub = invoices.Sum(i => i.Total * i.ExchangeRate) - allocs.Sum(a => a.AmountApplied * invoices.First(i => i.Id == a.ArInvoiceId).ExchangeRate)
                          - credited.Where(n => ids.Contains(n.ArInvoiceId)).Sum(n => n.Amount * invoices.First(i => i.Id == n.ArInvoiceId).ExchangeRate);
                    var accts = await ControlAccountsAsync(companyId, "ar", JournalSource.AR, false, GlAccountType.Asset, ct);
                    gl = await GlDebitBalanceAsync(companyId, accts, asOf, ct);
                    basis = $"AR control account(s): {string.Join(",", accts.Select(a => a.ToString("N")[..8]))}";
                    break;
                }
                case "Inventory":
                {
                    var items = await _db.InventoryItems.Where(i => i.CompanyId == companyId).ToListAsync(ct);
                    sub = items.Sum(i => i.QuantityOnHand * i.AverageCost);
                    gl = await GlDebitBalanceAsync(companyId, items.Select(i => i.AssetAccountId).Distinct().ToList(), asOf, ct);
                    basis = "Stock quantity x average cost (current position) against the inventory asset accounts";
                    break;
                }
                case "FixedAssets":
                {
                    var assets = await _db.FixedAssets.Include(a => a.Category).Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active).ToListAsync(ct);
                    sub = assets.Sum(a => a.CurrentNbv);
                    var cost = assets.Where(a => a.Category != null).Select(a => a.Category!.AssetAccountId).Distinct().ToList();
                    var dep = assets.Where(a => a.Category != null).Select(a => a.Category!.AccumulatedDepreciationAccountId).Distinct().ToList();
                    gl = await GlDebitBalanceAsync(companyId, cost, asOf, ct) + await GlDebitBalanceAsync(companyId, dep, asOf, ct);
                    basis = "Net book value of active assets (current position) against cost less accumulated depreciation accounts";
                    break;
                }
                case "Payroll":
                {
                    var runs = await _db.PayrollRuns.Include(r => r.Lines).Where(r => r.CompanyId == companyId && r.JournalEntryId != null).ToListAsync(ct);
                    sub = runs.Sum(r => r.Lines.Sum(l => l.GrossPay + l.NssaEmployer));
                    var jids = runs.Select(r => r.JournalEntryId!.Value).ToList();
                    gl = await _db.JournalEntries.Where(j => jids.Contains(j.Id)).SelectMany(j => j.Lines).SumAsync(l => (decimal?)l.BaseDebit, ct) ?? 0m;
                    basis = "Gross pay plus employer NSSA on posted runs against the debits of their journals";
                    break;
                }
                case "Bank":
                {
                    var banks = await _db.BankAccounts.Where(b => b.CompanyId == companyId).ToListAsync(ct);
                    sub = banks.Sum(b => b.CurrentBalance);
                    gl = await GlDebitBalanceAsync(companyId, banks.Select(b => b.GlAccountId).Distinct().ToList(), asOf, ct);
                    basis = "Bank account balances against their GL accounts";
                    break;
                }
                default: throw new InvalidOperationException("Area must be one of: " + string.Join(", ", Areas));
            }

            var diff = Math.Round(sub - gl, 2);
            var run = new ReconciliationRun
            {
                CompanyId = companyId, Area = area, AsOfDate = asOf, SubledgerTotal = Math.Round(sub, 2), GlTotal = Math.Round(gl, 2), Difference = diff,
                Status = Math.Abs(diff) <= Tolerance ? "Reconciled" : "Unreconciled", Explanation = basis, RunBy = userId, CreatedBy = userId
            };
            _db.ReconciliationRuns.Add(run);
            _audit.Record(companyId, userId, "Reconciliation.Run", "Reconciliation", nameof(ReconciliationRun), run.Id, null, new { area, asOf, diff });
            await _db.SaveChangesAsync(ct);
            return run;
        }

        public async Task<List<ReconciliationRun>> RunAllAsync(Guid companyId, DateTime asOf, Guid? userId, CancellationToken ct = default)
        {
            var list = new List<ReconciliationRun>();
            foreach (var a in Areas) list.Add(await RunAsync(companyId, a, asOf, userId, ct));
            return list;
        }

        public async Task<ReconciliationRun> ExplainAsync(Guid companyId, Guid id, string explanation, Guid userId, CancellationToken ct = default)
        {
            var r = await LoadRun(companyId, id, ct);
            if (r.Status != "Unreconciled") throw new InvalidOperationException("Only unreconciled runs need an explanation.");
            if (string.IsNullOrWhiteSpace(explanation) || explanation.Trim().Length < 15) throw new InvalidOperationException("Explain the difference in enough detail for a reviewer to follow (at least 15 characters).");
            r.Explanation = (r.Explanation ?? "") + " || " + explanation.Trim(); r.Status = "Explained"; r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
            _audit.Record(companyId, userId, "Reconciliation.Explained", "Reconciliation", nameof(ReconciliationRun), r.Id, null, new { r.Difference });
            await _db.SaveChangesAsync(ct); return r;
        }

        public async Task<ReconciliationRun> SignOffAsync(Guid companyId, Guid id, Guid reviewer, CancellationToken ct = default)
        {
            var r = await LoadRun(companyId, id, ct);
            if (r.Status is not ("Reconciled" or "Explained")) throw new InvalidOperationException("Only reconciled or explained runs can be signed off.");
            if (r.RunBy == reviewer) throw new InvalidOperationException("The reviewer must be someone other than the person who ran the reconciliation.");
            r.Status = "SignedOff"; r.SignedOffBy = reviewer; r.SignedOffAt = DateTime.UtcNow;
            _audit.Record(companyId, reviewer, "Reconciliation.SignedOff", "Reconciliation", nameof(ReconciliationRun), r.Id, null, new { r.Area, r.Difference });
            await _db.SaveChangesAsync(ct); return r;
        }

        public Task<List<ReconciliationRun>> ListAsync(Guid companyId, string? area, CancellationToken ct = default)
        {
            var q = _db.ReconciliationRuns.Where(r => r.CompanyId == companyId);
            if (!string.IsNullOrWhiteSpace(area)) q = q.Where(r => r.Area == area);
            return q.OrderByDescending(r => r.AsOfDate).ThenByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        }

        private async Task<ReconciliationRun> LoadRun(Guid companyId, Guid id, CancellationToken ct) =>
            await _db.ReconciliationRuns.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Reconciliation run not found.");

        // ---------------------------------------------------------------------------------------------
        // Bank reconciliation certification (spec 18)
        // ---------------------------------------------------------------------------------------------
        /// <summary>
        /// Statement balance must equal the book balance plus statement items not yet in the books (receipts positive, payments negative).
        /// </summary>
        public async Task<BankReconciliation> PrepareBankReconciliationAsync(Guid companyId, Guid bankAccountId, DateTime statementDate, decimal statementBalance, string? notes, Guid userId, CancellationToken ct = default)
        {
            var bank = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == bankAccountId && b.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Bank account not found.");
            var book = await GlDebitBalanceAsync(companyId, new[] { bank.GlAccountId }, statementDate.Date, ct);
            var unmatched = await _db.BankTransactions.Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId && !t.IsReconciled && t.TransactionDate <= statementDate).ToListAsync(ct);
            var unmatchedNet = unmatched.Sum(t => t.Debit - t.Credit);
            var diff = Math.Round(statementBalance - (book + unmatchedNet), 2);
            var rec = new BankReconciliation
            {
                CompanyId = companyId, BankAccountId = bankAccountId, StatementDate = statementDate.Date, StatementBalance = statementBalance, BookBalance = book,
                UnreconciledItemsTotal = unmatchedNet, Difference = diff, PreparedBy = userId, CreatedBy = userId, Notes = notes
            };
            _db.BankReconciliations.Add(rec);
            _audit.Record(companyId, userId, "BankRec.Prepared", "Bank", nameof(BankReconciliation), rec.Id, null, new { bankAccountId, diff });
            await _db.SaveChangesAsync(ct); return rec;
        }

        public async Task<BankReconciliation> CertifyBankReconciliationAsync(Guid companyId, Guid id, Guid reviewer, string? reviewerNotes, CancellationToken ct = default)
        {
            var rec = await _db.BankReconciliations.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Bank reconciliation not found.");
            if (rec.Status != "Draft") throw new InvalidOperationException("Only draft reconciliations can be certified.");
            if (rec.PreparedBy == reviewer) throw new InvalidOperationException("A bank reconciliation must be certified by someone other than its preparer.");
            if (Math.Abs(rec.Difference) > Tolerance && string.IsNullOrWhiteSpace(reviewerNotes))
                throw new InvalidOperationException($"The reconciliation has an unexplained difference of {rec.Difference:N2}. Resolve it or record the reviewer's explanation.");
            rec.Status = "Certified"; rec.ReviewedBy = reviewer; rec.CertifiedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(reviewerNotes)) rec.Notes = (rec.Notes ?? "") + " | Reviewer: " + reviewerNotes;
            _audit.Record(companyId, reviewer, "BankRec.Certified", "Bank", nameof(BankReconciliation), rec.Id, null, new { rec.Difference });
            await _db.SaveChangesAsync(ct); return rec;
        }

        public Task<List<BankReconciliation>> ListBankReconciliationsAsync(Guid companyId, CancellationToken ct = default) =>
            _db.BankReconciliations.Where(r => r.CompanyId == companyId).OrderByDescending(r => r.StatementDate).Take(200).ToListAsync(ct);

        // ---------------------------------------------------------------------------------------------
        // Petty cash (spec 18)
        // ---------------------------------------------------------------------------------------------
        public async Task<PettyCashFund> CreateFundAsync(Guid companyId, string name, Guid glAccountId, decimal imprest, Guid? custodian, Guid userId, CancellationToken ct = default)
        {
            if (imprest <= 0) throw new InvalidOperationException("The imprest amount must be positive.");
            if (!await _db.GlAccounts.AnyAsync(a => a.Id == glAccountId && a.CompanyId == companyId && a.IsActive, ct)) throw new InvalidOperationException("Account not found.");
            var f = new PettyCashFund { CompanyId = companyId, Name = name, GlAccountId = glAccountId, ImprestAmount = imprest, CustodianUserId = custodian, CreatedBy = userId };
            _db.PettyCashFunds.Add(f);
            _audit.Record(companyId, userId, "PettyCash.FundCreated", "Bank", nameof(PettyCashFund), f.Id, null, new { name, imprest });
            await _db.SaveChangesAsync(ct); return f;
        }

        public async Task<PettyCashVoucher> CreateVoucherAsync(Guid companyId, Guid fundId, DateTime date, decimal amount, Guid expenseAccountId, string description, Guid userId, CancellationToken ct = default)
        {
            var fund = await _db.PettyCashFunds.FirstOrDefaultAsync(f => f.Id == fundId && f.CompanyId == companyId && f.IsActive, ct) ?? throw new InvalidOperationException("Fund not found.");
            if (amount <= 0) throw new InvalidOperationException("The amount must be positive.");
            if (amount > fund.ImprestAmount) throw new InvalidOperationException("A single voucher cannot exceed the imprest amount.");
            if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("A description is required.");
            if (!await _db.GlAccounts.AnyAsync(a => a.Id == expenseAccountId && a.CompanyId == companyId && a.IsActive, ct)) throw new InvalidOperationException("Expense account not found.");
            var v = new PettyCashVoucher { CompanyId = companyId, PettyCashFundId = fundId, VoucherDate = date.Date, Amount = amount, ExpenseAccountId = expenseAccountId, Description = description, PreparedBy = userId, CreatedBy = userId };
            _db.PettyCashVouchers.Add(v);
            _audit.Record(companyId, userId, "PettyCash.VoucherCreated", "Bank", nameof(PettyCashVoucher), v.Id, null, new { amount });
            await _db.SaveChangesAsync(ct); return v;
        }

        public Task<PettyCashVoucher> ApproveAndPostVoucherAsync(Guid companyId, Guid voucherId, Guid approver, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                var v = await _db.PettyCashVouchers.FirstOrDefaultAsync(x => x.Id == voucherId && x.CompanyId == companyId, token) ?? throw new InvalidOperationException("Voucher not found.");
                if (v.Status != "Draft") throw new InvalidOperationException("Only draft vouchers can be approved.");
                var decision = await _gov.CheckApprovalAuthorityAsync(companyId, approver, "PettyCash", v.Amount, v.PreparedBy, token);
                if (!decision.Allowed) throw new InvalidOperationException(decision.Reason ?? "Not authorised.");
                var fund = await _db.PettyCashFunds.FirstAsync(f => f.Id == v.PettyCashFundId && f.CompanyId == companyId, token);
                var onHand = await GlDebitBalanceAsync(companyId, new[] { fund.GlAccountId }, DateTime.UtcNow.Date.AddDays(1), token);
                if (onHand < v.Amount) throw new InvalidOperationException($"The fund holds only {onHand:N2}. Replenish it first.");
                var je = await _gl.PostAsync(companyId, v.VoucherDate, JournalSource.Bank, $"Petty cash: {v.Description}", $"PCV-{v.Id.ToString("N")[..8]}",
                    new[] { new GlLineRequest(v.ExpenseAccountId, v.Amount, 0, "USD", 1m, null, v.Description), new GlLineRequest(fund.GlAccountId, 0, v.Amount, "USD", 1m, null, "Petty cash paid out") }, token, approver);
                v.Status = "Posted"; v.ApprovedBy = approver; v.JournalEntryId = je.Id;
                _audit.Record(companyId, approver, "PettyCash.VoucherPosted", "Bank", nameof(PettyCashVoucher), v.Id, null, new { je.EntryNo });
                await _db.SaveChangesAsync(token); return v;
            }, ct);

        public Task<JournalEntry> ReplenishFundAsync(Guid companyId, Guid fundId, Guid bankGlAccountId, DateTime date, Guid userId, CancellationToken ct = default) =>
            _db.InTransactionAsync(async token =>
            {
                var fund = await _db.PettyCashFunds.FirstOrDefaultAsync(f => f.Id == fundId && f.CompanyId == companyId, token) ?? throw new InvalidOperationException("Fund not found.");
                var onHand = await GlDebitBalanceAsync(companyId, new[] { fund.GlAccountId }, date.Date, token);
                var topUp = fund.ImprestAmount - onHand;
                if (topUp <= 0) throw new InvalidOperationException("The fund is already at its imprest level.");
                var je = await _gl.PostAsync(companyId, date, JournalSource.Bank, $"Petty cash replenishment: {fund.Name}", $"PCR-{fund.Id.ToString("N")[..6]}-{date:yyyyMMdd}",
                    new[] { new GlLineRequest(fund.GlAccountId, topUp, 0, "USD", 1m, null, "Replenish to imprest"), new GlLineRequest(bankGlAccountId, 0, topUp, "USD", 1m, null, "Cash to petty cash") }, token, userId);
                _audit.Record(companyId, userId, "PettyCash.Replenished", "Bank", nameof(PettyCashFund), fund.Id, null, new { topUp });
                return je;
            }, ct);

        public Task<List<PettyCashVoucher>> ListVouchersAsync(Guid companyId, CancellationToken ct = default) =>
            _db.PettyCashVouchers.Where(v => v.CompanyId == companyId).OrderByDescending(v => v.VoucherDate).Take(300).ToListAsync(ct);
        public Task<List<PettyCashFund>> ListFundsAsync(Guid companyId, CancellationToken ct = default) =>
            _db.PettyCashFunds.Where(f => f.CompanyId == companyId).OrderBy(f => f.Name).ToListAsync(ct);

        // ---------------------------------------------------------------------------------------------
        // Cash forecast (spec 18)
        // ---------------------------------------------------------------------------------------------
        public async Task<object> ForecastAsync(Guid companyId, DateTime asOf, int weeks, CancellationToken ct = default)
        {
            weeks = Math.Clamp(weeks, 1, 26);
            var opening = await _db.BankAccounts.Where(b => b.CompanyId == companyId).SumAsync(b => (decimal?)b.CurrentBalance, ct) ?? 0m;
            var rec = (await _db.ArInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct)).Select(i => (i.DueDate, i.Outstanding * i.ExchangeRate)).ToList();
            var pay = (await _db.ApInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct)).Select(i => (i.DueDate, i.Outstanding * i.ExchangeRate)).ToList();
            var rows = ValuationRules.CashForecast(opening, asOf, rec, pay, weeks);
            return new
            {
                AsOf = asOf.Date, OpeningCash = opening, Basis = "Open receivables and payables by due date; overdue items fall in week 1; excludes payroll, tax and other unscheduled flows.",
                Weeks = rows.Select(r => new { r.Week, r.Inflow, r.Outflow, r.Net, r.Closing }), LowestClosing = rows.Min(r => r.Closing)
            };
        }
    }
}
