# IPSAS 24 budget comparison implementation

**Research basis:** 28 November 2026  
**Jurisdictional presentation:** Zimbabwe Treasury Financial Reporting Manual and Treasury Circular No. 3 of 2025. This is a technical product mapping, not an audit opinion or legal certification.

## Authoritative requirements

- IPSAS 24 requires a comparison of original budget, final budget and actual amounts on a comparable basis, with explanations of material differences. Where budget and financial statements are not prepared on a comparable basis, the actual amounts must be reconciled and differences identified as basis, timing, entity and presentation differences. See [PSASB IPSAS 24 Guideline](https://www.psasb.go.ke/sites/default/files/PSASB/Accounting%20Standards/Guidelines/IPSAS%2024%20Guideline.pdf).
- Zimbabwe Treasury requires the comparison to be presented as a separate additional financial statement and on a basis comparable to the budget. It provides a prescribed statement format in Appendix 5 and requires reconciliation on the face of the statement where the bases differ. See [Zimbabwe Financial Reporting Manual](https://zimtreasury.co.zw/wp-content/uploads/2025/03/zfrm-soft-copy-1-1.pdf) and [Treasury Circular No. 3 of 2025, Appendix 5](https://zimtreasury.co.zw/wp-content/uploads/2025/03/Treasury-Circular-No-3-of-2025-on-2024-IPSAS-Transitional-Financial-Statements-1.pdf).
- IPSAS 24 material-variance explanations include changes from original to final budget and final budget to actual. Budget basis, classification basis, reporting period and entities covered must be disclosed.

## Implemented in ApexERP

- Added tenant-scoped budget version headers with financial year, version sequence, original/revised designation, accounting basis, classification basis, entity scope, reporting period, currency, authority reference and amendment explanation.
- Added budget lines mapped to Fund Centre, income/expense GL account, optional cost centre and tenant-provided classification code. Revenue and expense lines are supported; no official government code set is fabricated.
- Added independent approval workflow: draft → pending approval → approved. The submitter cannot finalise their own approval. Revised versions require an approved original version.
- Added GET endpoint at /api/v1/public-sector/budget-versions/comparison?year={year}, multi-line original/revised budget draft entry and submission UI, an IPSAS 24 statement view and CSV export in Public Sector & Government.
- Comparison selects the approved original and latest approved revised version, includes dimensions introduced or removed by the final budget, and computes expense/income actuals from posted/reversed ledger lines with the appropriate normal-balance sign.
- Accrual GL actuals are labelled comparable only when the budget basis is accrual, the reporting period/classification/entity metadata agree across versions, the entity scope exactly matches the registered company name, the budget currency matches the company's functional currency, and each budget classification code matches the mapped GL account SubType. Cash-basis budgets do not reuse accrual GL amounts as if they were comparable.
- Added tenant-scoped reconciliation records for basis, timing, entity and presentation adjustments, with preparer capture, independent approval workflow, supporting reference and statement cash-flow section amounts. The statement displays approved reconciliation rows only.

## Explicit remaining controls

1. For cash/modified-cash budgets, comparable actuals must be sourced from the budgetary ledger/cash and commitment records, independently certified and reconciled to the statement of cash flows. Accrual GL postings are deliberately not substituted.
2. The current actual calculation is based on tagged posted GL journal lines. Completeness of Fund Centre tagging, entity scope and any omitted budget entities must be certified before statutory issue.
3. Material variance explanations require preparer/reviewer sign-off and a configurable materiality threshold; an amendment reason alone is not a full statutory variance explanation.
4. The official Zimbabwe budget classification dictionary, current Treasury template version, reporting period conventions and approved entity scope must be configured from authoritative government sources.
5. The current statement is a functional first implementation slice, not a claim of full IPSAS compliance, Treasury certification or audit acceptance.

## Data integrity

- Budget versions and lines are tenant-filtered and covered by PostgreSQL RLS through the shared CompanyId policy.
- Approved versions are not edited in place through the API; changes are represented as a new revised version with an authority reference and amendment explanation.
- Existing PublicSectorBudgetAllocations remain in place for operational appropriation controls. The IPSAS 24 versioned statement is a separate reporting ledger; it does not silently reinterpret legacy expense-only allocations as an original/final statutory budget.
