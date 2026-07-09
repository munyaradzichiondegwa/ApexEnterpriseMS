using ApexERP.Application.Services;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.API.Controllers
{
    [Route("api/v1/reports")]
    public class ReportingController : ApiControllerBase
    {
        private readonly ReportingService _reports;
        private readonly StatementOfChangesInEquityService _equity;
        private readonly CashFlowStatementService _cashFlow;
        private readonly NotesToFinancialStatementsService _notes;
        private readonly ApexErpDbContext _db;

        public ReportingController(ReportingService reports, StatementOfChangesInEquityService equity,
            CashFlowStatementService cashFlow, NotesToFinancialStatementsService notes, ApexErpDbContext db)
        {
            _reports = reports;
            _equity = equity;
            _cashFlow = cashFlow;
            _notes = notes;
            _db = db;
        }

        [HttpGet("trial-balance")]
        public async Task<IActionResult> GetTrialBalance([FromQuery] DateTime? asOf)
        {
            var lines = await _reports.GetTrialBalanceAsync(CompanyId, asOf ?? DateTime.UtcNow);
            return Ok(new { asOfDate = asOf ?? DateTime.UtcNow, lines, totalDebit = lines.Sum(l => l.Debit), totalCredit = lines.Sum(l => l.Credit) });
        }

        /// <summary>Necessary Financial Report #2: Statement of Profit or Loss and Other Comprehensive Income.</summary>
        [HttpGet("income-statement")]
        public async Task<IActionResult> GetIncomeStatement([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _reports.GetIncomeStatementAsync(CompanyId, from, to);
            return Ok(result);
        }

        /// <summary>Necessary Financial Report #1: Statement of Financial Position.</summary>
        [HttpGet("balance-sheet")]
        public async Task<IActionResult> GetBalanceSheet([FromQuery] DateTime? asOf)
        {
            var result = await _reports.GetBalanceSheetAsync(CompanyId, asOf ?? DateTime.UtcNow);
            return Ok(result);
        }

        /// <summary>Necessary Financial Report #3: Statement of Changes in Equity.</summary>
        [HttpGet("statement-of-changes-in-equity")]
        public async Task<IActionResult> GetStatementOfChangesInEquity([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _equity.GetStatementAsync(CompanyId, from, to);
            return Ok(result);
        }

        /// <summary>Necessary Financial Report #4: Statement of Cash Flows (indirect method).</summary>
        [HttpGet("cash-flow-statement")]
        public async Task<IActionResult> GetCashFlowStatement([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _cashFlow.GetStatementAsync(CompanyId, from, to);
            return Ok(result);
        }

        /// <summary>Necessary Financial Report #5: Notes to the Financial Statements.</summary>
        [HttpGet("notes")]
        public async Task<IActionResult> GetNotes([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var result = await _notes.GetNotesAsync(CompanyId, from, to);
            return Ok(result);
        }

        [HttpPost("notes/disclosures")]
        public async Task<IActionResult> AddDisclosureNote([FromBody] FinancialStatementNote note)
        {
            note.CompanyId = CompanyId;
            _db.FinancialStatementNotes.Add(note);
            await _db.SaveChangesAsync();
            return Ok(note);
        }

        /// <summary>Full statutory pack in one call: all 5 necessary financial reports for a period.</summary>
        [HttpGet("full-statement-pack")]
        public async Task<IActionResult> GetFullStatementPack([FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            var balanceSheet = await _reports.GetBalanceSheetAsync(CompanyId, to);
            var incomeStatement = await _reports.GetIncomeStatementAsync(CompanyId, from, to);
            var equity = await _equity.GetStatementAsync(CompanyId, from, to);
            var cashFlow = await _cashFlow.GetStatementAsync(CompanyId, from, to);
            var notes = await _notes.GetNotesAsync(CompanyId, from, to);
            return Ok(new { balanceSheet, incomeStatement, statementOfChangesInEquity = equity, cashFlowStatement = cashFlow, notes });
        }
    }
}
