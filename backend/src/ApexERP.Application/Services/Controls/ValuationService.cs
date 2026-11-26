using System.Text.Json;
using ApexERP.Domain.Common;
using ApexERP.Domain.Controls;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    // =================================================================================================
    // IAS 36 impairment (spec 6)
    // =================================================================================================
    public class ImpairmentService : PostingServiceBase
    {
        public ImpairmentService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        public async Task<ImpairmentAssessment> AssessAsync(Guid companyId, Guid assetId, DateTime date, decimal fairValueLessCostsToSell,
            IReadOnlyList<decimal>? annualCashFlows, decimal? valueInUseOverride, decimal discountRatePct, string indicators, string assumptions,
            Guid lossAccountId, Guid userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(indicators)) throw new InvalidOperationException("Record the impairment indicators that triggered the assessment.");
            if (string.IsNullOrWhiteSpace(assumptions)) throw new InvalidOperationException("Record the assumptions used (cash flows, discount rate, fair-value source).");
            var asset = await Db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Asset not found.");
            if (asset.Status != AssetStatus.Active) throw new InvalidOperationException("Only active assets can be assessed.");
            await EnsureAccountsAsync(companyId, ct, lossAccountId);

            var viu = valueInUseOverride ?? (annualCashFlows is { Count: > 0 } ? ValuationRules.PresentValue(annualCashFlows, discountRatePct) : 0m);
            var (recoverable, loss) = ValuationRules.Impairment(asset.CurrentNbv, fairValueLessCostsToSell, viu);
            var a = new ImpairmentAssessment
            {
                CompanyId = companyId, FixedAssetId = assetId, AssessmentDate = date.Date, CarryingAmount = asset.CurrentNbv, FairValueLessCostsToSell = fairValueLessCostsToSell,
                ValueInUse = viu, DiscountRatePct = discountRatePct, RecoverableAmount = recoverable, ImpairmentLoss = loss, Indicators = indicators, Assumptions = assumptions,
                ImpairmentLossAccountId = lossAccountId, CreatedBy = userId
            };
            Db.ImpairmentAssessments.Add(a);
            Audit.Record(companyId, userId, "Impairment.Assessed", "FixedAssets", nameof(ImpairmentAssessment), a.Id, null, new { assetId, loss, recoverable });
            await Db.SaveChangesAsync(ct);
            return a;
        }

        public async Task<ImpairmentAssessment> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var a = await Load(companyId, id, ct);
            if (a.Status != "Draft") throw new InvalidOperationException("Only draft assessments can be approved.");
            await RequireApprovalAsync(companyId, approver, "Impairment", a.ImpairmentLoss, a.CreatedBy, ct);
            a.Status = "Approved"; a.ApprovedBy = approver;
            Audit.Record(companyId, approver, "Impairment.Approved", "FixedAssets", nameof(ImpairmentAssessment), a.Id, null, new { a.ImpairmentLoss });
            await Db.SaveChangesAsync(ct); return a;
        }

        public Task<ImpairmentAssessment> PostAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var a = await Load(companyId, id, token);
                if (a.Status != "Approved") throw new InvalidOperationException("Only approved assessments can be posted.");
                if (a.ImpairmentLoss <= 0) { a.Status = "Posted"; await Db.SaveChangesAsync(token); return a; }   // nothing to book: recoverable amount covers carrying amount
                var asset = await Db.FixedAssets.Include(x => x.Category).FirstAsync(x => x.Id == a.FixedAssetId && x.CompanyId == companyId, token);
                if (asset.CurrentNbv < a.ImpairmentLoss) throw new InvalidOperationException("The asset's carrying amount has fallen since the assessment; reassess.");
                var je = await Gl.PostAsync(companyId, a.AssessmentDate, JournalSource.Impairment, $"Impairment of {asset.AssetNo}", $"IMP-{a.Id.ToString("N")[..8]}",
                    new[]
                    {
                        new GlLineRequest(a.ImpairmentLossAccountId!.Value, a.ImpairmentLoss, 0, "USD", 1m, asset.CostCentreId, "Impairment loss"),
                        new GlLineRequest(asset.Category!.AccumulatedDepreciationAccountId, 0, a.ImpairmentLoss, "USD", 1m, null, "Accumulated impairment")
                    }, token, userId);
                asset.AccumulatedDepreciation += a.ImpairmentLoss; asset.CurrentNbv -= a.ImpairmentLoss;
                a.Status = "Posted"; a.JournalEntryId = je.Id;
                Audit.Record(companyId, userId, "Impairment.Posted", "FixedAssets", nameof(ImpairmentAssessment), a.Id, null, new { je.EntryNo, a.ImpairmentLoss });
                await Db.SaveChangesAsync(token); return a;
            }, ct);

        public Task<List<ImpairmentAssessment>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            Db.ImpairmentAssessments.Where(a => a.CompanyId == companyId).OrderByDescending(a => a.AssessmentDate).Take(300).ToListAsync(ct);

        private async Task<ImpairmentAssessment> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await Db.ImpairmentAssessments.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Assessment not found.");
    }

    // =================================================================================================
    // IAS 2 NRV, obsolescence and approved write-offs (spec 19)
    // =================================================================================================
    public class InventoryValuationService : PostingServiceBase
    {
        public InventoryValuationService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        public async Task<InventoryNrvAssessment> AssessAsync(Guid companyId, Guid itemId, DateTime date, decimal estimatedSellingPrice, decimal costsToSell,
            string kind, decimal? writeOffQuantity, string reason, Guid writeDownExpenseAccountId, Guid userId, CancellationToken ct = default)
        {
            if (kind is not ("NrvWriteDown" or "Obsolescence" or "WriteOff")) throw new InvalidOperationException("Kind must be NrvWriteDown, Obsolescence or WriteOff.");
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required.");
            var item = await Db.InventoryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Item not found.");
            if (item.QuantityOnHand <= 0) throw new InvalidOperationException("There is no stock to assess.");
            await EnsureAccountsAsync(companyId, ct, writeDownExpenseAccountId);

            decimal qty = item.QuantityOnHand, writeDown, nrv;
            if (kind == "WriteOff")
            {
                qty = writeOffQuantity ?? item.QuantityOnHand;
                if (qty <= 0 || qty > item.QuantityOnHand) throw new InvalidOperationException("The write-off quantity must be between 0 and the quantity on hand.");
                nrv = 0m; writeDown = Math.Round(qty * item.AverageCost, 2, MidpointRounding.ToEven);
            }
            else if (kind == "Obsolescence")
            {   // obsolescence provision: written down to scrap value (selling price net of costs), floor 0
                nrv = Math.Max(0m, estimatedSellingPrice - costsToSell);
                writeDown = nrv >= item.AverageCost ? 0m : Math.Round((item.AverageCost - nrv) * qty, 2, MidpointRounding.ToEven);
            }
            else (nrv, writeDown) = ValuationRules.NrvWriteDown(qty, item.AverageCost, estimatedSellingPrice, costsToSell);

            var a = new InventoryNrvAssessment
            {
                CompanyId = companyId, InventoryItemId = itemId, AssessmentDate = date.Date, Quantity = qty, UnitCost = item.AverageCost, EstimatedSellingPrice = estimatedSellingPrice,
                CostsToSell = costsToSell, NetRealisableValue = nrv, WriteDownAmount = writeDown, Kind = kind, Reason = reason, PreparedBy = userId, CreatedBy = userId,
                WriteDownExpenseAccountId = writeDownExpenseAccountId
            };
            Db.InventoryNrvAssessments.Add(a);
            Audit.Record(companyId, userId, "InventoryAssessment.Created", "Inventory", nameof(InventoryNrvAssessment), a.Id, null, new { kind, writeDown });
            await Db.SaveChangesAsync(ct);
            return a;
        }

        public async Task<InventoryNrvAssessment> ApproveAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var a = await Load(companyId, id, ct);
            if (a.Status != "Draft") throw new InvalidOperationException("Only draft assessments can be approved.");
            await RequireApprovalAsync(companyId, approver, "InventoryWriteOff", a.WriteDownAmount, a.PreparedBy, ct);
            a.Status = "Approved"; a.ApprovedBy = approver;
            Audit.Record(companyId, approver, "InventoryAssessment.Approved", "Inventory", nameof(InventoryNrvAssessment), a.Id, null, new { a.WriteDownAmount });
            await Db.SaveChangesAsync(ct); return a;
        }

        public Task<InventoryNrvAssessment> PostAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var a = await Load(companyId, id, token);
                if (a.Status != "Approved") throw new InvalidOperationException("Only approved assessments can be posted.");
                var item = await Db.InventoryItems.FirstAsync(i => i.Id == a.InventoryItemId && i.CompanyId == companyId, token);
                if (a.WriteDownAmount <= 0) { a.Status = "Posted"; await Db.SaveChangesAsync(token); return a; }
                if (item.QuantityOnHand < a.Quantity) throw new InvalidOperationException("Stock on hand has fallen below the assessed quantity; reassess.");

                var je = await Gl.PostAsync(companyId, a.AssessmentDate, JournalSource.Inventory, $"{a.Kind}: {item.Name}", $"INV-{a.Kind}-{a.Id.ToString("N")[..8]}",
                    new[]
                    {
                        new GlLineRequest(a.WriteDownExpenseAccountId!.Value, a.WriteDownAmount, 0, "USD", 1m, null, a.Reason),
                        new GlLineRequest(item.AssetAccountId, 0, a.WriteDownAmount, "USD", 1m, null, "Inventory written down")
                    }, token, userId);

                var layers = await Db.InventoryCostLayers.Where(l => l.CompanyId == companyId && l.InventoryItemId == item.Id && l.RemainingQuantity > 0).OrderBy(l => l.ReceiptDate).ToListAsync(token);
                if (a.Kind == "WriteOff")
                {
                    var left = a.Quantity;
                    foreach (var l in layers) { if (left <= 0) break; var take = Math.Min(l.RemainingQuantity, left); l.RemainingQuantity -= take; left -= take; }
                    item.QuantityOnHand -= a.Quantity;     // average cost is unchanged: both value and quantity fall at cost
                }
                else
                {
                    var totalValue = item.QuantityOnHand * item.AverageCost;
                    var factor = totalValue <= 0 ? 1m : (totalValue - a.WriteDownAmount) / totalValue;
                    item.AverageCost = Math.Round(item.AverageCost * factor, 6);
                    foreach (var l in layers) l.UnitCost = Math.Round(l.UnitCost * factor, 6);
                }
                Db.InventoryMovements.Add(new InventoryMovement
                {
                    CompanyId = companyId, InventoryItemId = item.Id, MovementDate = a.AssessmentDate, MovementType = MovementType.Adjustment,
                    Quantity = a.Kind == "WriteOff" ? -a.Quantity : 0m, UnitCost = item.AverageCost, Reference = a.Kind, JournalEntryId = je.Id
                });
                a.Status = "Posted"; a.JournalEntryId = je.Id;
                Audit.Record(companyId, userId, "InventoryAssessment.Posted", "Inventory", nameof(InventoryNrvAssessment), a.Id, null, new { je.EntryNo });
                await Db.SaveChangesAsync(token); return a;
            }, ct);

        public Task<List<InventoryNrvAssessment>> ListAsync(Guid companyId, CancellationToken ct = default) =>
            Db.InventoryNrvAssessments.Where(a => a.CompanyId == companyId).OrderByDescending(a => a.AssessmentDate).Take(300).ToListAsync(ct);

        private async Task<InventoryNrvAssessment> Load(Guid companyId, Guid id, CancellationToken ct) =>
            await Db.InventoryNrvAssessments.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Assessment not found.");
    }

    // =================================================================================================
    // IFRS 9 expected credit loss, credit limits, credit notes (spec 17)
    // =================================================================================================
    public class ReceivablesControlService : PostingServiceBase
    {
        public ReceivablesControlService(ApexErpDbContext db, GlPostingService gl, GovernanceService gov, AuditService audit) : base(db, gl, gov, audit) { }

        // ---- credit limits ----
        public async Task EnsureWithinCreditLimitAsync(Guid companyId, Guid customerId, decimal newAmount, CancellationToken ct = default)
        {
            var c = await Db.Customers.FirstOrDefaultAsync(x => x.Id == customerId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Customer not found.");
            var open = await Db.ArInvoices.Where(i => i.CompanyId == companyId && i.CustomerId == customerId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled).SumAsync(i => (decimal?)i.Outstanding, ct) ?? 0m;
            var over = await Db.CreditLimitOverrides.Where(o => o.CompanyId == companyId && o.CustomerId == customerId && o.ApprovedBy != null && o.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(o => o.TemporaryLimit).Select(o => (decimal?)o.TemporaryLimit).FirstOrDefaultAsync(ct);
            if (!ValuationRules.WithinCreditLimit(c.CreditLimit, over, open, newAmount))
                throw new InvalidOperationException($"This sale would take {c.Name} to {(open + newAmount):N2}, above the credit limit of {Math.Max(c.CreditLimit, over ?? 0m):N2}. Request an approved credit-limit override first.");
        }

        public async Task<CreditLimitOverride> RequestOverrideAsync(Guid companyId, Guid customerId, decimal limit, string reason, DateTime expiresAt, Guid userId, CancellationToken ct = default)
        {
            if (limit <= 0 || expiresAt <= DateTime.UtcNow) throw new InvalidOperationException("Provide a positive limit and a future expiry.");
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required.");
            var o = new CreditLimitOverride { CompanyId = companyId, CustomerId = customerId, TemporaryLimit = limit, Reason = reason, ExpiresAt = expiresAt, CreatedBy = userId };
            Db.CreditLimitOverrides.Add(o);
            Audit.Record(companyId, userId, "CreditLimit.OverrideRequested", "AR", nameof(CreditLimitOverride), o.Id, null, new { limit, expiresAt });
            await Db.SaveChangesAsync(ct); return o;
        }

        public async Task<CreditLimitOverride> ApproveOverrideAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var o = await Db.CreditLimitOverrides.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Override not found.");
            await RequireApprovalAsync(companyId, approver, "CreditLimitOverride", o.TemporaryLimit, o.CreatedBy, ct);
            o.ApprovedBy = approver;
            Audit.Record(companyId, approver, "CreditLimit.OverrideApproved", "AR", nameof(CreditLimitOverride), o.Id, null, new { o.TemporaryLimit });
            await Db.SaveChangesAsync(ct); return o;
        }

        // ---- credit notes ----
        public async Task<ArCreditNote> CreateCreditNoteAsync(Guid companyId, Guid invoiceId, decimal amount, string reason, Guid revenueReversalAccountId, Guid arControlAccountId,
            Guid? vatOutputAccountId, Guid userId, CancellationToken ct = default)
        {
            var inv = await Db.ArInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Invoice not found.");
            if (inv.Status is InvoiceStatus.Draft or InvoiceStatus.Cancelled) throw new InvalidOperationException("Only posted invoices can be credited.");
            if (amount <= 0 || amount > inv.Outstanding) throw new InvalidOperationException("The credit amount must be positive and no more than the invoice's outstanding balance.");
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required.");
            if (inv.VatAmount > 0 && vatOutputAccountId == null) throw new InvalidOperationException("This invoice carries VAT; supply the VAT output account.");
            await EnsureAccountsAsync(companyId, ct, revenueReversalAccountId, arControlAccountId, vatOutputAccountId);
            var count = await Db.ArCreditNotes.IgnoreQueryFilters().CountAsync(n => n.CompanyId == companyId, ct);
            var cn = new ArCreditNote
            {
                CompanyId = companyId, CreditNoteNo = $"CN-{DateTime.UtcNow:yyyy}-{count + 1:D5}", ArInvoiceId = invoiceId, CustomerId = inv.CustomerId, Amount = amount, Reason = reason,
                PreparedBy = userId, CreatedBy = userId, RevenueReversalAccountId = revenueReversalAccountId, ArControlAccountId = arControlAccountId, VatOutputAccountId = vatOutputAccountId
            };
            Db.ArCreditNotes.Add(cn);
            Audit.Record(companyId, userId, "CreditNote.Created", "AR", nameof(ArCreditNote), cn.Id, null, new { cn.CreditNoteNo, amount });
            await Db.SaveChangesAsync(ct); return cn;
        }

        public async Task<ArCreditNote> ApproveCreditNoteAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var cn = await Db.ArCreditNotes.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Credit note not found.");
            if (cn.Status != "Draft") throw new InvalidOperationException("Only draft credit notes can be approved.");
            await RequireApprovalAsync(companyId, approver, "ArCreditNote", cn.Amount, cn.PreparedBy, ct);
            cn.Status = "Approved"; cn.ApprovedBy = approver;
            Audit.Record(companyId, approver, "CreditNote.Approved", "AR", nameof(ArCreditNote), cn.Id, null, new { cn.Amount });
            await Db.SaveChangesAsync(ct); return cn;
        }

        public Task<ArCreditNote> PostCreditNoteAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var cn = await Db.ArCreditNotes.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, token) ?? throw new InvalidOperationException("Credit note not found.");
                if (cn.Status != "Approved") throw new InvalidOperationException("Only approved credit notes can be posted.");
                var inv = await Db.ArInvoices.FirstAsync(i => i.Id == cn.ArInvoiceId && i.CompanyId == companyId, token);
                if (cn.Amount > inv.Outstanding) throw new InvalidOperationException("The invoice's outstanding balance is now lower than the credit amount.");
                var vat = inv.VatAmount > 0 && inv.Total > 0 ? Math.Round(cn.Amount * inv.VatAmount / inv.Total, 2, MidpointRounding.ToEven) : 0m;
                var net = cn.Amount - vat;
                var lines = new List<GlLineRequest> { new(cn.RevenueReversalAccountId!.Value, net, 0, inv.Currency, inv.ExchangeRate, null, $"Credit note {cn.CreditNoteNo}") };
                if (vat > 0) lines.Add(new GlLineRequest(cn.VatOutputAccountId!.Value, vat, 0, inv.Currency, inv.ExchangeRate, null, "VAT output reversed"));
                lines.Add(new GlLineRequest(cn.ArControlAccountId!.Value, 0, cn.Amount, inv.Currency, inv.ExchangeRate, null, $"Receivable reduced: {inv.InvoiceNo}"));
                var je = await Gl.PostAsync(companyId, DateTime.UtcNow.Date, JournalSource.AR, $"Credit note {cn.CreditNoteNo}", cn.CreditNoteNo, lines, token, userId);
                inv.Outstanding -= cn.Amount; if (inv.Outstanding == 0) inv.Status = InvoiceStatus.Paid;
                cn.Status = "Posted"; cn.JournalEntryId = je.Id;
                Audit.Record(companyId, userId, "CreditNote.Posted", "AR", nameof(ArCreditNote), cn.Id, null, new { je.EntryNo });
                await Db.SaveChangesAsync(token); return cn;
            }, ct);

        public Task<List<ArCreditNote>> ListCreditNotesAsync(Guid companyId, CancellationToken ct = default) =>
            Db.ArCreditNotes.Where(n => n.CompanyId == companyId).OrderByDescending(n => n.CreatedAt).Take(300).ToListAsync(ct);

        // ---- expected credit loss ----
        public async Task<List<EclLossRate>> ReplaceLossRatesAsync(Guid companyId, IReadOnlyList<(int From, int To, decimal Pct)> buckets, Guid userId, CancellationToken ct = default)
        {
            if (buckets.Count == 0) throw new InvalidOperationException("Provide at least one bucket.");
            var ordered = buckets.OrderBy(b => b.From).ToList();
            if (ordered[0].From != 0) throw new InvalidOperationException("The first bucket must start at 0 days.");
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].To < ordered[i].From || ordered[i].Pct < 0 || ordered[i].Pct > 100) throw new InvalidOperationException("Each bucket needs To >= From and a rate between 0 and 100.");
                if (i > 0 && ordered[i].From != ordered[i - 1].To + 1) throw new InvalidOperationException("Buckets must be contiguous with no gaps or overlaps.");
            }
            foreach (var old in await Db.EclLossRates.Where(r => r.CompanyId == companyId && r.IsActive).ToListAsync(ct)) old.IsActive = false;
            var rows = ordered.Select(b => new EclLossRate { CompanyId = companyId, FromDays = b.From, ToDays = b.To, LossRatePct = b.Pct, EffectiveFrom = DateTime.UtcNow, CreatedBy = userId }).ToList();
            Db.EclLossRates.AddRange(rows);
            Audit.Record(companyId, userId, "Ecl.RatesReplaced", "AR", nameof(EclLossRate), Guid.Empty, null, ordered);
            await Db.SaveChangesAsync(ct); return rows;
        }

        public async Task<EclProvisionRun> CalculateAsync(Guid companyId, DateTime asOf, Guid badDebtExpenseAccountId, Guid allowanceAccountId, Guid userId, CancellationToken ct = default)
        {
            var rates = await Db.EclLossRates.Where(r => r.CompanyId == companyId && r.IsActive).OrderBy(r => r.FromDays).ToListAsync(ct);
            if (rates.Count == 0) throw new InvalidOperationException("Configure the loss-rate matrix first.");
            await EnsureAccountsAsync(companyId, ct, badDebtExpenseAccountId, allowanceAccountId);
            var open = await Db.ArInvoices.Where(i => i.CompanyId == companyId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).ToListAsync(ct);
            var lastBucketEnd = rates.Max(r => r.ToDays);
            var balances = open.Select(i => new ReceivableBalance(i.CustomerId.ToString(), Math.Min(Math.Max(0, (asOf.Date - i.DueDate.Date).Days), lastBucketEnd), i.Outstanding)).ToList();
            var matrix = rates.Select(r => new AgeingBucket(r.FromDays, r.ToDays, r.LossRatePct)).ToList();
            var required = ExpectedCreditLoss.Provision(balances, matrix);
            var previous = await Db.EclProvisionRuns.Where(r => r.CompanyId == companyId && r.Status == "Posted").OrderByDescending(r => r.AsOfDate).Select(r => (decimal?)r.RequiredAllowance).FirstOrDefaultAsync(ct) ?? 0m;
            var run = new EclProvisionRun
            {
                CompanyId = companyId, AsOfDate = asOf.Date, TotalReceivables = open.Sum(i => i.Outstanding), RequiredAllowance = required, PreviousAllowance = previous, Movement = required - previous,
                MatrixSnapshotJson = JsonSerializer.Serialize(matrix), BadDebtExpenseAccountId = badDebtExpenseAccountId, AllowanceAccountId = allowanceAccountId, CreatedBy = userId
            };
            Db.EclProvisionRuns.Add(run);
            Audit.Record(companyId, userId, "Ecl.Calculated", "AR", nameof(EclProvisionRun), run.Id, null, new { required, previous });
            await Db.SaveChangesAsync(ct); return run;
        }

        public async Task<EclProvisionRun> ApproveRunAsync(Guid companyId, Guid id, Guid approver, CancellationToken ct = default)
        {
            var r = await Db.EclProvisionRuns.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Provision run not found.");
            if (r.Status != "Draft") throw new InvalidOperationException("Only draft runs can be approved.");
            await RequireApprovalAsync(companyId, approver, "EclProvision", Math.Abs(r.Movement), r.CreatedBy, ct);
            r.Status = "Approved"; r.ApprovedBy = approver;
            Audit.Record(companyId, approver, "Ecl.Approved", "AR", nameof(EclProvisionRun), r.Id, null, new { r.Movement });
            await Db.SaveChangesAsync(ct); return r;
        }

        public Task<EclProvisionRun> PostRunAsync(Guid companyId, Guid id, Guid userId, CancellationToken ct = default) =>
            Db.InTransactionAsync(async token =>
            {
                var r = await Db.EclProvisionRuns.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, token) ?? throw new InvalidOperationException("Provision run not found.");
                if (r.Status != "Approved") throw new InvalidOperationException("Only approved runs can be posted.");
                if (r.Movement != 0m)
                {
                    var amt = Math.Abs(r.Movement); var increase = r.Movement > 0;
                    var je = await Gl.PostAsync(companyId, r.AsOfDate, JournalSource.Provision, "Expected credit loss allowance", $"ECL-{r.AsOfDate:yyyyMMdd}-{r.Id.ToString("N")[..6]}",
                        new[]
                        {
                            new GlLineRequest(increase ? r.BadDebtExpenseAccountId!.Value : r.AllowanceAccountId!.Value, amt, 0, "USD", 1m, null, "ECL movement"),
                            new GlLineRequest(increase ? r.AllowanceAccountId!.Value : r.BadDebtExpenseAccountId!.Value, 0, amt, "USD", 1m, null, "ECL movement")
                        }, token, userId);
                    r.JournalEntryId = je.Id;
                }
                r.Status = "Posted";
                Audit.Record(companyId, userId, "Ecl.Posted", "AR", nameof(EclProvisionRun), r.Id, null, new { r.Movement });
                await Db.SaveChangesAsync(token); return r;
            }, ct);

        public Task<List<EclProvisionRun>> ListRunsAsync(Guid companyId, CancellationToken ct = default) =>
            Db.EclProvisionRuns.Where(r => r.CompanyId == companyId).OrderByDescending(r => r.AsOfDate).Take(100).ToListAsync(ct);

        /// <summary>Customer statement: open invoices with age, for sending to the customer (spec 17).</summary>
        public async Task<object> StatementAsync(Guid companyId, Guid customerId, DateTime asOf, CancellationToken ct = default)
        {
            var c = await Db.Customers.FirstOrDefaultAsync(x => x.Id == customerId && x.CompanyId == companyId, ct) ?? throw new InvalidOperationException("Customer not found.");
            var open = await Db.ArInvoices.Where(i => i.CompanyId == companyId && i.CustomerId == customerId && i.Outstanding > 0 && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).OrderBy(i => i.InvoiceDate).ToListAsync(ct);
            return new
            {
                Customer = new { c.Id, c.Name, c.Code }, AsOf = asOf.Date, CreditLimit = c.CreditLimit,
                Items = open.Select(i => new { i.InvoiceNo, i.InvoiceDate, i.DueDate, i.Total, i.Outstanding, DaysOverdue = Math.Max(0, (asOf.Date - i.DueDate.Date).Days) }),
                TotalOutstanding = open.Sum(i => i.Outstanding)
            };
        }
    }
}
