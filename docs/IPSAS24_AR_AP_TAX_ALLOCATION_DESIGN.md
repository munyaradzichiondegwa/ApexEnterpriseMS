# IPSAS 24 AR/AP tax allocation

**Status:** implementation present on `feature/apexerp-consolidated-enterprise`; latest integration verification is pending.  
**Scope:** cash-basis IPSAS 24 actuals sourced from posted AR receipts, AP payments and separate WHT remittances.

## 1. Effective-dated tax-account mappings

`TaxAccountMapping` is company-scoped and versioned. Supported purposes are:

- `AR-VAT-OUTPUT`: VAT output liability account, mapped per VAT tax code.
- `AP-VAT-INPUT`: VAT input asset account, mapped per VAT tax code.
- `AP-WHT-PAYABLE`: WHT payable liability account, currently code-less because supplier WHT is calculated from the supplier's configured WHT rate.

The API provides draft creation, submission and independent finalisation after the linked approval workflow is approved. Posting checks the mapping's company, purpose, tax type/code, approval status and invoice-date effectiveness, and checks that the GL account is active, postable and of the expected account type. The invoice stores the exact control-account IDs used. Approved mapping versions cannot be edited or deleted; corrections require a new version.

## 2. Immutable tax-component provenance

Each posted tax component is stored as an `InvoiceTaxComponent` with invoice kind/ID, originating invoice-line ID where applicable, tax code/type, taxable base, applied rate, amount, GL account, mapping ID/version, journal ID and exact journal-line ID. The posting path verifies that the selected journal line and amount match the tax component. AR and AP VAT tax codes must be active, company-owned, effective on the invoice date and agree with the line VAT calculation. AP WHT is a separate payable component and is not treated as supplier cash.

Invoice tax-component rows and posted journal lines are immutable. Historical invoices are not backfilled with guessed mapping or journal provenance.

## 3. Explicit settlement-component allocation

`SettlementComponentAllocation` records each receipt/payment allocation against either an invoice line or a tax component. The API accepts explicit component allocations for each invoice settlement. Allocations must be positive, unique, reference a component belonging to the same invoice/company and sum exactly to the parent invoice settlement amount. Cumulative allocations cannot exceed the source invoice-line or tax-component amount.

- Partial receipts/payments and one receipt/payment allocated across multiple invoices are supported when the component evidence is explicit.
- A partial settlement across multiple GL/cost-centre dimensions is not prorated. The caller must provide the split.
- VAT-bearing AR receipts and VAT/WHT-bearing AP payments cannot fall back to automatic tax-free allocation.
- WHT is not part of supplier cash. It is excluded from AP supplier-payment component allocations and must be separately evidenced by an outgoing, reconciled bank transaction against the WHT payable journal leg. The WHT remittance endpoint binds each component to its originating AP invoice and validates the bank debit, posted journal and remaining WHT balance.
- Settlement component rows are immutable; corrections use compensating source events and the existing reversal/replacement controls.

## 4. Independently approved IPSAS 24 tax-budget policy

`PublicSectorTaxBudgetPolicy` is company-scoped, versioned and effective-dated. The policy identifies the tax type and, for VAT, the tax code. It specifies one of:

- `IncludeInBudgetActual`: classify the cash-settled component with its originating invoice line's approved budget dimensions.
- `ExcludeFromBudgetActual`: retain the source evidence but exclude that tax component from the budget actual.
- `SeparateClassification`: use the explicitly configured budget GL account and Fund Centre, with an optional cost centre.

Draft policies are submitted to the approval workflow and may only be finalised by a user other than the preparer after that workflow is approved. Separate classifications are validated against company-owned GL, Fund Centre and cost-centre records. Approved policy versions are immutable. No treatment is inferred from VAT/WHT type: the reporting entity must configure and approve its accounting/budget treatment.

The AR/AP source validator revalidates the exact invoice mapping version, effective policy, source settlement allocations, invoice lines, tax journal lines, bank journal and source-currency amounts before certification. WHT remittance actuals use their own bank evidence and the approved WHT policy.

## 5. Reconciliation and remaining boundaries

For AR, invoice gross reconciles to receivables, revenue and VAT; receipt allocations reconcile to the cash received. The approved tax policy determines whether VAT is included with revenue, excluded, or separately classified.

For AP, expense/asset lines, VAT input, WHT payable and AP control reconcile to the posted invoice journal. Supplier cash payment is distinct from WHT withheld; the WHT liability is settled using separate tax-authority bank/payment evidence. The approved tax policy determines the budget treatment of VAT and WHT remittances.

All settlement component amounts remain in source currency; cash actual conversion retains the existing approved FX-rate provenance. Tax component rate and amount are retained as posted; no current tax rate is retroactively applied to historic invoices.

Outstanding acceptance is not a missing model design: it is successful full CI for the latest commits, comprehensive PostgreSQL coverage of tax-bearing source certification and reversal/replacement, and entity-specific approved policy configuration. This implementation does not determine whether VAT is recoverable/irrecoverable or whether a tax component belongs in a particular statutory budget classification.

## Acceptance tests

- Company isolation and active/effective-dated tax mapping; wrong GL type, missing mapping and foreign-company account rejection.
- AR and AP posted tax components preserve exact tax code, source line, applied rate, mapping version and journal-line identity.
- Partial and full tax-bearing AR receipts; AP payments with VAT and WHT; multiple invoice lines/dimensions; multi-invoice receipts/payments; no implicit pro-rata allocation.
- WHT remittance ties to its originating AP invoice, WHT payable account, bank debit and posted remittance journal; supplier cash allocation excludes WHT.
- Unapproved/expired policy, missing separate classification, changed source population, over-allocation, duplicate allocation, journal mismatch and cross-company references fail closed.
- Approved mappings/policies, invoice tax components, journal lines and settlement allocations cannot be silently edited or deleted.
- Reversal and replacement preserve the exact original source/tax provenance and are independently certified.

## Release rule

The software controls are implemented on the feature branch, but statutory use remains conditional on successful CI, the reporting entity's approved VAT/WHT budget-treatment policies and approved classification dimensions. No tax treatment or Treasury classification is supplied by default. PR #17 remains draft and unmerged pending the complete IPSAS 24 acceptance gates.
