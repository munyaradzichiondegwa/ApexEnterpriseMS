# IPSAS 24 budget comparison implementation

**Research basis:** 28 November 2026  
**Jurisdictional presentation:** Zimbabwe Treasury Financial Reporting Manual and Treasury Circular No. 3 of 2025. This is a technical product mapping, not an audit opinion or legal certification.

## Authoritative requirements

- IPSAS 24 requires a comparison of original budget, final budget and actual amounts on a comparable basis, with explanations of material differences. Where budget and financial statements are not prepared on a comparable basis, the actual amounts must be reconciled and differences identified as basis, timing, entity and presentation differences. See [PSASB IPSAS 24 Guideline](https://www.psasb.go.ke/sites/default/files/PSASB/Accounting%20Standards/Guidelines/IPSAS%2024%20Guideline.pdf).
- Zimbabwe Treasury requires the comparison to be presented as a separate additional financial statement and on a basis comparable to the budget. Appendix 5 of the available ZFRM specifies the Notes and Comment Ref columns as well as original, final, comparable actual and difference columns. ApexERP captures explicit note references on budget lines and comment references on variance explanations. See [Zimbabwe Financial Reporting Manual — section 4.6 and Appendix 5](https://zimtreasury.co.zw/wp-content/uploads/2025/03/zfrm-soft-copy-1-1.pdf) and [Treasury Circular No. 3 of 2025, Appendix 5](https://zimtreasury.co.zw/wp-content/uploads/2025/03/Treasury-Circular-No-3-of-2025-on-2024-IPSAS-Transitional-Financial-Statements-1.pdf).
- IPSAS 24 material-variance explanations include changes from original to final budget and final budget to actual. Budget basis, classification basis, reporting period and entities covered must be disclosed.

## Current implementation status — 28 November 2026

**This section supersedes older phase-status statements later in this document.** The feature branch now contains a broader IPSAS 24 implementation than the historical notes below describe. It is not yet a complete statutory-release certification.

### Implemented controls

- Original and final budget snapshots are separately approved, immutable versions. The comparison presents original budget, final budget and actual on a comparable basis. A budget dimension absent from one version is shown as zero in that version, not as an unknown amount.
- Comparison rows expose the Fund Centre hierarchy level (Vote, Programme or Sub-Programme) and the Cost Centre code/name alongside GL classification, so the legislative and management dimensions are visible in both the UI and CSV export. GL SubType remains validated against the account; a separate Treasury classification category is required on every line when the classification basis is ZFRM Appendix 5. Budget lines carry a separate `NoteReference`; variance explanations carry a `CommentReference`. The CSV export orders the Appendix 5 columns as Budget Line, Notes, Original, Final, Actual Amounts on Comparable Basis, Difference between Final Budget and Actual, and Comment Ref, followed by reporting metadata and supporting dimensions. It appends the total surplus/(deficit) row for revenue/expense statements.
- Accrual actuals are derived from posted/reversed GL journal lines and are withheld when reporting period, company entity scope, functional currency, classification, Fund Centre or cost-centre dimensions do not agree. Untagged actuals in budgeted GL accounts block comparability.
- Cash actuals are derived only from certified bank transactions, AR receipts and AP payments. Accrual GL totals are never substituted for cash actuals. AR/AP source adapters now retain company-scoped, effective-dated tax-account mappings and immutable invoice tax-component provenance linked to exact posted journal lines and mapping versions. Tax-bearing settlements and partial multi-line settlements require explicit settlement-component allocations that reconcile exactly to each invoice settlement; tax-free full invoices (and a single-line partial settlement) may be allocated deterministically by invoice line. No proportional tax split is inferred. AP WHT is excluded from supplier cash allocations and requires separate reconciled bank-remittance evidence. Independently approved, effective-dated tax-budget policies determine whether VAT/WHT is included, excluded or separately classified in budget actuals. The source validators recheck these policies and component/journal evidence at certification. Tax-bearing certification remains conditional on the entity's approved policy configuration and successful latest CI.
- Revised budgets require an authority reference, a reason and an explicit amendment nature: reallocation within the approved budget, other authorised factors, or both. The comparison exposes the classification and reason for original-to-final changes. Legacy revised budgets without this new metadata remain incomplete for disclosure.
- Non-zero final-budget-to-actual variances require an evidence-backed explanation, submission and independent approval. The variance amount is recalculated at submission and approval. Until a formally approved entity-specific materiality threshold exists, every non-zero variance requires explanation.
- Budget-to-financial-statement reconciliation supports separate basis, timing, entity and presentation differences. Revenue and expense adjustments reconcile budget-comparable actuals to accrual financial-statement actuals; operating/investing/financing adjustments separately reconcile the Statement of Cash Flows. Cash-basis budget comparisons require independently approved Revenue and Expense adjustment rows or explicit no-difference attestations before statutory export. The separate cash-flow bridge remains an additional gate. The Public Sector UI supports creating, listing, submitting and finalising these adjustments after independent workflow approval; zero-difference attestations also require independent approval.
- The cash source register supports bank accounts, receivables control accounts and payables control accounts. Each source is identified by its company-owned internal account ID. Unsupported Cashbook entries are rejected rather than treated as complete.
- Each expected source has a period submission with record count, control total, currency, opening/closing balances and evidence reference. Source records must be mapped exactly to one or more certified budget actual allocations or to a reason-coded exclusion. Split AR/AP allocations are supported and must reconcile exactly to the underlying source amount. One actual allocation cannot be used twice.
- Live source populations are re-read at source submission creation/review, record-reconciliation submission/review, register certification and each budget-comparison/bridge read. Record IDs, per-record amounts, canonical source references, effective dates, currencies, posted-journal links, counts and control totals must still agree. Bank statements also satisfy opening balance + net movement = closing balance. A late or changed source record invalidates the comparison. A reasoned numbered replacement submission is supported after a rejected or previously reviewed submission; the earlier reviewed revision remains immutable and the latest revision is the one used for comparability. This supports new or corrected source-population evidence and re-review. A separately controlled compensating-reversal workflow is now implemented for already-certified actuals: the original evidence is retained, the initiator must be independent of the original preparer/certifier, only one reversal is permitted, the reversal requires independent certification, and the original is marked reversed atomically. The comparison includes the certified reversal as a negative amount. The database permits recapture against the genuine source allocation only after reversal certification; reversal rows are excluded from source-population completeness counts.
- Exclusions require a reason and independent record-level review. Reviewed exclusions count as accounted-for source records, but do not become budget actuals; resulting differences must still be explained through the approved reconciliation.
- A separate expected-source-register completeness attestation records entity scope, declared source count and evidence. It requires independent certification and freezes later additions to the certified register. This is a management attestation supported by evidence, not automated discovery of undisclosed bank accounts or source systems.
- The cash-budget comparison and cash-flow bridge fail closed when the register, source submissions, record mappings, source populations or certifications are missing, unreviewed, stale or inconsistent. CSV export is withheld until comparable actuals, original-to-final explanations and non-zero variance explanations are complete.

### Latest branch verification — 28 November 2026

Commit `9d2707643e1703638bac70f887ccde445f9d4469`, GitHub Actions run [#46138757689](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46138757689), passed all three CI jobs:

- Backend build and **338/338 unit and PostgreSQL integration tests**.
- Frontend type-check, tests and production build.
- EF Core migration drift check, clean PostgreSQL installation, database hardening, RLS runtime boot and migration checks.

The backend suite includes regression coverage for reviewed source-submission replacement revisions, certified actual reversal and replacement, independent reversal certification, source-population completeness treatment, approved FX-rate provenance and stale-rate invalidation, explicit AR/AP invoice-line GL/cost-centre allocation, and AR/AP opening/closing control-balance reconciliation, in addition to the earlier cash actual, variance, register-completeness and tenant-isolation controls. This is CI verification of the feature branch, not a production migration or statutory approval.

### Cash-flow bridge hardening — 28 November 2026

- The IPSAS 24 cash-flow bridge now has two independent reconciliation gates: each operating/investing/financing budget-to-financial-statement adjustment must reconcile to its corresponding section, and the underlying Statement of Cash Flows must itself reconcile opening cash plus net movement to closing cash.
- The reconciliation API exposes the statement-level reconciliation status and difference. A zero difference in the three budget bridge sections can no longer mask an internally unreconciled Statement of Cash Flows.
- This code change is awaiting the branch CI result. It does not establish that the cash-flow statement's underlying classification methodology is correct, nor does it replace independent certification of the financial-statement totals.

### Remaining boundaries — do not mark statutory IPSAS 24 release complete

1. **Modified-cash budgets:** not enabled. There is no approved entity-specific recognition policy for commitments/obligations, approved commitment-to-budget-line mapping or controlled liquidation/reversal treatment. A purchase order or encumbrance is not a cash flow.
2. **External source authentication:** bank, AR and AP populations already held in ApexERP are revalidated, but external statements are not cryptographically imported or authenticated and no dedicated cashbook adapter exists. Retain authoritative exports and require independent comparison with the in-system population.
3. **Tax scope:** VAT and WHT are supported through effective-dated mappings, immutable posted tax-component provenance, explicit settlement allocations, approved budget-treatment policies and separate reconciled WHT remittance. DST and other tax types are not enabled. Multi-tax invoices and multi-line WHT inclusion have the constraints documented in `docs/IPSAS24_AR_AP_TAX_ALLOCATION_DESIGN.md`.
4. **Comparable entity scope:** accrual actuals support automatic basis, timing, entity and presentation difference calculations for the registered company, including a different financial-statement period. Multiple legal entities and partial-entity subsets sourced from external ledgers are not automatically consolidated; such scope is blocked until a controlled entity adapter and policy exist.
5. **Cash-flow adjustment integrity:** the preview validates source coverage, each operating/investing/financing section and the underlying Statement of Cash Flows reconciliation. Adjustment retrieval has been hardened to select only independently approved rows for the exact budget version, period, registered reporting entity and matching section. The hardening and tax/remittance work still require a successful current-head backend CI run.
6. **AR/AP control balances and source inventory:** opening/closing AR/AP control balances are checked against the GL, and late postings invalidate review. Source-register completeness remains an independently certified management assertion; ApexERP cannot discover undisclosed accounts or source systems. The entity must maintain a complete approved source and chart-of-accounts inventory.
7. **Zimbabwe statutory configuration:** Treasury Circular No. 3 of 2025 relates to 2024 transitional statements and is not treated as the 2026 instruction. The latest Treasury-approved 2026 template, classification dictionaries, legislative oversight aggregation and jurisdiction-specific rules must be supplied and configured before statutory issue.
8. **Issue approval and materiality:** the API comparison/export gates do not constitute signed statutory statement issuance, legal opinion, Treasury acceptance, PAAB approval or audit opinion. The cash-flow bridge requires independently certified final financial-statement cash-flow totals. Every non-zero variance currently requires explanation; a materiality threshold remains disabled until formally approved by the reporting entity.

### AR/AP tax allocation engineering decision

Tax-bearing cash actuals are enabled only when the invoice has an approved effective tax-account mapping, immutable tax-component/journal-line provenance, explicit settlement allocation, and an approved effective budget-treatment policy. AP WHT remains separate from supplier cash and can only be recognised as a cash payment through a reconciled bank remittance allocated to the originating WHT components. The implementation and its known boundaries are documented in [IPSAS 24 AR/AP tax allocation design](IPSAS24_AR_AP_TAX_ALLOCATION_DESIGN.md). Entity-approved tax treatment remains a prerequisite; the software does not decide VAT recoverability or statutory tax treatment.

### AR/AP tax-account provenance foundation — 28 November 2026

- AR invoice posting now persists the exact VAT output GL account used. AP invoice posting persists the exact VAT input and WHT payable GL accounts used.
- Posting validates company ownership, active/postable status and expected account class (AR VAT output: liability; AP VAT input: asset; AP WHT payable: liability).
- Added migration `20261128120000_PersistArApTaxPostingAccounts`; historical rows remain null rather than being backfilled by inference. PostgreSQL regressions cover valid AR/AP persistence and invalid AR VAT account type/company.
- Subsequent implementation added effective-dated tax-account mappings, immutable invoice tax components linked to exact journal lines, explicit AR/AP settlement-component allocations, independent effective-dated VAT/WHT budget-treatment policies, and separate WHT remittance allocations from reconciled bank transactions. See the detailed design record for current limitations and acceptance boundaries.

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

**Correction boundary:** a reasoned numbered replacement submission can follow a rejected or previously reviewed submission. The earlier revision remains immutable; the replacement must pass fresh independent control and record-level review. If a previously certified actual is changed or misclassified, the original is preserved and corrected only through a compensating reversal, independent certification and separately validated replacement. The period remains blocked until the replacement and latest source-population revision are reviewed.

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

1. **Modified-cash budgets:** not supported. Commitment recognition is not inferred from purchase orders, goods receipts, invoices or encumbrances. An entity-specific policy must define eligible obligations, recognition triggers, classification, liquidation and reversal treatment before modified-cash actuals can be enabled.
2. **External source authentication:** ApexERP revalidates controlled bank, AR and AP populations already held in the system, but does not cryptographically ingest or authenticate external bank statements or subledger exports. Original source exports must be retained and independently compared with the in-system population. A dedicated cashbook adapter is not implemented.
3. **Tax coverage and policy:** VAT and WHT settlement controls are implemented, but DST and other tax types are not enabled. Each reporting entity must approve effective-dated tax mappings and budget-treatment policies. Multi-tax invoices currently require the supported control-account mapping shape; mixed-purpose WHT remittances and multi-line WHT inclusion have documented restrictions in `docs/IPSAS24_AR_AP_TAX_ALLOCATION_DESIGN.md`.
4. **Comparable reporting scope:** accrual comparisons calculate basis, timing, entity and presentation candidates for the registered company, including differing statement periods. The system does not yet consolidate multiple legal entities or automatically build partial-entity subsets from external ledgers. Any entity scope beyond the registered company remains blocked pending a controlled entity-ledger adapter and approved consolidation policy.
5. **Cash-flow bridge approval:** the preview independently checks source completeness, each operating/investing/financing section and the underlying Statement of Cash Flows reconciliation. Bridge adjustment rows must be independently approved. A current hardening change now scopes those approvals to the selected budget version, period, registered reporting entity and cash-flow section; this change still requires CI verification. Independent certification of final statutory cash-flow totals and a signed statutory statement-issue workflow remain outstanding.
6. **Accrual dimensions and source inventory:** missing or unmatched Fund Centre/cost-centre dimensions block comparability. Register completeness is an independently certified management assertion; ApexERP cannot discover undisclosed bank accounts or source systems. The entity remains responsible for a complete approved chart-of-accounts, source-system and reporting-entity inventory.
7. **Zimbabwe statutory configuration:** the available ZFRM includes Appendix 5, with Notes and Comment Ref columns. ApexERP captures those references and exports the core columns. The available manual is the 2023 edition; Treasury must confirm whether a later circular or replacement template applies to the reporting year. Detailed current Treasury revenue/expenditure dictionaries, entity-specific schedules and legislative oversight aggregations require approved configuration. See [Zimbabwe Financial Reporting Manual — Appendix 5](https://zimtreasury.co.zw/wp-content/uploads/2025/03/zfrm-soft-copy-1-1.pdf).
8. **Statutory approval and materiality:** software comparison/export gates do not constitute legal opinion, Treasury acceptance, PAAB approval or audit opinion. Every non-zero comparable variance currently requires an explanation; a materiality threshold remains disabled until formally approved by the reporting entity.

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


## Historical implementation notes

The earlier phase-by-phase notes have been removed from this current-status document because they described intermediate states that are superseded by the implementation and remaining-boundary sections above. Commit history and the regression acceptance matrix provide the implementation audit trail. Use the status and limitations at the beginning of this document as the authoritative current position.

