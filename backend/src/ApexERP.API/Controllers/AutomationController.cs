using ApexERP.API.Authorization;
using ApexERP.Application.Security;
using ApexERP.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record CreateAccrualRequest(string Description, decimal Amount, string Currency, DateTime AccrualDate, Guid ExpenseAccountId, Guid AccruedLiabilityAccountId,
        Guid? SupplierId, bool AutoReverse = true, Guid? CostCentreId = null);
    public record CreatePrepaymentRequest(string Description, decimal TotalAmount, string Currency, DateTime StartDate, int Months, Guid PrepaidAssetAccountId, Guid ExpenseAccountId,
        Guid? SupplierId = null, Guid? CostCentreId = null);
    public record CreateContractRequest(string ContractNo, Guid CustomerId, DateTime StartDate, DateTime? EndDate, decimal TransactionPrice, string Currency, Guid ReceivableAccountId,
        Guid ContractLiabilityAccountId, Guid? ContractAssetAccountId, List<ObligationRequest> Obligations);
    public record BillContractRequest(decimal Amount, DateTime? Date);
    public record SatisfyObligationRequest(DateTime? Date);
    public record ModifyContractRequest(string NewContractNo, decimal AdditionalPrice, DateTime NewEnd, string Note);
    public record AssessImpairmentRequest(Guid FixedAssetId, DateTime AssessmentDate, decimal FairValueLessCostsToSell, List<decimal>? AnnualCashFlows, decimal? ValueInUse,
        decimal DiscountRatePct, string Indicators, string Assumptions, Guid LossAccountId);
    public record AssessInventoryRequest(Guid InventoryItemId, DateTime AssessmentDate, decimal EstimatedSellingPrice, decimal CostsToSell, string Kind, decimal? WriteOffQuantity,
        string Reason, Guid WriteDownExpenseAccountId);

    /// <summary>Accruals, prepayments, IFRS 15 revenue, IAS 36 impairment and IAS 2 valuation (APEXERP spec sections 4-6, 19).</summary>
    [Route("api/v1/automation")]
    public class AutomationController : ControlsControllerBase
    {
        private readonly AccrualService _accruals; private readonly PrepaymentService _prepayments; private readonly RevenueService _revenue;
        private readonly ImpairmentService _impairment; private readonly InventoryValuationService _inventory;

        public AutomationController(AccrualService accruals, PrepaymentService prepayments, RevenueService revenue, ImpairmentService impairment, InventoryValuationService inventory)
        { _accruals = accruals; _prepayments = prepayments; _revenue = revenue; _impairment = impairment; _inventory = inventory; }

        // ---- accruals ----
        [HttpGet("accruals")] public async Task<IActionResult> Accruals() => Ok(await _accruals.ListAsync(CompanyId));
        [HttpGet("accruals/ageing")] public async Task<IActionResult> AccrualAgeing([FromQuery] DateTime? asOf) => Ok(await _accruals.AgeingAsync(CompanyId, DateOrToday(asOf)));
        [HttpPost("accruals")] public Task<IActionResult> CreateAccrual(CreateAccrualRequest r) => Run(async () =>
            Ok(await _accruals.CreateAsync(CompanyId, r.Description, r.Amount, r.Currency, r.AccrualDate, r.ExpenseAccountId, r.AccruedLiabilityAccountId, r.SupplierId, r.AutoReverse, r.CostCentreId, UserId)));
        [HttpPost("accruals/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveAccrual(Guid id) => Run(async () => Ok(await _accruals.ApproveAsync(CompanyId, id, UserId)));
        [HttpPost("accruals/{id:guid}/post")] public Task<IActionResult> PostAccrual(Guid id) => Run(async () => Ok(await _accruals.PostAsync(CompanyId, id, UserId)));
        [HttpPost("accruals/reverse-due")] public Task<IActionResult> ReverseDue([FromQuery] DateTime? asOf) => Run(async () => Ok(await _accruals.ReverseDueAsync(CompanyId, DateOrToday(asOf), UserId)));
        [HttpPost("accruals/{id:guid}/match/{invoiceId:guid}")] public Task<IActionResult> MatchAccrual(Guid id, Guid invoiceId) => Run(async () => Ok(await _accruals.MatchToInvoiceAsync(CompanyId, id, invoiceId, UserId)));

        // ---- prepayments ----
        [HttpGet("prepayments")] public async Task<IActionResult> Prepayments() => Ok(await _prepayments.ListAsync(CompanyId));
        [HttpGet("prepayments/{id:guid}/balance")] public Task<IActionResult> PrepaymentBalance(Guid id) => Run(async () => Ok(await _prepayments.BalanceAsync(CompanyId, id)));
        [HttpPost("prepayments")] public Task<IActionResult> CreatePrepayment(CreatePrepaymentRequest r) => Run(async () =>
            Ok(await _prepayments.CreateAsync(CompanyId, r.Description, r.TotalAmount, r.Currency, r.StartDate, r.Months, r.PrepaidAssetAccountId, r.ExpenseAccountId, r.SupplierId, r.CostCentreId, UserId)));
        [HttpPost("prepayments/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApprovePrepayment(Guid id) => Run(async () => Ok(await _prepayments.ApproveAsync(CompanyId, id, UserId)));
        [HttpPost("prepayments/run-due")] public Task<IActionResult> RunPrepayments([FromQuery] DateTime? asOf) => Run(async () => Ok(await _prepayments.RunDueAsync(CompanyId, DateOrToday(asOf), UserId)));

        // ---- revenue (IFRS 15) ----
        [HttpGet("revenue/contracts")] public async Task<IActionResult> Contracts() => Ok(await _revenue.ListAsync(CompanyId));
        [HttpGet("revenue/position")] public async Task<IActionResult> RevenuePosition() => Ok(await _revenue.PositionAsync(CompanyId));
        [HttpPost("revenue/contracts")] public Task<IActionResult> CreateContract(CreateContractRequest r) => Run(async () =>
            Ok(await _revenue.CreateContractAsync(CompanyId, r.ContractNo, r.CustomerId, r.StartDate, r.EndDate, r.TransactionPrice, r.Currency, r.ReceivableAccountId,
                r.ContractLiabilityAccountId, r.ContractAssetAccountId, r.Obligations ?? new List<ObligationRequest>(), UserId)));
        [HttpPost("revenue/contracts/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveContract(Guid id) => Run(async () => Ok(await _revenue.ApproveAsync(CompanyId, id, UserId)));
        [HttpPost("revenue/contracts/{id:guid}/bill")] public Task<IActionResult> Bill(Guid id, BillContractRequest r) => Run(async () => Ok(await _revenue.BillAsync(CompanyId, id, r.Amount, DateOrToday(r.Date), UserId)));
        [HttpPost("revenue/contracts/{id:guid}/obligations/{obligationId:guid}/satisfy")]
        public Task<IActionResult> Satisfy(Guid id, Guid obligationId, SatisfyObligationRequest r) => Run(async () => Ok(await _revenue.SatisfyPointInTimeAsync(CompanyId, id, obligationId, DateOrToday(r.Date), UserId)));
        [HttpPost("revenue/contracts/{id:guid}/modify")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> Modify(Guid id, ModifyContractRequest r) => Run(async () => Ok(await _revenue.ModifyAsync(CompanyId, id, r.NewContractNo, r.AdditionalPrice, r.NewEnd, r.Note, UserId)));
        [HttpPost("revenue/recognise-due")] public Task<IActionResult> RecogniseDue([FromQuery] DateTime? asOf) => Run(async () => Ok(await _revenue.RecogniseDueAsync(CompanyId, DateOrToday(asOf), UserId)));

        // ---- impairment (IAS 36) ----
        [HttpGet("impairments")] public async Task<IActionResult> Impairments() => Ok(await _impairment.ListAsync(CompanyId));
        [HttpPost("impairments")] public Task<IActionResult> AssessImpairment(AssessImpairmentRequest r) => Run(async () =>
            Ok(await _impairment.AssessAsync(CompanyId, r.FixedAssetId, r.AssessmentDate, r.FairValueLessCostsToSell, r.AnnualCashFlows, r.ValueInUse, r.DiscountRatePct, r.Indicators, r.Assumptions, r.LossAccountId, UserId)));
        [HttpPost("impairments/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveImpairment(Guid id) => Run(async () => Ok(await _impairment.ApproveAsync(CompanyId, id, UserId)));
        [HttpPost("impairments/{id:guid}/post")] public Task<IActionResult> PostImpairment(Guid id) => Run(async () => Ok(await _impairment.PostAsync(CompanyId, id, UserId)));

        // ---- inventory valuation (IAS 2) ----
        [HttpGet("inventory-valuation")] public async Task<IActionResult> InventoryAssessments() => Ok(await _inventory.ListAsync(CompanyId));
        [HttpPost("inventory-valuation")] public Task<IActionResult> AssessInventory(AssessInventoryRequest r) => Run(async () =>
            Ok(await _inventory.AssessAsync(CompanyId, r.InventoryItemId, r.AssessmentDate, r.EstimatedSellingPrice, r.CostsToSell, r.Kind, r.WriteOffQuantity, r.Reason, r.WriteDownExpenseAccountId, UserId)));
        [HttpPost("inventory-valuation/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveInventory(Guid id) => Run(async () => Ok(await _inventory.ApproveAsync(CompanyId, id, UserId)));
        [HttpPost("inventory-valuation/{id:guid}/post")] public Task<IActionResult> PostInventory(Guid id) => Run(async () => Ok(await _inventory.PostAsync(CompanyId, id, UserId)));
    }

    public record CreditOverrideRequest(Guid CustomerId, decimal TemporaryLimit, string Reason, DateTime ExpiresAt);
    public record CreateCreditNoteRequest(Guid ArInvoiceId, decimal Amount, string Reason, Guid RevenueReversalAccountId, Guid ArControlAccountId, Guid? VatOutputAccountId);
    public record LossRateBucket(int FromDays, int ToDays, decimal LossRatePct);
    public record ReplaceLossRatesRequest(List<LossRateBucket> Buckets);
    public record CalculateEclRequest(DateTime AsOf, Guid BadDebtExpenseAccountId, Guid AllowanceAccountId);

    /// <summary>Credit control, credit notes and expected credit loss (spec 17).</summary>
    [Route("api/v1/ar/controls")]
    public class ArControlsController : ControlsControllerBase
    {
        private readonly ReceivablesControlService _svc;
        public ArControlsController(ReceivablesControlService svc) { _svc = svc; }

        [HttpGet("statement/{customerId:guid}")] public Task<IActionResult> Statement(Guid customerId, [FromQuery] DateTime? asOf) => Run(async () => Ok(await _svc.StatementAsync(CompanyId, customerId, DateOrToday(asOf))));
        [HttpPost("credit-overrides")] public Task<IActionResult> RequestOverride(CreditOverrideRequest r) => Run(async () => Ok(await _svc.RequestOverrideAsync(CompanyId, r.CustomerId, r.TemporaryLimit, r.Reason, r.ExpiresAt, UserId)));
        [HttpPost("credit-overrides/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveOverride(Guid id) => Run(async () => Ok(await _svc.ApproveOverrideAsync(CompanyId, id, UserId)));

        [HttpGet("credit-notes")] public async Task<IActionResult> CreditNotes() => Ok(await _svc.ListCreditNotesAsync(CompanyId));
        [HttpPost("credit-notes")] public Task<IActionResult> CreateCreditNote(CreateCreditNoteRequest r) => Run(async () =>
            Ok(await _svc.CreateCreditNoteAsync(CompanyId, r.ArInvoiceId, r.Amount, r.Reason, r.RevenueReversalAccountId, r.ArControlAccountId, r.VatOutputAccountId, UserId)));
        [HttpPost("credit-notes/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveCreditNote(Guid id) => Run(async () => Ok(await _svc.ApproveCreditNoteAsync(CompanyId, id, UserId)));
        [HttpPost("credit-notes/{id:guid}/post")] public Task<IActionResult> PostCreditNote(Guid id) => Run(async () => Ok(await _svc.PostCreditNoteAsync(CompanyId, id, UserId)));

        [HttpGet("ecl/runs")] public async Task<IActionResult> EclRuns() => Ok(await _svc.ListRunsAsync(CompanyId));
        [HttpPut("ecl/loss-rates")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ReplaceRates(ReplaceLossRatesRequest r) => Run(async () => Ok(await _svc.ReplaceLossRatesAsync(CompanyId, r.Buckets.Select(b => (b.FromDays, b.ToDays, b.LossRatePct)).ToList(), UserId)));
        [HttpPost("ecl/calculate")] public Task<IActionResult> CalculateEcl(CalculateEclRequest r) => Run(async () => Ok(await _svc.CalculateAsync(CompanyId, r.AsOf, r.BadDebtExpenseAccountId, r.AllowanceAccountId, UserId)));
        [HttpPost("ecl/runs/{id:guid}/approve")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> ApproveEcl(Guid id) => Run(async () => Ok(await _svc.ApproveRunAsync(CompanyId, id, UserId)));
        [HttpPost("ecl/runs/{id:guid}/post")] public Task<IActionResult> PostEcl(Guid id) => Run(async () => Ok(await _svc.PostRunAsync(CompanyId, id, UserId)));
    }

    public record CreatePaymentRunItem(Guid InvoiceId, decimal Amount);
    public record CreatePaymentRunRequest(DateTime PaymentDate, string Currency, Guid BankGlAccountId, Guid ApControlAccountId, List<CreatePaymentRunItem> Items);
    public record BankChangeRequest(Guid SupplierId, string BankName, string AccountNo, string? Branch);
    public record VerifyBankChangeRequest(string Note, bool Approve);
    public record OverrideMatchRequest(string Reason);
    public record StatementLineDto(string Reference, decimal Amount);
    public record ReconcileStatementRequest(Guid SupplierId, DateTime AsOf, List<StatementLineDto> Lines);

    /// <summary>Payment runs, independent authorisation, supplier bank-detail control, three-way match and statement reconciliation (spec 16).</summary>
    [Route("api/v1/ap/controls")]
    public class ApControlsController : ControlsControllerBase
    {
        private readonly ApControlsService _svc; private readonly ApMatchService _match; private readonly ApexERP.Infrastructure.Data.ApexErpDbContext _db;
        public ApControlsController(ApControlsService svc, ApMatchService match, ApexERP.Infrastructure.Data.ApexErpDbContext db) { _svc = svc; _match = match; _db = db; }

        [HttpGet("payment-runs")] public async Task<IActionResult> Runs() => Ok(await _svc.ListRunsAsync(CompanyId));
        [HttpPost("payment-runs")] [HasPermission(Permissions.ApPaymentsCreate)]
        public Task<IActionResult> CreateRun(CreatePaymentRunRequest r) => Run(async () =>
            Ok(await _svc.CreateRunAsync(CompanyId, r.PaymentDate, r.Currency, r.BankGlAccountId, r.ApControlAccountId, r.Items.Select(i => (i.InvoiceId, i.Amount)).ToList(), UserId)));
        [HttpPost("payment-runs/{id:guid}/authorise")] [HasPermission(Permissions.ApPaymentsAuthorise)]
        public Task<IActionResult> Authorise(Guid id) => Run(async () => Ok(await _svc.AuthoriseRunAsync(CompanyId, id, UserId)));
        [HttpPost("payment-runs/{id:guid}/release")] [HasPermission(Permissions.ApPaymentsCreate)]
        public Task<IActionResult> Release(Guid id) => Run(async () => Ok(await _svc.ReleaseRunAsync(CompanyId, id, UserId)));
        [HttpPost("payment-runs/{id:guid}/cancel")] [HasPermission(Permissions.ApPaymentsCreate)]
        public Task<IActionResult> Cancel(Guid id) => Run(async () => { await _svc.CancelRunAsync(CompanyId, id, UserId); return Ok(new { cancelled = true }); });

        [HttpGet("bank-changes")] public async Task<IActionResult> BankChanges() => Ok(await _svc.ListBankChangesAsync(CompanyId));
        [HttpPost("bank-changes")] public Task<IActionResult> RequestChange(BankChangeRequest r) => Run(async () => Ok(await _svc.RequestBankChangeAsync(CompanyId, r.SupplierId, r.BankName, r.AccountNo, r.Branch, UserId)));
        [HttpPost("bank-changes/{id:guid}/verify")] [HasPermission(Permissions.ApSuppliersVerifyBank)]
        public Task<IActionResult> VerifyChange(Guid id, VerifyBankChangeRequest r) => Run(async () => Ok(await _svc.VerifyBankChangeAsync(CompanyId, id, UserId, r.Note, r.Approve)));

        [HttpPost("invoices/{id:guid}/match")] public Task<IActionResult> Match(Guid id) => Run(async () =>
        {
            var inv = await _db.ApInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == CompanyId);
            if (inv == null) return NotFound(new { error = "Invoice not found." });
            var result = await _match.EvaluateAndStoreAsync(CompanyId, inv);
            await _db.SaveChangesAsync();
            return Ok(new { outcome = result.Outcome.ToString(), paymentAllowed = result.PaymentAllowed, issues = result.Issues });
        });
        [HttpPost("invoices/{id:guid}/match-override")] [HasPermission(Permissions.ControlsApprove)]
        public Task<IActionResult> OverrideMatch(Guid id, OverrideMatchRequest r) => Run(async () => Ok(await _match.OverrideAsync(CompanyId, id, UserId, r.Reason)));

        [HttpPost("statement-reconciliation")] public Task<IActionResult> ReconcileStatement(ReconcileStatementRequest r) => Run(async () =>
            Ok(await _svc.ReconcileStatementAsync(CompanyId, r.SupplierId, r.Lines.Select(l => new StatementLine(l.Reference, l.Amount)).ToList(), r.AsOf)));
        [HttpGet("withholding-schedule")] public async Task<IActionResult> Wht([FromQuery] DateTime from, [FromQuery] DateTime to) => Ok(await _svc.WithholdingScheduleAsync(CompanyId, from, to));
    }
}
