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
- Budget-to-financial-statement reconciliation supports separate basis, timing, entity and presentation differences. The service calculates fingerprinted Revenue and Expense candidates from the approved budget-comparable actuals and registered-entity financial-statement actuals. The automatic reconciliation endpoint creates the exact calculated amount (including an explicit zero-difference attestation), stores its source fingerprint and calculation timestamp, and blocks duplicates for the same budget version/period/entity/type/section. Approval re-runs the comparison and rejects a changed source fingerprint or amount. Cash-basis budget comparisons require independently approved Revenue and Expense adjustment rows or explicit no-difference attestations before statutory export. The separate cash-flow bridge remains an additional gate. The Public Sector UI/API supports creating, listing, submitting and finalising these adjustments after independent workflow approval.
- The cash source register supports bank accounts, receivables control accounts and payables control accounts. Each source is identified by its company-owned internal account ID. Unsupported Cashbook entries are rejected rather than treated as complete.
- Each expected source has a period submission with record count, control total, currency, opening/closing balances and evidence reference. Source records must be mapped exactly to one or more certified budget actual allocations or to a reason-coded exclusion. Split AR/AP allocations are supported and must reconcile exactly to the underlying source amount. One actual allocation cannot be used twice.
- Live source populations are re-read at source submission creation/review, record-reconciliation submission/review, register certification and each budget-comparison/bridge read. Record IDs, per-record amounts, canonical source references, effective dates, currencies, posted-journal links, counts and control totals must still agree. Bank statements also satisfy opening balance + net movement = closing balance. A late or changed source record invalidates the comparison. A reasoned numbered replacement submission is supported after a rejected or previously reviewed submission; the earlier reviewed revision remains immutable and the latest revision is the one used for comparability. This supports new or corrected source-population evidence and re-review. A separately controlled compensating-reversal workflow is now implemented for already-certified actuals: the original evidence is retained, the initiator must be independent of the original preparer/certifier, only one reversal is permitted, the reversal requires independent certification, and the original is marked reversed atomically. The comparison includes the certified reversal as a negative amount. The database permits recapture against the genuine source allocation only after reversal certification; reversal rows are excluded from source-population completeness counts.
- Exclusions require a reason and independent record-level review. Reviewed exclusions count as accounted-for source records, but do not become budget actuals; resulting differences must still be explained through the approved reconciliation.
- A separate expected-source-register completeness attestation records entity scope, declared source count and evidence. It requires independent certification and freezes later additions to the certified register. This is a management attestation supported by evidence, not automated discovery of undisclosed bank accounts or source systems.
- The cash-budget comparison and cash-flow bridge fail closed when the register, source submissions, record mappings, source populations or certifications are missing, unreviewed, stale or inconsistent. CSV export is withheld until comparable actuals, original-to-final explanations and non-zero variance explanations are complete.

### Latest branch verification — 28 November 2026

**All CI jobs passed.**

Current PR head commit `d303a2b7b3ea5db99af76ceca645a96a62617df5`, GitHub Actions run [#46149077075](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46149077075), passed the complete CI matrix:

- Backend build and **342/342 unit and PostgreSQL integration tests**.
- Frontend type-check, tests and production build.
- EF Core migration drift check, clean PostgreSQL installation, hardening scripts (including repeat execution), least-privilege runtime role, API boot with RLS, backup/restore round-trip and RLS rollback verification.

The 342-test suite includes the IPSAS 24 source-population and register-completeness controls, certified actual reversal/replacement, approved FX-rate provenance and stale-rate invalidation, AR/AP tax posting and settlement-component evidence, WHT remittance controls, AR/AP opening/closing control-balance reconciliation, automatic basis/timing/entity/presentation candidate creation, independent reconciliation approval and stale-source invalidation. The migration job confirms the model snapshot and migrations agree on this head. This is CI verification of the feature branch, not a production migration, statutory approval or audit opinion.

The prior 338/338 run `#46138757689` is superseded by this current-head result.

### Cash-flow bridge hardening — 28 November 2026

- The IPSAS 24 cash-flow bridge has two independent reconciliation gates: each operating/investing/financing budget-to-financial-statement adjustment must reconcile to its corresponding section, and the underlying Statement of Cash Flows must itself reconcile opening cash plus net movement to closing cash.
- The reconciliation API exposes the statement-level reconciliation status and difference. A zero difference in the three budget bridge sections cannot mask an internally unreconciled Statement of Cash Flows.
- Adjustment retrieval is scoped to the selected approved cash budget version, exact budget period, registered reporting entity and matching Operating/Investing/Financing section. It cannot import approved adjustments from another version, period or statement section. The current-head CI run #46149077075 passed the full migration and backend test matrix.
- Remaining control boundary: cash-flow adjustment amounts still require independent approval and the bridge preview checks that the approved aggregate adjustments reconcile to each cash-flow section. Independent certification of final statutory cash-flow totals and signed statement issue approval remain external release gates.

### Remaining boundaries — do not mark statutory IPSAS 24 release complete

1. **Modified-cash budgets:** a controlled implementation now supports approved, company-scoped policies, explicit commitment-event-to-budget-line mappings, certified recognition events, partial AP-payment liquidation and independently certified reversal. The only enabled recognition event class is GoodsReceived. No policy or tax/commitment rule is preconfigured by default; the reporting entity must approve the effective dates, cut-off, recognition window, treatment and mappings. A purchase order or encumbrance alone is never a cash flow.
2. **External source authentication and cashbook ingestion:** external bank statements can be imported through a strict versioned UTF-8 CSV adapter. Original bytes, digest, metadata, parsed rows and batch identity are retained; bank-origin authentication currently requires independent human bank confirmation. Trusted API and digital-signature modes remain disabled until verifiers exist. A dedicated external cashbook adapter is still outstanding. A strict pure CSV parser now validates the documented v1 header, UTF-8, transaction identities, dates, currency, debit/credit totals, opening/closing balance, reversal references and in-file duplicates; it does not persist or authenticate the source, reconcile against bank/AR/AP, map budget actuals or certify completeness. The versioned interchange contract, tenant boundaries and no-double-counting rules are defined in [IPSAS 24 external cashbook interchange contract](IPSAS24_EXTERNAL_CASHBOOK_INTERCHANGE.md). This parser is not a deployed adapter; production activation still requires persistence, source-owner authentication, source-specific approval and a representative sample.
3. **Tax scope:** VAT and WHT are supported through effective-dated mappings, immutable posted tax-component provenance, explicit settlement allocations, approved budget-treatment policies and separate reconciled WHT remittance. Zimbabwe's 5% Electronic Commerce Operators' Tax (DST under Income Tax Act section 12A) is legally distinct from the 15.5%/3/23 DSWT VAT withholding mechanism under VAT Act section 13A. A separate DST evaluator and quarterly calendar now calculate the operator-level 5% liability and statutory windows, but do not yet provide revenue-source registration, approval, tax ledger, return filing, payment reconciliation or GL posting. The DSWT register does not implement those DST workflows either; neither DSWT nor DST is enabled as an IPSAS 24 tax actual type. See [Zimbabwe digital tax distinction and incidence](ZIMBABWE_DIGITAL_SERVICES_WITHHOLDING_TAX.md#separate-tax-electronic-commerce-operators-tax-dst--do-not-confuse-with-dswt). Multi-tax invoices and multi-line WHT inclusion have the constraints documented in `docs/IPSAS24_AR_AP_TAX_ALLOCATION_DESIGN.md`.
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

**Status: partially implemented and deliberately restricted to evidenced goods-received obligations under an independently approved policy. It is not a blanket recognition of purchase-order commitments and is not enabled for statutory reporting without entity-specific policy approval.**

The current implementation provides:
- Company-scoped, effective-dated modified-cash recognition policies tied to an approved cash-basis budget version, with independently approved mappings to that budget's expense lines.
- A fail-closed event adapter for `GoodsReceived` only. It requires an active commitment, a goods-receipt line belonging to the committed purchase order, a mapped expense line in the same Fund Centre, reporting/effective-period compliance, a positive evidenced amount and a remaining unrecognised commitment balance.
- Append-only recognition, liquidation and reversal ledger events. Recognition is not an ordinary cash payment or an IPSAS financial-statement expense.
- Independent policy approval and separate event submission/certification. The policy preparer cannot approve their own policy; the event preparer cannot certify their own event.
- AP liquidation only after a posted, independently authorised bank-transfer payment is linked to a reconciled bank transaction, matching bank GL credit and functional currency. Partial liquidations are supported and cannot exceed the unliquidated recognition.
- Controlled reversals with source evidence and independent certification. Reversal and liquidation cannot both consume the same original recognition; the original record is retained.

**Explicitly unsupported:** approved PO/contract at award, services received, verified AP invoice without a goods receipt, payroll/accrual events, and other commitment stages. The API rejects unimplemented event kinds; it must not accept them by merely adding a mapping label. Each additional event kind requires a dedicated source adapter, independently evidenced trigger/date, exact budget-line allocation, duplicate protection, partial-realisation/liquidation treatment, reversal rules and PostgreSQL integration tests.

**Reporting and legal boundary:** unliquidated obligations are not cash flows and must be separately reconciled/disclosed against the Statement of Cash Flows. A policy is an entity-specific accounting decision; no universal Zimbabwe public-sector modified-cash recognition trigger is preconfigured. The basis must not be used for statutory reporting until the Accounting Officer/Treasury policy, reporting basis, classification and disclosure treatment are supplied and independently approved.

Acceptance coverage currently includes policy approval segregation, company scoping, duplicate source prevention, exact classification mapping, commitment balance checks, period/evidence cut-off, AP settlement validation, partial liquidation and reversal controls. Remaining event kinds and external statutory acceptance remain open.

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

## Explicit remaining controls — six coordinated workstreams

The six workstreams below are tracked as one completion programme. A code-level control is not treated as complete where a bank, ZIMRA, Treasury, reporting entity or independent assurance provider must supply evidence or approval.

1. **External cashbook/subledger ingestion — open.** The existing source-register workflow reconciles ApexERP's live bank, AR and AP populations, and the bank-statement importer authenticates statement evidence through independent bank confirmation. A separate generic external cashbook adapter is not implemented. It requires a source-system interchange contract, immutable source export, record identity and hashing, duplicate/idempotency rules, opening/closing/control-total checks, mapping to source actuals or reason-coded exclusions, tenant scoping and independent review. Do not label the existing in-app source-register submission as external ingestion.
2. **Modified-cash event coverage — partially implemented.** Goods-received obligations are supported under an approved company policy, with independent approval/certification, AP settlement liquidation and reversal. Other event classes (approved PO/contract, services received, verified invoice without goods receipt, payroll and other legally defined triggers) remain disabled until each has a real source adapter, policy-approved trigger, allocation, liquidation and reversal controls plus PostgreSQL tests.
3. **Trusted bank API/digital-signature verification — open pending provider evidence.** Independent bank confirmation is the only enabled authentication method. SHA-256 proves that retained bytes have not changed; it does not prove bank authorship. Trusted API and digital-signature modes remain disabled until a named bank supplies an API/signature specification, trust chain/key distribution, server-side verification credentials, revocation/rotation process and test evidence. No "verified" state may be set by a caller-supplied boolean or reference.
4. **DSWT/ZIMRA operational acceptance — partially implemented, external acceptance open.** DSWT calculation, withholding evidence register, AP gross-payment and bank reconciliation, return preparation and manual TaRMS filing evidence are implemented. Direct TaRMS API transmission remains disabled pending an authorised ZIMRA interface contract and credentials. Entity-specific TaRMS tax-type registration, current ZIMRA confirmation, contract review for any gross-up, and tax/legal sign-off are external release gates. DSWT support in the AP register must not be represented as end-to-end IPSAS 24 tax-budget allocation; the IPSAS 24 tax actual adapter currently supports VAT and WHT only.
5. **IPSAS 24 reconciliation/disclosure completeness — software workflow implemented; statutory configuration and entity acceptance open.** Original/final budget comparison, live source-population revalidation, record-level reconciliation, exclusions, certified reversal/replacement, cash-flow bridge calculations and independent bridge-adjustment approvals are implemented. A hash-pinned statement-issue workflow also exists: statement preparation is blocked until comparability, basis/timing/entity/presentation reconciliations, variance explanations and (for cash budgets) the operating/investing/financing cash-flow bridge are complete; review, approval and publication require four distinct actors (preparer, reviewer, approver and publisher), evidence references and decision notes; every transition revalidates the live snapshot and rejects changed source/budget/template data. The workflow records evidence references; it is not a cryptographic digital-signature service. Still required: independent verification of the current Treasury template and approved 2026 classification dictionaries/entity schedules; confirmation of applicable ZFRM version; formal reporting-entity scope for consolidations; and a formally approved materiality policy (the software currently requires explanations for all non-zero comparable variances). Multi-entity consolidation and partial-entity subsets remain blocked until a controlled consolidation adapter and approved accounting policy exist.
6. **Independent control testing — technical CI passed; independent assurance remains open.** At commit `2c4aac4d97d1c6ef1c397162b7aff8dd34181973`, GitHub Actions workflow [#46165414971](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46165414971) passed backend build and all 407 PostgreSQL-backed tests, frontend typecheck/tests/build and dependency audit, migration drift, clean database migration, hardening idempotency, least-privilege runtime/RLS boot and backup/restore. This is automated engineering verification, not an independent audit. Remaining assurance requires an independent reviewer to execute the documented control scenarios against a release candidate, inspect evidence/audit trails and sign the acceptance record. Any code changes after this commit require a fresh full CI run.

### Release rule

The six workstreams are not all closed. External provider specifications, taxpayer registration, Treasury instructions, entity-specific accounting policies and independent assurance cannot be manufactured by code. Keep the PR in draft and do not merge or issue statutory statements until the relevant external gates have evidence, approval and a fresh passing CI run.

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




## Verification status — 28 November 2026

The latest code verification at commit `13d9ca82e00db41b45a01a401076e6e2f5951504`, GitHub Actions [#46156147044](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/runs/46156147044), passed all three jobs: backend build and 372/372 unit/PostgreSQL integration tests; frontend type-check, tests, production build and dependency audit; and EF model/migration drift, clean PostgreSQL install, hardening scripts, least-privilege runtime role, RLS boot and backup/restore. This verifies the code branch at that commit; the documentation-only follow-up and subsequent changes require their own CI run.

### Remaining implementation contract: modified-cash recognition

The modified-cash policy and ledger workflow is implemented for the GoodsReceived event class only. It requires an approved cash-budget version, a company-scoped and independently approved policy, effective dates, reporting period, post-period evidence window, explicit budget-line mapping and IncludeInBudgetActual or DiscloseSeparately treatment. Recognition evidence is linked to the commitment and source event. AP liquidation requires a posted payment allocation, independent payment authorisation, bank account and reconciled bank transaction; multiple partial liquidations are allowed. A certified reversal is permitted only for the unliquidated remainder. The live commitment, invoice, payment, bank and policy evidence is revalidated for comparison. Approved policies and certified ledger events are immutable. PO approval/encumbrance alone is not recognition evidence. Other obligation event classes, amendment adapters and any jurisdiction-specific policy decisions remain disabled until approved.

### Remaining implementation contract: external bank/cashbook evidence

The bank-statement adapter now imports strict versioned UTF-8 CSV, retaining the original file bytes in the database together with SHA-256, media type, byte length, original filename, upload actor/time, source institution and bank-account reference, statement period, parser version and import batch identity. It rejects overlapping periods, duplicate statement references, repeated transaction IDs/fingerprints, malformed rows and control-total or balance mismatches. A separate approval workflow requires independently evidenced bank confirmation; trusted bank API and digital-signature methods are deliberately disabled until server-side verifiers exist. Every statement row must be either matched one-to-one to a posted bank-ledger transaction or independently reviewed with a reason-coded exclusion; the uploader, authenticator, matcher, exclusion reviewer and completeness certifier are separated. Excluded statement rows are retained as source-population records without fabricated journal links, and the later IPSAS 24 source-register reconciliation must separately account for them. The report-time adapter re-hashes retained bytes and revalidates the full live transaction population. The separate external cashbook/subledger adapter is still outstanding.
