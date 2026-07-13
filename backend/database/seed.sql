-- ApexERP seed data: run AFTER registering a company via POST /api/v1/auth/register (or via the
-- /register page), using that company's UUID.
--
-- IMPORTANT — table/column names: EF Core generates tables named after each DbSet property in
-- ApexErpDbContext (e.g. `GlAccounts`, not `gl_accounts`) and preserves the exact PascalCase of
-- every C# property as the column name. PostgreSQL folds *unquoted* identifiers to lowercase, so
-- every identifier below is double-quoted to preserve the exact case EF Core actually created.
-- If you ever get a "relation ... does not exist" or "column ... does not exist" error, run
-- `\d "TableName"` in psql to see the real column list before troubleshooting further.
--
-- IMPORTANT — enums: none of the C# enums in this project are configured with a string
-- conversion, so EF Core stores every enum as its underlying integer (0-based ordinal position in
-- the C# enum declaration), not as text. The mapping used below:
--   GlAccountType:     Asset=0, Liability=1, Equity=2, Income=3, Expense=4
--   CashFlowCategory:  NotApplicable=0, Operating=1, Investing=2, Financing=3, CashAndCashEquivalents=4
--   OciCategory:       NotApplicable=0, AssetRevaluationSurplus=1, ForeignOperationTranslationReserve=2
--   TaxType:           VAT=0, WHT=1, DST=2
--
-- IMPORTANT — no database-side defaults: a C# property initializer like `IsActive = true` is a
-- CLR-only convenience with zero effect on the generated column -- EF Core does not add a SQL
-- DEFAULT constraint unless explicitly configured (this project doesn't). Every NOT NULL column
-- must therefore be supplied explicitly below, including Id (gen_random_uuid(), built into
-- PostgreSQL 13+ with no extension needed) and CreatedAt (now()).
--
-- IMPORTANT: The PAYE bands below are ILLUSTRATIVE placeholders modelled on the structure of
-- ZIMRA's published monthly PAYE tax tables (a tax-free threshold, then rising marginal bands up
-- to a top rate, expressed as USD monthly gross). They are NOT guaranteed to match the currently
-- gazetted ZIMRA table for the current tax year. Before going live, replace this section with the
-- exact bands published in the current ZIMRA Public Notice / Finance Act.
--
-- Usage (PowerShell):
--   Get-Content backend\database\seed.sql -Raw | docker exec -i apexerp-postgres psql -U apexerp -d apexerp -v company_id="'<your-company-uuid>'"

-- ============ Chart of Accounts (Zimbabwe SME baseline) ============
INSERT INTO "GlAccounts"
  ("Id", "CreatedAt", "CompanyId", "Code", "Name", "Type", "SubType", "Currency",
   "IsActive", "IsBankAccount", "IsControlAccount", "CurrentBalance", "CashFlowCategory", "OciCategory", "IsIntercompanyAccount")
VALUES
(gen_random_uuid(), now(), :company_id, '1000', 'Cash on Hand',                         0, 'Current Asset',       'USD', true, false, false, 0, 4, 0, false),
(gen_random_uuid(), now(), :company_id, '1010', 'Bank - USD Nostro',                    0, 'Current Asset',       'USD', true, true,  false, 0, 4, 0, false),
(gen_random_uuid(), now(), :company_id, '1020', 'Bank - ZWL RTGS',                      0, 'Current Asset',       'ZWL', true, true,  false, 0, 4, 0, false),
(gen_random_uuid(), now(), :company_id, '1100', 'Trade Debtors (AR Control)',           0, 'Current Asset',       'USD', true, false, true,  0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '1200', 'Inventory',                            0, 'Current Asset',       'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '1300', 'VAT Input',                            0, 'Current Asset',       'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '1500', 'Property, Plant & Equipment',          0, 'Fixed Asset',         'USD', true, false, false, 0, 2, 0, false),
(gen_random_uuid(), now(), :company_id, '1510', 'Accumulated Depreciation - PPE',       0, 'Fixed Asset',         'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '2000', 'Trade Creditors (AP Control)',         1, 'Current Liability',   'USD', true, false, true,  0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2100', 'VAT Output',                           1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2110', 'VAT Control (Net Payable)',            1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2200', 'PAYE Payable',                         1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2210', 'NSSA Payable',                         1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2220', 'AIDS Levy Payable',                    1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2230', 'WHT Payable',                          1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2240', 'DST Payable',                          1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2300', 'Net Salaries Payable',                 1, 'Current Liability',   'USD', true, false, false, 0, 1, 0, false),
(gen_random_uuid(), now(), :company_id, '2400', 'Dividends Payable',                    1, 'Current Liability',   'USD', true, false, false, 0, 3, 0, false),
(gen_random_uuid(), now(), :company_id, '2500', 'Loans Payable (Long-Term)',            1, 'Non-Current Liability','USD', true, false, false, 0, 3, 0, false),
(gen_random_uuid(), now(), :company_id, '3000', 'Share Capital',                        2, 'Equity',              'USD', true, false, false, 0, 3, 0, false),
(gen_random_uuid(), now(), :company_id, '3100', 'Retained Earnings',                    2, 'Equity',              'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '3200', 'Revaluation Surplus',                  2, 'Equity',              'USD', true, false, false, 0, 0, 1, false),
(gen_random_uuid(), now(), :company_id, '3300', 'Foreign Currency Translation Reserve', 2, 'Equity',              'USD', true, false, false, 0, 0, 2, false),
(gen_random_uuid(), now(), :company_id, '4000', 'Sales Revenue',                        3, 'Revenue',             'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '4800', 'Overhead Recovery Income (Grants)',    3, 'Other Income',        'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '4900', 'Gain/Loss on Disposal',                3, 'Other Income',        'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '5000', 'Cost of Goods Sold',                   4, 'Direct Cost',         'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6000', 'Salaries & Wages Expense',             4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6010', 'NSSA Employer Contribution Expense',   4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6100', 'Rent Expense',                         4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6200', 'Utilities Expense',                    4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6300', 'Depreciation Expense',                 4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6400', 'Office Supplies Expense',              4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6500', 'Grant Indirect Cost Allocation',       4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false),
(gen_random_uuid(), now(), :company_id, '6900', 'Sundry Expenses',                      4, 'Operating Expense',   'USD', true, false, false, 0, 0, 0, false);

-- ============ Tax Codes ============
-- Type: VAT=0, WHT=1, DST=2
INSERT INTO "TaxCodes" ("Id", "CreatedAt", "CompanyId", "Code", "Name", "Rate", "Type", "IsActive", "EffectiveFrom")
VALUES
(gen_random_uuid(), now(), :company_id, 'VAT15', 'Standard Rated VAT (15%)',                    0.15, 0, true, '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 'VAT0',  'Zero Rated VAT (0%)',                         0.00, 0, true, '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 'VATEX', 'VAT Exempt',                                  0.00, 0, true, '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 'WHT10', 'Withholding Tax - Non-Compliant Supplier (10%)', 0.10, 1, true, '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 'WHT30', 'Withholding Tax - No Tax Clearance (30%)',     0.30, 1, true, '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 'DST',   'Digital Services Tax',                        0.05, 2, true, '2024-01-01');

-- ============ PAYE Tax Bands (illustrative - verify against current ZIMRA table) ============
-- USD monthly gross bands, cumulative-deduct method: PAYE = gross * rate - deduct
INSERT INTO "PayeTaxBands" ("Id", "CreatedAt", "CompanyId", "LowerBound", "UpperBound", "Rate", "Deduct", "Currency", "EffectiveFrom")
VALUES
(gen_random_uuid(), now(), :company_id, 0,       100,  0.00, 0,      'USD', '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 100.01,  300,  0.20, 20.00,  'USD', '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 300.01,  1000, 0.25, 35.00,  'USD', '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 1000.01, 2000, 0.30, 85.00,  'USD', '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 2000.01, 3000, 0.35, 185.00, 'USD', '2024-01-01'),
(gen_random_uuid(), now(), :company_id, 3000.01, NULL, 0.40, 335.00, 'USD', '2024-01-01');
