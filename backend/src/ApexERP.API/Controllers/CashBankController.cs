using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    public record ImportBankLineRequest(DateTime TransactionDate, string Description, decimal Debit, decimal Credit, string? Reference);

    [Route("api/v1/cashbank")]
    public class CashBankController : ApiControllerBase
    {
        private readonly ApexErpDbContext _db;
        private readonly GlPostingService _gl;

        public CashBankController(ApexErpDbContext db, GlPostingService gl)
        {
            _db = db;
            _gl = gl;
        }

        [HttpGet("accounts")]
        public async Task<IActionResult> GetAccounts() => Ok(await _db.BankAccounts.Where(b => b.CompanyId == CompanyId).ToListAsync());

        [HttpPost("accounts")]
        public async Task<IActionResult> CreateAccount([FromBody] BankAccount b)
        {
            b.CompanyId = CompanyId;
            _db.BankAccounts.Add(b);
            await _db.SaveChangesAsync();
            return Ok(b);
        }

        [HttpGet("accounts/{bankAccountId}/transactions")]
        public async Task<IActionResult> GetTransactions(Guid bankAccountId) =>
            Ok(await _db.BankTransactions.Where(t => t.CompanyId == CompanyId && t.BankAccountId == bankAccountId)
                .OrderByDescending(t => t.TransactionDate).ToListAsync());

        /// <summary>Imports bank statement lines (from CBZ/Stanbic/FBC/Ecobank/NMB CSV export --
        /// parsing handled client-side or by a dedicated importer; this endpoint accepts the
        /// normalised rows) and attempts auto-matching against existing unreconciled GL bank
        /// movements by date + amount, per PRD's "&gt;=80% auto-matching rate" target.</summary>
        [HttpPost("accounts/{bankAccountId}/import")]
        public async Task<IActionResult> ImportStatementLines(Guid bankAccountId, List<ImportBankLineRequest> lines)
        {
            var bankAccount = await _db.BankAccounts.FirstOrDefaultAsync(b => b.Id == bankAccountId && b.CompanyId == CompanyId)
                ?? throw new InvalidOperationException("Bank account not found.");

            var imported = new List<BankTransaction>();
            var candidateSystemTxns = await _db.BankTransactions
                .Where(t => t.CompanyId == CompanyId && t.BankAccountId == bankAccountId && !t.IsReconciled)
                .ToListAsync();

            foreach (var line in lines)
            {
                var txn = new BankTransaction
                {
                    CompanyId = CompanyId,
                    BankAccountId = bankAccountId,
                    TransactionDate = line.TransactionDate,
                    Description = line.Description,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    Reference = line.Reference,
                    IsReconciled = false
                };

                // Auto-match: same date (+/- 3 days) and exact amount against an unreconciled system-side movement.
                var match = candidateSystemTxns.FirstOrDefault(s =>
                    Math.Abs((s.TransactionDate - line.TransactionDate).TotalDays) <= 3 &&
                    s.Debit == line.Debit && s.Credit == line.Credit);
                if (match != null)
                {
                    txn.IsReconciled = true;
                    match.IsReconciled = true;
                }

                _db.BankTransactions.Add(txn);
                imported.Add(txn);
            }

            await _db.SaveChangesAsync();
            return Ok(new { imported = imported.Count, autoMatched = imported.Count(t => t.IsReconciled) });
        }

        [HttpPost("transactions/{id}/reconcile")]
        public async Task<IActionResult> ManualReconcile(Guid id)
        {
            var txn = await _db.BankTransactions.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == CompanyId);
            if (txn == null) return NotFound();
            txn.IsReconciled = true;
            await _db.SaveChangesAsync();
            return Ok(txn);
        }
    }
}
