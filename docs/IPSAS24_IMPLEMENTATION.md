# IPSAS 24 budget comparison implementation

**Research basis:** 28 November 2026  
**Jurisdictional presentation:** Zimbabwe Treasury Financial Reporting Manual and Treasury Circular No. 3 of 2025. This is a technical product mapping, not an audit opinion or legal certification.

## Authoritative requirements

- IPSAS 24 requires a comparison of original budget, final budget and actual amounts on a comparable basis, with explanations of material differences. Where budget and financial statements are not prepared on a comparable basis, the actual amounts must be reconciled and differences identified as basis, timing, entity and presentation differences. See [PSASB IPSAS 24 Guideline](https://www.psasb.go.ke/sites/default/files/PSASB/Accounting%20Standards/Guidelines/IPSAS%2024%20Guideline.pdf).
- Zimbabwe Treasury requires the comparison to be presented as a separate additional financial statement and on a basis comparable to the budget. It provides a prescribed statement format in Appendix 5 and requires reconciliation on the face of the statement where the bases differ. See [Zimbabwe Financial Reporting Manual](https://zimtreasury.co.zw/wp-content/uploads/2025/03/zfrm-soft-copy-1-1.pdf) and [Treasury Circular No. 3 of 2025, Appendix 5](https://zimtreasury.co.zw/wp-content/uploads/2025/03/Treasury-Circular-No-3-of-2025-on-2024-IPSAS-Transitional-Financial-Statements-1.pdf).
- IPSAS 24 material-variance explanations include changes from original to final budget and final budget to actual. Budget basis, classification basis, reporting period and entities covered must be disclosed.

## Current implementation status — 28 November 2026

**This section supersedes older phase-status statements later in this document.** The feature branch now contains a broader IPSAS 24 implementation than the historical notes below describe. It is not yet a complete statutory-release certification.

### Implemented controls

- Original and final budget snapshots are separately approved, immutable versions. The comparison presents original budget, final budget and actual on a comparable basis. A budget dimension absent from one version is shown as zero in that version, not as an unknown amount.
- Comparison rows expose the Fund Centre hierarchy level (Vote, Programme or Sub-Programme) and the Cost Centre code/name alongside GL classification, so the legislative and management dimensions are visible in both the UI and CSV export.
- Accrual actuals are derived from posted/reversed GL journal lines and are withheld when reporting period, company entity scope, functional currency, classification, Fund Centre or cost-centre dimensions do not agree. Untagged actuals in budgeted GL accounts block comparability.
- Cash actuals are derived only from certified bank transactions, AR receipts and AP payments. Accrual GL totals are never substituted for cash actuals. The supported AR/AP adapter is deliberately restricted to fully allocated, posted, tax-free invoice settlements with explicit Fund Centre/GL mapping; VAT/WHT and cost-centre splits are not inferred.
- Revised budgets require an authority reference, a reason and an explicit amendment nature: reallocation within the approved budget, other authorised factors, or both. The comparison exposes the classification and reason for original-to-final changes. Legacy revised budgets without this new metadata remain incomplete for disclosure.
- Non-zero final-budget-to-actual variances require an evidence-backed explanation, submission and independent approval. The variance amount is recalculated at submission and approval. Until a formally approved entity-specific materiality threshold exists, every non-zero variance requires explanation.
- Budget-to-financial-statement reconciliation supports separate basis, timing, entity and presentation differences. Accrual-basis reconciliation rows carry revenue, expense and operating/investing/financing cash-flow amounts. Cash-basis reconciliation rows are restricted to operating, investing and financing net cash flows. Reconciliation rows require independent workflow approval.
- The cash source register supports bank accounts, receivables control accounts and payables control accounts. Each source is identified by its company-owned internal account ID. Unsupported Cashbook entries are rejected rather than treated as complete.
- Each expected source has a period submission with record count, control total, currency, opening/closing balances and evidence reference. Source records must be mapped exactly to one or more certified budget actual allocations or to a reason-coded exclusion. Split AR/AP allocations are supported and must reconcile exactly to the underlying source amount. One actual allocation cannot be used twice.
- Live source populations are re-read at source submission creation/review, record-reconciliation submission/review, register certification and each budget-comparison/bridge read. Record IDs, counts, control totals and currency must still agree. Bank statements also satisfy opening balance + net movement = closing balance. A late or changed source record invalidates the comparison. A new submission revision is supported after a rejected submission, but a direct post-review reopen/reversal workflow is not yet complete; a change after final review can therefore block the period until the source record is corrected through an authorised reversal and resubmission process.
- Exclusions require a reason and independent record-level review. Reviewed exclusions count as accounted-for source records, but do not become budget actuals; resulting differences must still be explained through the approved reconciliation.
- A separate expected-source-register completeness attestation records entity scope, declared source count and evidence. It requires independent certification and freezes later additions to the certified register. This is a management attestation supported by evidence, not automated discovery of undisclosed bank accounts or source systems.
- The cash-budget comparison and cash-flow bridge fail closed when the register, source submissions, record mappings, source populations or certifications are missing, unreviewed, stale or inconsistent. CSV export is withheld until comparable actuals, original-to-final explanations and non-zero variance explanations are complete.

### Remaining boundaries — do not mark statutory IPSAS 24 release complete

1. **Modified-cash budgets:** not enabled. There is no approved entity-specific recognition policy for commitments/obligations, no approved commitment-to-budget-line mapping, and no controlled liquidation/reversal treatment. A purchase order or encumbrance is not a cash flow.
2. **Cashbook and external statement ingestion:** no dedicated cashbook adapter or cryptographically verified external bank/subledger statement import is implemented. The supported source adapters reconcile records already held in ApexERP; reviewers must compare them with original source-system exports retained as evidence.
3. **Foreign currency cash actuals:** the current cash adapter requires source currency to equal budget currency. It does not yet perform cash-budget FX translation with approved rate source, effective date, quote direction and immutable rate provenance.
4. **AR/AP settlement classification:** tax-bearing invoices, withholding-tax splits, cost-centre allocation and partial settlement across multiple GL classifications remain blocked until explicit invoice-line/source-journal mapping is implemented. No proportional allocation is invented.
5. **Source correction/reopening:** a new source-submission revision is supported after rejection, but there is not yet a complete post-review invalidation/reopen workflow for a source population that changes after independent review. A stale review fails closed; authorised correction, reversal and resubmission controls are still required.
6. **AR/AP opening and closing control balances:** source transaction populations, record counts, amounts and currencies are revalidated, but the AR/AP opening and closing balances are not yet independently reconciled to their GL control-account balances. This remains a source-control acceptance item.
7. **Reporting entity and timing differences:** automatic actual capture currently requires the budget entity scope and reporting period to match the registered company and budget period. It does not yet compute a comparable subset for a budget that covers only part of a reporting entity, or a different budget period, and reconcile the resulting entity/timing differences.
8. **Current Zimbabwe statutory configuration:** Treasury Circular No. 3 of 2025 is for the 2024 transitional financial statements. It is not treated as the current 2026 reporting instruction. The latest Treasury-approved 2026 template, classification dictionaries, legislative oversight aggregation and jurisdiction-specific reporting rules must be supplied and configured before statutory issue.
9. **Issue approval:** the API comparison and export gates are controls over the data available to ApexERP. They are not a signed statutory statement-issuance workflow, legal opinion, Treasury acceptance or audit opinion. The cash-flow bridge is a reconciliation preview and must use independently certified financial-statement cash-flow totals before statutory release.
10. **Materiality:** the current all-non-zero variance explanation rule is intentionally conservative. A configurable threshold is not activated until formally approved by the reporting entity.

### Standards basis

IPSAS 24 requires original and final budget amounts, actual amounts on a comparable basis, and explanations of material differences. Changes from original to final budget must explain whether they result from reallocations within the budget or other factors. Where budget and financial-statement actuals are not comparable, the reconciliation must identify basis, timing and entity differences; for accrual budgets it covers total revenue, total expense and net cash flows from operating, investing and financing, while for non-accrual budgets it covers the three net cash-flow sections. See the [IPSASB/IFAC-hosted IPSAS 24 text](https://ifacweb.blob.core.windows.net/publicfiles/2025-04/A33_IPSAS%2024.pdf) and [UN IPSAS guidance on material differences](https://www.un.org/ipsas/Chapter16.html).

## Implemented in ApexERP

- Added tenant-scoped budget version headers with financial year, version sequence, original/revised designation, accounting basis, classification basis, entity scope, reporting period, currency, authority reference and amendment explanation.
- Added budget lines mapped to Fund Centre, income/expense GL account, optional cost centre and tenant-provided classification code. Revenue and expense lines are supported; no official government code set is fabricated.
- Added independent approval workflow: draft → pending approval → approved. The submitter cannot finalise their own approval. Revised versions require an approved original version.
- Added GET endpoint at /api/v1/public-sector/budget-versions/comparison?year={year}, multi-line original/revised budget draft entry and submission UI, an IPSAS 24 statement view and CSV export in Public Sector & Government.
- Comparison selects the approved original and latest approved revised version, includes dimensions introduced or removed by the final budget, and computes expense/income actuals from posted/reversed ledger lines with the appropriate normal-balance sign.
- Accrual GL actuals are labelled comparable only when the budget basis is accrual, the reporting period/classification/entity metadata agree across versions, the entity scope exactly matches the registered company name, the budget currency matches the company's functional currency, and each budget classification code matches the mapped GL account SubType. Cash-basis budgets do not reuse accrual GL amounts as if they were comparable.
- Added tenant-scoped reconciliation records for basis, timing, entity and presentation adjustments, with preparer capture, independent approval workflow, supporting reference and statement cash-flow section amounts. The statement displays approved reconciliation rows only.

## Phase 3 — modified-cash commitment recognition

**Status: not enabled; policy and classification controls are still required.**

IPSAS 24 permits an approved budget to include commitments and changes in commitments, but the entity must disclose its budgetary basis and reconcile modified-cash actuals to the statement of cash flows. Unliquidated obligations are not cash flows and must be identified as a basis difference. The system must not treat an operational encumbrance as a cash payment or as an IPSAS financial-statement expense.

The existing procurement commitment register is an operational budget-control mechanism:
- An approved purchase order creates a commitment against a Fund Centre.
- Posted AP invoices realise the commitment, including partial realisation.
- Cancellation/release reduces or closes the encumbrance.
- The commitment record currently has no approved GL budget-line mapping or commitment-specific classification evidence. Purchase-order lines also do not carry the complete Fund Centre/GL/cost-centre/tax classification needed for IPSAS 24 budget actuals.

Accordingly, the system must not yet add Commitment.Amount or Commitment.RealisedAmount to IPSAS 24 actuals. Doing so would risk duplicate recognition when the related AP invoice/payment is captured and would not establish the correct budget-line classification.

The implementation contract for this phase is:
1. Add a distinct ModifiedCash budget basis without changing the persisted numeric values of existing Accrual and Cash enum members.
2. Require an entity-specific, independently approved modified-cash policy reference on the approved budget version. The policy must state which obligation stages qualify (approved PO/contract, goods/services received, verified invoice, or another legally approved trigger), the recognition date, treatment of amendments/cancellations and the relationship to cash disbursements.
3. Add a tenant-scoped, independently approved mapping from the commitment/obligation source to one or more approved budget lines. The mapping must carry Fund Centre, GL classification, optional cost centre, currency, period and evidence references, and must reconcile exactly to the eligible source amount.
4. Keep commitment-basis actuals distinct from cash receipts/payments. Do not count a disbursement and its underlying commitment twice; maintain a source relationship and a controlled liquidation/reversal allocation.
5. Revalidate the commitment, purchase order, approval, status, amount, currency and source mapping at certification and statement generation. Reject cancelled, released, over-realised, unapproved or cross-company sources.
6. Reconcile unliquidated obligations and other modified-cash adjustments to the independently reviewed Statement of Cash Flows. A commitment register total is not itself a cash-flow amount.
7. Do not enable the basis for a Zimbabwe public-sector entity until the applicable Treasury/Accounting Officer policy and classification instructions are formally supplied and approved. The available Zimbabwe transitional IPSAS guidance does not itself define a universal modified-cash commitment recognition rule.

Acceptance tests must cover independent approval, approved-PO eligibility, commitment amendments, partial invoice realisation, cancellation, payment liquidation without double-counting, period cut-off, source changes after approval, duplicate mapping, exact allocation totals, tenant isolation/RLS and the cash-flow reconciliation bridge.

## Period completeness and source reconciliation — implemented for supported adapters

**Status: implemented and fail-closed for bank, AR-receipt and AP-payment source populations already held in ApexERP.**

The supported workflow now has four separately controlled stages:

1. **Expected-source register:** company-owned bank accounts, AR control accounts and AP control accounts are registered against an approved cash budget version. Entries require independent approval. Cashbook is rejected until a dedicated adapter exists.
2. **Period source submission:** each registered source submits its effective period, record count, control total, currency, opening/closing balances and evidence reference. The service re-reads live source populations and rejects mismatched counts, totals, currency and bank opening-to-closing movement.
3. **Record-level reconciliation:** each source record is mapped to one or more certified budget actual allocations or one reason-coded exclusion. Source amounts, dates, currency, posted settlement journal, source kind, budget version and source identity are validated. Split AR/AP allocations must sum exactly to the underlying receipt/payment. An actual cannot be used twice in an active revision.
4. **Register completeness certification:** an independent certifier approves the declared source population count and entity scope after every registered source has a reviewed control submission and reviewed record-level reconciliation. The database freezes additions to the register after certification.

The comparison and cash-flow reconciliation preview re-read the live source populations and require the latest submission revision for every registered source to remain reviewed. A missing source is not treated as zero. Fully reviewed exclusions can account for non-budget source activity without fabricating a budget actual; any resulting budget-to-financial-statement difference must still be reconciled and explained.

**Source-evidence boundary:** this workflow reconciles transaction records already held in ApexERP. It does not cryptographically import or authenticate an external bank or subledger export. The responsible entity must retain the original export and evidence, and the independent reviewer must compare the in-system population with that authoritative source. The completeness attestation is a management assertion supported by evidence; ApexERP cannot discover undisclosed accounts or source systems.

**Correction boundary:** rejected submissions can be replaced by a numbered revision. A post-review source change invalidates live comparability. A complete controlled reopen/reversal workflow for already-reviewed source populations is not yet implemented; until that is delivered, the period remains blocked rather than silently rewriting reviewed history.

### Acceptance coverage

PostgreSQL integration coverage includes:
- Original/final budget comparison, no-revision treatment and tenant isolation.
- Cash budgets not substituting accrual GL amounts for cash actuals.
- Certified bank actuals, independent certification and late/unreconciled source detection.
- Fully settled AR receipts and authorised AP payments, including split actual allocations.
- Zero-activity attestations and rejection when source activity appears after submission.
- Expected-source approval, register freeze, source submissions and independent record-level review.
- Variance explanation preparation, independent review and invalidation when the actual variance changes.
- EF migration drift, clean PostgreSQL installation, hardening scripts and RLS boot through CI.

## Explicit remaining controls



1. **Modified-cash budgets:** not supported. The product does not infer commitment recognition from purchase orders, goods receipts, invoices or encumbrances. An entity-specific recognition policy, trigger, classification and liquidation/reversal treatment must be formally approved before this basis can be enabled.
2. **Cashbook and external statement ingestion:** no dedicated cashbook adapter or cryptographically verified external bank/subledger statement import is implemented. Reviewers must compare ApexERP records with original source exports retained as evidence.
3. **Cash FX:** cash actuals currently require the source currency to equal the approved budget currency. Cash-budget translation using approved effective-dated FX rates and immutable quote provenance is not implemented.
4. **AR/AP control balances and classification:** transaction populations, counts, amounts and currencies are revalidated, but AR/AP opening/closing balances are not yet reconciled to GL control-account balances. Tax-bearing invoices, WHT splits and cost-centre allocation require explicit source-journal mapping; no proportional allocation is inferred.
5. **Reporting entity and timing differences:** automatic actual capture requires the budget entity scope and reporting period to match the registered company and budget period. Comparable subsets for partial entities or different periods are not automatically calculated; manual reconciliation rows do not replace a complete entity/timing bridge.
6. **Source corrections after review:** rejected submissions can be revised, but a full post-review reopen/reversal and recertification workflow is still required. Stale source populations block comparison in the meantime.
7. **Accrual dimension completeness:** actuals on budgeted GL accounts with missing or unmatched Fund Centre/cost-centre dimensions block comparability. A complete approved chart-of-accounts and reporting-entity inventory is still a management acceptance responsibility.
8. **Zimbabwe statutory configuration:** Treasury Circular No. 3 of 2025 relates to the 2024 transitional financial statements. The latest Treasury-approved 2026 template, classification dictionaries, reporting period conventions and legislative oversight aggregations must be supplied and configured before statutory issue.
9. **Statutory issue approval:** the comparison and export gates do not constitute a signed statutory statement-issuance workflow, legal opinion, Treasury acceptance or audit opinion. The cash-flow bridge is a reconciliation preview and requires independently certified financial-statement cash-flow totals before statutory release.
10. **Materiality:** every non-zero comparable variance currently requires an explanation. A configurable materiality threshold remains disabled until formally approved by the reporting entity.

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
- Added bank-transaction, AR-receipt and AP-payment source kinds; cash receipt/payment direction; budget-line, Fund Centre, GL and cash-flow section mappings; source date/reference; effective date; currency; and certification metadata.
- Capture is restricted to approved cash-basis budget versions. Modified-cash capture remains disabled until entity-specific rules are formally configured.
- Bank transaction capture requires a reconciled transaction linked to a posted journal and an available company bank account. The posted journal must contain an exact matching amount against the selected budget GL account, Fund Centre and cost centre.
- Expanded AR receipt and AP payment source adapters to support one receipt/payment allocated across multiple invoices and multiple invoice GL lines. Fully settled invoices can be mapped across multiple GL classifications. Partial settlements are supported when the invoice has one GL classification; the current adapter maps the source allocation amount to that classification without estimating proportions. Partial settlement of a multi-GL invoice remains blocked until explicit invoice-line allocation is implemented.
- AR/AP split capture still requires tax-free invoices with explicit Fund Centre classification and posted invoice/settlement journals. VAT, withholding-tax and cost-centre splits remain excluded pending explicit source-journal-line mapping. The current invoice line model does not carry a cost-centre dimension, so the adapters reject cost-centre budget lines rather than infer them. All allocations for a source must be captured and their total must equal the original receipt/payment amount before any allocation can be certified. A source cannot receive further allocations after one allocation has been certified.
- Added an optional Fund Centre dimension to AR invoices, carried into the posted revenue journal, so AR settlement actuals have an auditable public-sector classification. Existing AR invoices without this tag remain valid for ordinary accounting but cannot be used for IPSAS 24 budget actual capture.
- Source amount, currency, date and reference are derived from the source record rather than trusted from the API request. The budget effective date must match the source transaction date; timing differences are handled through approved reconciliation adjustments, not by shifting source events between periods. Source currency must equal the budget currency; FX conversion is not silently approximated.
- A company/source-kind/source-record/budget-line unique index prevents duplicate allocation of the same source to the same budget line while allowing a source to span multiple budget lines. Company query filtering is applied to the new entity. Migration `20261128080000_AllowIpsas24SourceSplitAllocations` replaces the former one-source/one-actual uniqueness constraint.
- Added draft → pending certification → certified lifecycle. The certifier must differ from the creator. PostgreSQL trigger prevents deletion, evidence edits and mutation of certification metadata after certification.
- Added API routes under `/api/v1/public-sector/budget-execution-actuals` for capture, listing, submission and certification.
- Added an approved-budget lookup for the latest cash budget version and a cash-flow bridge endpoint. It totals certified receipts/payments by operating, investing and financing, checks that every posted journal-linked bank movement in the budget period is reconciled and captured, blocks on draft/pending actuals, and compares approved reconciliation adjustments with the difference to the generated Statement of Cash Flows.
- Added PostgreSQL integration tests for reconciled bank evidence, independent certification, self-certification rejection, duplicate source-line rejection, multi-invoice AR receipts and AP payments, multi-line invoice classification, partial single-GL invoice settlements, source-total reconciliation and IPSAS 24 comparison aggregation.

### Still not implemented / not verified

- Certified cash actuals from supported bank, AR receipt and AP payment sources are included in the IPSAS 24 comparison statement. Comparability is blocked when source coverage is incomplete, evidence fails revalidation, or any actual remains unapproved. Period-level zero-activity attestation is implemented in Phase 2A below; non-zero source-population reconciliation still requires a complete source-to-actual coverage ledger.
- Added a Public Sector screen for selecting an approved cash budget line and a bank movement, AR receipt or AP payment source, capturing the event, listing actuals, submitting for certification and certifying as a separate user. The screen filters AP payments to those with recorded independent authorisation.
- Modified-cash commitments, immutable source snapshot hashing, compensating reversal workflow and foreign-currency source conversion remain disabled/outstanding. Zero-activity period attestation is implemented in Phase 2A; full non-zero source completeness remains outstanding.
- The cash-basis actuals are now included in the IPSAS 24 comparison using certified source-linked events, rather than accrual GL journal totals. The comparison remains unavailable on a comparable basis if any in-period bank-statement movement is unreconciled, unlinked, unposted or uncaptured, if any actual is unapproved, or if certified source evidence no longer validates.
- Cash-basis budget lines may map to balance-sheet GL accounts when the account is explicitly classified to operating, investing or financing cash flows. This is necessary for capital acquisitions and financing transactions; the accrual-basis budget line restriction remains income/expense.
- The cash-flow bridge preview is implemented, but it is not yet a statutory statement-issuance gate. The existing CashFlowStatementService requires independent review before its totals can be treated as externally certified figures. A successful budget-comparison source-coverage check must not be interpreted as a completed IPSAS 24 reconciliation to the Statement of Cash Flows.
- AR/AP adapters now support fully settled receipts/payments allocated across multiple invoices and multiple invoice lines. Each captured budget-line amount is derived from the posted invoice-line GL/Fund Centre classification; the full set of source allocations must reconcile exactly to the receipt/payment amount before independent certification. Partial settlements, VAT/WHT splits and cost-centre allocations remain unsupported: invoice lines do not currently carry a cost-centre dimension, and no tax/withholding settlement allocation adapter is enabled. The adapter therefore remains constrained and is not yet general-purpose.
- Modified-cash commitment rules, source snapshot hashing, compensating reversal workflow and foreign-currency source conversion remain disabled/outstanding. Phase 2A implements zero-activity attestation; full non-zero source completeness remains outstanding.
- Cash final budgets must retain zero-value dimensions where a classification was removed from the original budget; otherwise actual execution cannot be captured against that final budget line and the comparison is blocked.
- Migration `20261128060000_AddIpsas24SettlementSources` adds the optional AR invoice Fund Centre dimension. EF snapshot drift, clean PostgreSQL installation and the expanded integration tests must pass in the latest GitHub Actions run before this slice is considered verified.
- After applying the migration in an environment where RLS is enabled, rerun `backend/database/hardening/002_row_level_security.sql` so the newly created company-owned table receives the database policy. Confirm the runtime role remains non-owner and has no `BYPASSRLS`.


## Phase 2A — period completeness and zero-activity certification

Implemented on the feature branch:

- Added tenant/company-scoped `PublicSectorCashPeriodAttestation` records tied to one approved cash budget version and its exact reporting period.
- Added a Draft → PendingCertification → Certified/Rejected lifecycle with independent certification. The preparer/submitter cannot certify.
- The source population is re-counted at creation, submission and certification across bank transactions, AR receipts and AP payments. A nil return is rejected if any source record exists; it is not inferred from an empty actuals table.
- Certification stores the observed counts and evidence reference. Database constraints enforce valid periods/non-negative counts, and a database trigger prevents deletion, post-finalisation edits and invalid state transitions.
- Added tenant-scoped API endpoints to list, create, submit and certify period attestations.
- The IPSAS 24 comparison now permits zero cash actuals only when a matching independent zero-activity attestation is certified and a live re-count still confirms zero bank, AR and AP source records.
- Added a PostgreSQL integration test for the blocked-without-attestation path, independent approval, zero-count evidence and the comparable zero-actual path.
- Added migration `20261128070000_AddIpsas24CashPeriodAttestations`; the shared RLS hardening script discovers company-owned tables by `CompanyId`, so the new table is included by the same policy installation process.

Not yet verified / remaining in this slice:

- Verified by GitHub Actions run #46117505399 on commit `ec9a274991c40966b3ee8521794aca1129d45b5c`: EF model/migration drift check, clean PostgreSQL migration, idempotent hardening scripts, least-privilege runtime role, API boot with RLS, backend build/tests and frontend typecheck/tests/build all passed.
- The Public Sector UI includes nil-return creation, submission and independent certification controls, plus observed bank/AR/AP source counts and attestation status.
- The first completeness rule is intentionally strict: any in-period bank transaction, AR receipt or AP payment means the period is not zero-activity. Non-zero source population reconciliation still needs a general source-to-actual completeness ledger. Current coverage checks bank movements against captured bank transactions or AR/AP settlement journal IDs, but does not yet prove every AR/AP source population is fully mapped to approved actual allocations.
- A zero-activity attestation is not, by itself, a completed Statement of Cash Flows reconciliation or statutory statement-issuance approval. The UI exposes the declaration workflow; source counts are revalidated by the service at creation, submission, certification and comparison time.


## Regression verification — omitted posted settlement sources

The PostgreSQL integration suite now includes a non-zero-period regression case in which the approved cash budget has valid certified AR/AP actuals, but a further posted AR receipt and AP payment are deliberately left uncaptured. The comparison must become non-comparable and report both missing source populations. This protects against a false-completeness result caused by capturing only some settlements.

Verification: GitHub Actions run [#694](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46120594919) completed successfully for commit `d55aa2cf90a0816863d95224eb43a59af707f960` (backend build and PostgreSQL integration tests, frontend checks, migration drift, clean install, database hardening, least-privilege runtime, backup/restore and RLS rollback).

This test verifies the currently implemented posted AR/AP settlement coverage gate. It does **not** verify the not-yet-implemented expected-source register, source submission/control-total workflow, or independent source-period close. Those remain separate acceptance requirements.


## 2026-11-28 implementation and verification update

The cash-flow bridge now applies the same source-completeness controls as budget comparability:

- An empty execution-actual ledger is accepted only when the approved cash budget version has a certified zero-activity attestation, certification metadata and zero recorded counts, and a fresh count of bank transactions, AR receipts and AP payments remains zero.
- For periods with activity, every posted AR receipt and AP payment in the period must be represented either by a direct certified budget actual or by a captured bank actual linked to the same settlement journal. Missing AR and AP settlement coverage is reported separately and blocks the bridge.
- The bridge response exposes whether the certified zero-activity attestation is valid and the missing posted AR/AP settlement coverage counts.

Verification on PR head `1291dd73f9798b7231109b29227828570f7150a4`:
- Backend build and PostgreSQL integration tests: passed.
- Migration drift, clean-install, hardening-script and boot checks: passed.
- Frontend typecheck, tests and build: passed.
- GitHub Actions run: [46121539342](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46121539342).

### Remaining completeness control

A certified nil-return is not, by itself, proof that the expected source population is complete. Before the period can be closed or a statutory comparison issued, the entity needs an approved register of expected source systems/accounts for the reporting entity and period. Each expected source must then have a period submission with independently reviewable record counts, control totals, currency, opening/closing balances and evidence. A source absent from the register or lacking a reviewed submission must remain *unknown/incomplete*, never be inferred as zero.

The expected-source register and source-submission/reconciliation workflow remain outstanding. They require tenant-scoped entities, independent approval/review, immutable final states, database constraints and migration coverage; the existing cash-period attestation is not a substitute for those controls.


### Expected cash-source register: first implementation slice

The branch now includes a company-scoped expected cash-source register linked to an approved cash-basis budget version:

- Supported source categories are bank accounts, receivables control, payables control and cashbook.
- Each entry records a source code/name, optional source identifier, required/optional flag and effective dates contained within the approved budget period.
- Entries follow Draft → PendingApproval → Approved. The preparer/submitter cannot approve their own entry.
- The register has a unique company + budget-version + source-kind + source-code key, a budget-version foreign key, a period check constraint, tenant query filtering and a PostgreSQL trigger preventing deletion or mutation after approval.
- Read, create, submit and approve API routes are available under `/api/v1/public-sector/expected-cash-sources`.

This is a register-management slice, not yet a complete source-close control. It does not yet capture period submissions, source record counts/control totals, opening/closing balances, evidence packages, record-level mapping to budget actuals, approved exclusions or independent period close. Until those controls are implemented, the register must not be used to certify period completeness or unlock nil-return/budget comparison on its own.


### Period source submissions: control-total capture and independent review

The next workflow slice adds one period submission for each approved expected source. A submission captures the source's effective period, record count, control total, opening balance, closing balance, three-letter currency code and supporting evidence reference. The preparer submits it for review; a different user must review and accept or reject it, with a reason mandatory for rejection.

The database enforces non-negative record counts, valid periods, three-character currency codes, uniqueness per company/expected source, restricted foreign keys and immutable reviewed/rejected submissions. The API exposes list, create, submit and review operations under `/api/v1/public-sector/cash-source-submissions`.

**Important remaining limitation:** these submissions currently capture source-level control totals; they do not yet reconcile individual bank/AR/AP source record IDs to certified budget actuals or approved exclusions. They also do not yet certify that the expected-source register itself is complete. Consequently, neither a reviewed submission nor the register alone may be treated as proof that every source record has been accounted for, and no period-close or statutory-issuance gate should rely on this slice yet.

### Expected-source submission verification — 28 November 2026

The expected-source register and period source-submission slice are now verified together on the feature branch. GitHub Actions run [#46122871171](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46122871171) completed successfully for PR head `d4976e08888f606ddb37b7a52578c2940bda9b57`:

- Backend build and PostgreSQL integration tests: passed.
- EF migration drift, clean PostgreSQL installation, database hardening and API boot checks: passed.
- Frontend typecheck, tests and production build: passed.

The preceding run [#46122797856](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46122797856) also completed successfully across all three jobs after the explicit expected-source-submission foreign-key index was aligned between the EF model, migration and model snapshot.

This verification covers the register and source-level control submission workflows only. It does not change the stated boundary: record-level source-to-actual reconciliation, independently approved exclusions, certification of expected-register completeness and period-close/statutory-issuance gates remain outstanding. No production migration has been applied.
