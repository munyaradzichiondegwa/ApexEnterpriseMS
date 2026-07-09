using ApexERP.Domain.Common;
using ApexERP.Domain.Entities;
using ApexERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexERP.Application.Services
{
    public record VatReturnResult(decimal OutputVat, decimal InputVat, decimal NetVatPayable, DateTime PeriodStart, DateTime PeriodEnd);
    public record WhtReportLine(string SupplierName, string InvoiceNo, decimal GrossAmount, decimal WhtRate, decimal WhtAmount);

    /// <summary>PRD Module 9 (ZW Tax Compliance): rules-based ZIMRA VAT return generation (Output
    /// VAT from AR - Input VAT from AP = net payable) and WHT reporting for certificate issuance.
    /// Actual e-filing submission to the ZIMRA portal is a placeholder -- see README.</summary>
    public class TaxComplianceService
    {
        private readonly ApexErpDbContext _db;

        public TaxComplianceService(ApexErpDbContext db)
        {
            _db = db;
        }

        public async Task<VatReturnResult> GenerateVatReturnAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var outputVat = await _db.ArInvoices
                .Where(i => i.CompanyId == companyId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled
                            && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd)
                .SumAsync(i => i.VatAmount, ct);

            var inputVat = await _db.ApInvoices
                .Where(i => i.CompanyId == companyId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled
                            && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd)
                .SumAsync(i => i.VatAmount, ct);

            return new VatReturnResult(outputVat, inputVat, outputVat - inputVat, periodStart, periodEnd);
        }

        public async Task<List<WhtReportLine>> GenerateWhtReportAsync(Guid companyId, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default)
        {
            var invoices = await _db.ApInvoices
                .Include(i => i.Supplier)
                .Where(i => i.CompanyId == companyId && i.WhtAmount > 0
                            && i.InvoiceDate >= periodStart && i.InvoiceDate <= periodEnd
                            && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled)
                .ToListAsync(ct);

            return invoices.Select(i => new WhtReportLine(
                i.Supplier?.Name ?? "Unknown",
                i.InvoiceNo,
                i.Subtotal,
                i.Supplier?.WhtRate ?? 0m,
                i.WhtAmount
            )).ToList();
        }

        /// <summary>Computes realised FX gain/loss for a settled AR/AP transaction (paid at a different
        /// rate than invoiced) -- used by the FX revaluation batch job at period end.</summary>
        public decimal CalculateRealisedFxGainLoss(decimal foreignAmount, decimal invoicedRate, decimal settlementRate)
        {
            var invoicedBase = foreignAmount * invoicedRate;
            var settledBase = foreignAmount * settlementRate;
            return Math.Round(settledBase - invoicedBase, 6); // positive = gain, negative = loss
        }
    }
}
