-- ApexERP seed data: run AFTER registering a company via POST /api/v1/auth/register
-- Replace :company_id with the actual company UUID before running.
--
-- IMPORTANT: The PAYE bands below are ILLUSTRATIVE placeholders modelled on the structure of
-- ZIMRA's published monthly PAYE tax tables (a tax-free threshold, then rising marginal bands
-- up to a top rate, expressed as USD monthly gross). They are NOT guaranteed to match the
-- currently gazetted ZIMRA table for the current tax year. Before going live, replace this
-- section with the exact bands published in the current ZIMRA Public Notice / Finance Act.

-- ============ Chart of Accounts (Zimbabwe SME baseline) ============
-- Columns added for the full IFRS statement suite: cash_flow_category classifies each account for
-- the indirect-method Statement of Cash Flows (Operating/Investing/Financing/CashAndCashEquivalents/
-- NotApplicable); oci_category flags accounts whose movements are Other Comprehensive Income rather
-- than P&L (AssetRevaluationSurplus/ForeignOperationTranslationReserve/NotApplicable).
INSERT INTO gl_accounts (company_id, code, name, type, sub_type, currency, is_bank_account, is_control_account, cash_flow_category, oci_category) VALUES
(:company_id, '1000', 'Cash on Hand', 'Asset', 'Current Asset', 'USD', false, false, 'CashAndCashEquivalents', 'NotApplicable'),
(:company_id, '1010', 'Bank - USD Nostro', 'Asset', 'Current Asset', 'USD', true, false, 'CashAndCashEquivalents', 'NotApplicable'),
(:company_id, '1020', 'Bank - ZWL RTGS', 'Asset', 'Current Asset', 'ZWL', true, false, 'CashAndCashEquivalents', 'NotApplicable'),
(:company_id, '1100', 'Trade Debtors (AR Control)', 'Asset', 'Current Asset', 'USD', false, true, 'Operating', 'NotApplicable'),
(:company_id, '1200', 'Inventory', 'Asset', 'Current Asset', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '1300', 'VAT Input', 'Asset', 'Current Asset', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '1500', 'Property, Plant & Equipment', 'Asset', 'Fixed Asset', 'USD', false, false, 'Investing', 'NotApplicable'),
(:company_id, '1510', 'Accumulated Depreciation - PPE', 'Asset', 'Fixed Asset', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '2000', 'Trade Creditors (AP Control)', 'Liability', 'Current Liability', 'USD', false, true, 'Operating', 'NotApplicable'),
(:company_id, '2100', 'VAT Output', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2110', 'VAT Control (Net Payable)', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2200', 'PAYE Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2210', 'NSSA Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2220', 'AIDS Levy Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2230', 'WHT Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2240', 'DST Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2300', 'Net Salaries Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Operating', 'NotApplicable'),
(:company_id, '2400', 'Dividends Payable', 'Liability', 'Current Liability', 'USD', false, false, 'Financing', 'NotApplicable'),
(:company_id, '2500', 'Loans Payable (Long-Term)', 'Liability', 'Non-Current Liability', 'USD', false, false, 'Financing', 'NotApplicable'),
(:company_id, '3000', 'Share Capital', 'Equity', 'Equity', 'USD', false, false, 'Financing', 'NotApplicable'),
(:company_id, '3100', 'Retained Earnings', 'Equity', 'Equity', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '3200', 'Revaluation Surplus', 'Equity', 'Equity', 'USD', false, false, 'NotApplicable', 'AssetRevaluationSurplus'),
(:company_id, '3300', 'Foreign Currency Translation Reserve', 'Equity', 'Equity', 'USD', false, false, 'NotApplicable', 'ForeignOperationTranslationReserve'),
(:company_id, '4000', 'Sales Revenue', 'Income', 'Revenue', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '4800', 'Overhead Recovery Income (Grants)', 'Income', 'Other Income', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '4900', 'Gain/Loss on Disposal', 'Income', 'Other Income', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '5000', 'Cost of Goods Sold', 'Expense', 'Direct Cost', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6000', 'Salaries & Wages Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6010', 'NSSA Employer Contribution Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6100', 'Rent Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6200', 'Utilities Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6300', 'Depreciation Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6400', 'Office Supplies Expense', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6500', 'Grant Indirect Cost Allocation', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable'),
(:company_id, '6900', 'Sundry Expenses', 'Expense', 'Operating Expense', 'USD', false, false, 'NotApplicable', 'NotApplicable');

-- ============ Tax Codes ============
INSERT INTO tax_codes (company_id, code, name, rate, type, effective_from) VALUES
(:company_id, 'VAT15', 'Standard Rated VAT (15%)', 0.15, 'VAT', '2024-01-01'),
(:company_id, 'VAT0', 'Zero Rated VAT (0%)', 0.00, 'VAT', '2024-01-01'),
(:company_id, 'VATEX', 'VAT Exempt', 0.00, 'VAT', '2024-01-01'),
(:company_id, 'WHT10', 'Withholding Tax - Non-Compliant Supplier (10%)', 0.10, 'WHT', '2024-01-01'),
(:company_id, 'WHT30', 'Withholding Tax - No Tax Clearance (30%)', 0.30, 'WHT', '2024-01-01'),
(:company_id, 'DST', 'Digital Services Tax', 0.05, 'DST', '2024-01-01');

-- ============ PAYE Tax Bands (illustrative - verify against current ZIMRA table) ============
-- USD monthly gross bands, cumulative-deduct method: PAYE = gross * rate - deduct
INSERT INTO paye_tax_bands (company_id, lower_bound, upper_bound, rate, deduct, currency, effective_from) VALUES
(:company_id, 0,      100,   0.00, 0,      'USD', '2024-01-01'),
(:company_id, 100.01, 300,   0.20, 20.00,  'USD', '2024-01-01'),
(:company_id, 300.01, 1000,  0.25, 35.00,  'USD', '2024-01-01'),
(:company_id, 1000.01,2000,  0.30, 85.00,  'USD', '2024-01-01'),
(:company_id, 2000.01,3000,  0.35, 185.00, 'USD', '2024-01-01'),
(:company_id, 3000.01,NULL,  0.40, 335.00, 'USD', '2024-01-01');
