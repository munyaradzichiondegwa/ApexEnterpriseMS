# IPSAS 24 AR/AP tax allocation controls

**Status:** implemented on the feature branch; backend CI verification for the tax/remittance changes and subsequent cash-flow bridge hardening is pending. Migration/model and frontend checks passed in the previously recorded run, but that is not a complete backend acceptance result.  
**Scope:** tax-bearing cash actuals from posted AR receipts, AP supplier payments and separate WHT remittances.

## Accounting rules enforced

- AR VAT is part of the customer receipt but is not automatically treated as revenue. Its budget treatment is controlled by an approved policy for the specific VAT tax code.
- AP supplier cash is the net amount paid to the supplier. WHT withheld is a separate liability and is never allocated as supplier cash.
- AP VAT input may be recoverable or irrecoverable. The treatment is configured by tax code; the software does not infer recoverability from the tax type or GL account.
- WHT becomes a cash payment only when remitted to the tax authority from a reconciled bank transaction. The remittance is allocated to the original posted WHT components and the WHT payable journal line.
- No tax is prorated across budget dimensions. Partial multi-line settlements require explicit component allocations.

## Implemented data model

1. **TaxAccountMapping** — company-scoped, effective-dated, versioned tax control-account mapping. Supported purposes are AR-VAT-OUTPUT, AP-VAT-INPUT and AP-WHT-PAYABLE. The GL account must be company-owned, active, postable and of the required type. Mapping approval uses an independent workflow. An approved version is immutable; a later effective-dated version supersedes it for subsequent invoice dates.
2. **InvoiceTaxComponent** — immutable invoice tax snapshot containing invoice kind/ID, originating invoice line where applicable, tax code/type, taxable base, configured rate, amount, GL account, mapping ID/version, journal entry ID and exact journal-line ID. Tax-code identity is also carried on the posted JournalLine.
3. **SettlementComponentAllocation** — immutable allocation of a receipt/payment to an invoice line or tax component. AR and AP rows identify supplier/customer settlement cash. TX rows identify WHT components remitted through a reconciled bank transaction. Database constraints enforce valid component shapes, positive amounts and allowed settlement kinds.
4. **PublicSectorTaxBudgetPolicy** — company-scoped, effective-dated and independently approved policy, keyed by VAT tax code or the code-less WHT component. Treatment is IncludeInBudgetActual, ExcludeFromBudgetActual or SeparateClassification. Separate classification requires an active budget GL account and Fund Centre, with an optional active Cost Centre. Approved policy versions are immutable; the latest effective approved version is applied.

## Posting and settlement controls

- AR posting requires active company-owned VAT tax codes effective on the invoice date, validates line VAT against the configured rate, reconciles invoice line totals to the header, requires an approved AR-VAT-OUTPUT mapping and persists line-level VAT components against the exact posted tax journal line.
- AP posting applies the equivalent controls for AP-VAT-INPUT. If WHT applies, posting requires an approved AP-WHT-PAYABLE mapping and persists a separate WHT component against its exact posted journal line.
- The AR receipt and AP payment operations accept optional componentAllocations. Tax-bearing settlements require explicit allocations to invoice lines and VAT components. Partial multi-line settlements also require explicit allocation evidence. Tax-free full settlements may be deterministically allocated by invoice line; partial multi-line settlements are not guessed.
- AR/AP settlement and GL posting execute within one transaction. Component allocations cannot exceed the original invoice-line or tax-component balance, cannot be duplicated, and must sum exactly to the parent invoice settlement allocation.
- WHT remittance uses POST bank-transactions/{id}/tax-remittance-allocations. It requires a reconciled outgoing bank transaction, a posted journal, exact WHT component and mapping provenance, approved WHT budget policy, and remittance amounts that reconcile to the bank debit and the WHT payable debit journal lines. WHT cannot be marked as paid through the AP supplier payment.
- IPSAS 24 actual capture resolves the effective approved policy for each settled tax component. Include treatment follows the originating invoice-line dimensions (or the AP invoice Fund Centre for WHT); exclude treatment does not create a budget actual; separate treatment uses the approved budget GL/Fund Centre/Cost Centre. WHT remittance actuals are resolved against the WHT policy and the separate bank-remittance evidence.
- Certification re-reads the posted invoice, tax journal line, exact mapping version, settlement allocation, effective policy and bank source. Changed or missing evidence fails closed. Tax component snapshots, approved mapping/policy versions and settlement allocations are immutable; correction requires controlled reversal/replacement rather than editing history.

## API policy workflow

Tax mappings and budget policies are created as drafts, submitted through the configured approval chain, and finalised only after the linked workflow is approved by someone other than the preparer. VAT policies must identify the VAT tax code so recoverable and irrecoverable treatments can differ. WHT policy is currently code-less because the AP supplier model does not yet persist a WHT tax-code reference. Unsupported tax types are rejected.

## Known boundaries

- The implementation currently supports VAT and WHT. DST and other tax types are not enabled for IPSAS 24 actuals.
- An AR invoice and an AP invoice currently use one VAT control-account argument per posting. Multiple tax codes may be represented, but their approved mappings must resolve to that same control account for the invoice.
- WHT remittance allocation requires the bank transaction to be reconciled and its posted journal to contain one exact WHT payable debit line per mapped payable account. Mixed-purpose bank payments containing other debit components are not silently treated as WHT-only remittances.
- WHT IncludeInBudgetActual follows the originating AP expense dimension only for a single-line invoice. Multi-line WHT must use an approved SeparateClassification or ExcludeFromBudgetActual policy; the application does not prorate the invoice-level WHT component across expense lines.
- No legal/tax policy is preconfigured. The reporting entity must approve the effective-dated treatment for each VAT code and WHT. Recoverability, budget classification and Fund Centre policy are entity decisions.
- Modified-cash recognition, Treasury 2026 official classification dictionaries/templates, cashbook ingestion and external tax-authority integrations remain separate acceptance items.

## Acceptance evidence

The PostgreSQL integration suite contains coverage for AR/AP tax posting, exact tax journal-line provenance, explicit settlement allocation evidence, tax policy resolution and WHT remittance allocation. The migration/model and frontend jobs passed in the recorded workflow, but backend verification for the tax/remittance changes and later cash-flow bridge hardening must pass on the current PR head before this package is marked verified.

## Release rule

Tax-bearing actuals are not comparable merely because the invoice is posted. Each source must have a valid approved mapping, a valid approved budget-treatment policy, explicit settlement evidence, and independent source-completeness certification. A passing CI run does not approve the reporting entity's accounting policy or replace Treasury/PAAB review.
