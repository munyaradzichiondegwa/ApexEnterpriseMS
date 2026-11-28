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


## Phase 2 — cash and modified-cash actuals: implementation contract

The next implementation slice must introduce a separate, source-backed budget-execution actual ledger. It must not derive cash-basis actuals from accrual journal postings or infer cash paid from AP invoice status.

### Source events and provenance

Each captured actual must retain, at minimum:

- Company/tenant, financial year, budget period and reporting entity.
- Approved budget version and budget line (or an explicitly approved mapping to its Fund Centre, GL classification and cost centre dimensions).
- Budget-basis event type: cash receipt, cash payment, or (only where the entity's formally approved modified-cash policy includes it) an eligible commitment/obligation.
- Original source-system identifier and source document reference, source date, effective/value date, currency, amount and exchange-rate provenance where applicable.
- Source record hash or immutable source snapshot reference, capture actor/time, validation status and approval workflow reference.
- Reversal/correction linkage to the original event. Corrections must be compensating events; approved source events must not be silently edited or deleted.

The source adapter must accept only records from controlled cash/bank receipt and payment processes (or approved external bank/cashbook imports) and the commitment register for specifically defined modified-cash elements. A purchase order, goods receipt, AP invoice, posted accrual journal or payment instruction is not evidence of cash settlement by itself. A commitment is not a cash flow and must be separately identified if the approved budget basis includes it.

### Comparability and aggregation

- Aggregate only approved, non-reversed source events within the approved budget period and on the approved budget basis.
- Preserve receipt/payment direction and the approved budget's classification scheme. Do not net unrelated receipts and payments merely because they share a Fund Centre.
- Enforce company, entity, period, currency and classification scope at both ingestion and report time.
- Block statement issuance when source coverage, unresolved duplicate source IDs, unapproved mappings, missing dimensions or unapproved events remain.
- Keep accrual-basis GL actuals and cash/modified-cash budget actuals as distinct measures with explicit basis labels.

### Statement of Cash Flows reconciliation

For a cash or modified-cash budget, IPSAS 24 paragraph 47 requires reconciliation of actual amounts on the budget-comparable basis to net cash flows from operating, investing and financing activities. The reconciliation must not be an arbitrary balancing journal or an unstructured set of manual amounts.

The bridge must:
1. Start with approved budget-basis actuals.
2. Map source events to the Statement of Cash Flows activity using controlled account/cash-flow classifications.
3. Show basis, timing, entity and presentation differences separately.
4. Calculate each reconciliation section and the resulting bridge total from the approved source events and the approved financial-statement cash-flow result.
5. Fail validation if any operating, investing, financing or total difference does not reconcile to zero within the configured currency precision; any permitted rounding tolerance must be explicit and audited.
6. Preserve drill-through from every bridge amount to its source events and from each source event to its receipt/payment/commitment evidence.

This follows IPSAS 24 paragraphs 47–49: for a non-accrual budget, the reconciliation target is net cash flows from operating, investing and financing activities. The standard separately identifies basis, timing and entity differences; presentation differences are also relevant where classification formats differ. See [IPSAS 24, paragraphs 47–49 (IPSASB/IFAC-hosted standard PDF)](https://ifacweb.blob.core.windows.net/publicfiles/2025-04/A33_IPSAS%2024.pdf). The UN IPSAS Finance Manual provides a worked modified-cash example in which unliquidated obligations are removed from cash flows and capital acquisitions are classified as investing: [UN IPSAS Finance Manual, Chapter 16](https://www.un.org/ipsas/Chapter16.html).

### Approval and issue gates

- Source-event capture/preparation and certification must be separate duties.
- The certifier cannot be the preparer or the source-event submitter.
- Reconciliation preparation and approval must remain independent, with all supporting evidence attached.
- Statement issue requires approved original and final budgets (or a documented no-revision final budget), complete source coverage, approved mappings, completed cash-flow bridge, resolved material variances and reviewer sign-off.
- A draft or pending source event, reconciliation or variance explanation must never be included in the issued statement.

### Regression acceptance matrix

PostgreSQL integration tests must prove: duplicate source-event rejection within a company; the same source identifier may exist in a different company; cross-company source/evidence access is denied; cash receipt/payment sign conventions; commitment inclusion only for an explicitly configured modified-cash basis; no accrual-GL substitution; reversals preserve the original audit trail; reporting-period cut-off uses the effective/value date; foreign-currency events use retained rate provenance; untagged or unmapped source events block issue; all three cash-flow sections reconcile independently; a one-cent (or currency-minor-unit) bridge difference fails; pending/unapproved items are excluded; and preparer self-approval is rejected.

No modified-cash rule should be enabled by default. Each entity must configure and approve the precise commitment/obligation elements included in its budget basis, with the governing authority and effective date recorded.


## Phase 2 implementation status — cash actual capture foundation

Implemented on the feature branch:

- Added tenant-scoped `PublicSectorBudgetExecutionActual` persistence and migration `20261128040000_AddIpsas24BudgetExecutionActuals`.
- Added a bank-transaction source kind; event direction for cash receipts/payments; budget-line, Fund Centre, GL and cash-flow section mappings; source date/reference; effective date; currency; and certification metadata.
- Capture is restricted to approved cash-basis budget versions. Modified-cash capture remains disabled until entity-specific rules are formally configured.
- Bank transaction capture requires a reconciled transaction linked to a posted journal and an available company bank account. The posted journal must contain an exact matching amount against the selected budget GL account, Fund Centre and cost centre.
- Added strict AR receipt and AP payment source adapters. AR capture requires a posted receipt journal, one fully allocated receipt against one fully settled tax-free invoice, a posted invoice revenue journal, and an explicit invoice Fund Centre mapped to the selected revenue GL budget line. AP capture requires a posted payment journal, independent payment authorisation (preparer differs from authoriser), one fully allocated payment against one fully settled tax-free invoice, a posted invoice expense journal, and matching invoice Fund Centre/expense GL mapping. Both adapters verify the exact bank GL leg from the posted settlement journal and require matching currency/date.
- Added an optional Fund Centre dimension to AR invoices, carried into the posted revenue journal, so AR settlement actuals have an auditable public-sector classification. Existing AR invoices without this tag remain valid for ordinary accounting but cannot be used for IPSAS 24 budget actual capture.
- AR/AP adapters deliberately reject partial, split, multi-line, VAT, withholding-tax and cost-centre allocations until explicit line-level settlement allocation mapping is implemented. They do not estimate proportional allocations or infer classifications from invoice headers where the GL evidence is insufficient.
- Source amount, currency, date and reference are derived from the source record rather than trusted from the API request. The budget effective date must match the source transaction date; timing differences are handled through approved reconciliation adjustments, not by shifting source events between periods. Source currency must equal the budget currency; FX conversion is not silently approximated.
- A company/source-kind/source-record unique index prevents duplicate capture. Company query filtering is applied to the new entity.
- Added draft → pending certification → certified lifecycle. The certifier must differ from the creator. PostgreSQL trigger prevents deletion, evidence edits and mutation of certification metadata after certification.
- Added API routes under `/api/v1/public-sector/budget-execution-actuals` for capture, listing, submission and certification.
- Added an approved-budget lookup for the latest cash budget version and a cash-flow bridge endpoint. It totals certified receipts/payments by operating, investing and financing, checks that every posted journal-linked bank movement in the budget period is reconciled and captured, blocks on draft/pending actuals, and compares approved reconciliation adjustments with the difference to the generated Statement of Cash Flows.
- Added PostgreSQL integration tests for reconciled bank evidence, independent certification, self-certification rejection, duplicate source rejection, and the strict AR receipt/AP payment source adapters.

### Still not implemented / not verified

- Budget actuals are not yet included in the IPSAS 24 comparison statement; statement comparability remains blocked for cash budgets until a period-coverage certification and reconciliation bridge are implemented.
- Added a Public Sector screen for selecting an approved cash budget line and reconciled bank transaction, capturing the event, listing actuals, submitting for certification and certifying as a separate user. AR/AP settlement allocation mapping remains outstanding; only directly mapped reconciled bank journal movements are accepted.
- Modified-cash commitments, source snapshot hashing, compensating reversal workflow, foreign-currency source conversion and source completeness attestations remain disabled/outstanding.
- The cash-basis actuals are now included in the IPSAS 24 comparison using certified source-linked events, rather than accrual GL journal totals. The comparison remains unavailable on a comparable basis if any in-period bank-statement movement is unreconciled, unlinked, unposted or uncaptured, if any actual is unapproved, or if certified source evidence no longer validates.
- Cash-basis budget lines may map to balance-sheet GL accounts when the account is explicitly classified to operating, investing or financing cash flows. This is necessary for capital acquisitions and financing transactions; the accrual-basis budget line restriction remains income/expense.
- The cash-flow bridge preview is implemented, but it is not yet a statutory statement-issuance gate. The existing CashFlowStatementService requires independent review before its totals can be treated as externally certified figures. A successful budget-comparison source-coverage check must not be interpreted as a completed IPSAS 24 reconciliation to the Statement of Cash Flows.
- General-purpose AR/AP allocation adapters remain incomplete: the first adapters intentionally support only fully settled single-invoice, single-line, tax-free allocations with explicit Fund Centre and GL mapping. Partial payments, multi-invoice receipts/payment runs, multi-line invoice allocation, VAT/WHT splits and cost-centre allocation remain blocked pending line-level allocation mapping.
- Modified-cash commitment rules, source snapshot hashing, compensating reversal workflow, foreign-currency source conversion and source completeness attestations remain disabled/outstanding.
- Cash final budgets must retain zero-value dimensions where a classification was removed from the original budget; otherwise actual execution cannot be captured against that final budget line and the comparison is blocked.
- Migration `20261128060000_AddIpsas24SettlementSources` adds the optional AR invoice Fund Centre dimension. EF snapshot drift, clean PostgreSQL installation and the expanded integration tests must pass in the latest GitHub Actions run before this slice is considered verified.
- After applying the migration in an environment where RLS is enabled, rerun `backend/database/hardening/002_row_level_security.sql` so the newly created company-owned table receives the database policy. Confirm the runtime role remains non-owner and has no `BYPASSRLS`.
