# IPSAS 24 AR/AP tax allocation design

**Status:** design prerequisite; implementation remains fail-closed.  
**Scope:** cash-basis IPSAS 24 actuals sourced from posted AR receipts and AP payments.

## Why tax-bearing invoices are not yet eligible

The existing invoice models retain invoice totals, VAT/WHT amounts and tax-code IDs. Invoice lines retain their VAT amounts, operational GL account and cost centre. However:

- `TaxCode` stores code, name, rate, type and effective dates; it does not identify the posted input/output VAT or WHT control GL account.
- AR and AP posting receive VAT/WHT control-account IDs as method arguments. Those account IDs are not persisted as an invoice-level tax mapping.
- AR invoice posting credits VAT output without Fund Centre or cost-centre dimensions. AP posting debits VAT input and credits WHT payable without those budget dimensions.
- AP `Outstanding` is `Total - WhtAmount`; the supplier cash settlement therefore differs from invoice gross. WHT payable is a separate liability, not cash paid to the supplier.
- The current IPSAS adapter validates the tax-free case and rejects VAT-bearing AR invoices and VAT/WHT-bearing AP invoices.

The GL journal is authoritative evidence of what was posted, but identifying a journal line only by matching an amount is not a safe substitute for a durable tax-account mapping: equal-valued tax and other journal legs can occur.

## Implemented foundation — 28 November 2026

The first code increment now persists the exact tax-control GL account IDs used at posting:

- AR invoice: `VatOutputAccountId`.
- AP invoice: `VatInputAccountId` and `WhtPayableAccountId`.

AR posting requires the VAT output account to be an active, postable liability account in the invoice company. AP posting requires VAT input to be an active, postable asset account and WHT payable to be an active, postable liability account in that same company. The values are saved with the invoice and therefore identify the actual accounts used by the posted journal rather than relying on a later account-name or code lookup.

Migration `20261128120000_PersistArApTaxPostingAccounts` adds nullable columns so existing historical invoices remain intact; historical account IDs are not guessed or backfilled. PostgreSQL integration regressions cover valid AR/AP persistence and rejection of wrong-type or cross-company AR VAT accounts.

AR and AP invoice posting now also validates VAT tax-code provenance before creating the journal: every VAT-bearing line must reference a company-owned, active VAT tax code effective on the invoice date; its VAT amount must agree with the configured rate within one cent; and line subtotals/VAT must reconcile to invoice headers. This is a posting-integrity control, not a replacement for persisted effective-dated tax-account mapping or tax-component journal-line identity.

This remains a traceability foundation only. It does not yet provide persisted effective-dated tax-account mappings, per-tax-component journal-line identity, tax settlement allocation, approved budget-treatment policy, or tax-bearing IPSAS 24 actual capture. The adapter must remain fail-closed for VAT/WHT invoices.

## Required accounting model

Before enabling tax-bearing cash actuals, implement all of the following:

1. **Persisted, company-scoped tax-account mapping.** Store effective-dated mappings for VAT input, VAT output and WHT payable (and any additional tax types actually supported). Validate account ownership, active status, expected GL account type and currency policy. Preserve the exact mapping version used when the invoice was posted; never infer it from account names or seeded account codes.
2. **Tax component traceability.** For every posted invoice, preserve tax code, tax type, taxable base, rate, tax amount, tax GL account and source journal-line identity. Reconcile each tax component to the posted journal and invoice header. Reject missing, duplicate, stale or non-balancing mappings.
3. **Settlement allocation.** For every receipt/payment allocation, identify how much of the cash settlement relates to each invoice line and each tax component. Support partial settlements and multi-invoice receipts/payments only where the allocation evidence is explicit and sums exactly to the posted bank movement. Do not prorate tax across budget dimensions unless the entity's approved accounting policy explicitly authorises that method.
4. **Budget-treatment policy.** Add an independently approved, effective-dated policy for recoverable VAT, irrecoverable VAT and withholding taxes. It must state whether each component is included in, excluded from, or separately classified in cash-basis budget actuals, with an approved budget classification and Fund Centre treatment where applicable. No default policy may be inferred from tax type alone.
5. **Independent review and immutable provenance.** Capture policy and mapping versions, invoice and settlement source IDs, journal IDs/line IDs, preparer, reviewer, timestamps and evidence references. Changes to the live source population, journal, mapping or policy must invalidate certification until re-reviewed.
6. **Compensation and completeness.** Tax components must participate in the existing source-register completeness, duplicate-allocation, reversal and replacement controls. A reversal must reverse the exact original tax and budget allocations; a replacement must use current valid evidence and the applicable approved policy.

## Required reconciliation equations

For an AR invoice, reconcile invoice gross to the posted receivable and revenue/tax legs. For each receipt, reconcile the bank debit/credit to the sum of invoice settlement allocations. Budget actual must then follow the approved policy for revenue and VAT; gross cash received must not automatically be treated as revenue.

For an AP invoice, reconcile expense/asset lines, VAT input, WHT payable and AP control to the posted journal. Reconcile the supplier payable and WHT liability separately. For each payment, reconcile the bank movement to the supplier cash payment; WHT withheld is not part of the cash paid to the supplier. Budget actual must follow the approved policy for expense and recoverable/irrecoverable VAT; neither gross invoice total nor net supplier payment is automatically the correct budget amount.

All equations must be evaluated in source currency and functional currency with the captured exchange-rate provenance and documented rounding tolerance.

## Acceptance tests

- AR standard-rated, zero-rated and exempt invoices; multiple tax codes; equal-valued journal legs; partial and full receipts; one receipt allocated to multiple invoices; multi-dimensional invoice lines.
- AP VAT input and WHT together; WHT-only and VAT-only cases; partial and full payments; multi-invoice payment runs; WHT certificate and tax-liability settlement; multi-dimensional invoice lines.
- Missing/foreign/inactive/effective-date-invalid tax mappings; policy not approved or outside effective dates; journal mismatch; tax amount/rate mismatch; stale mapping/policy; ambiguous journal line; duplicate settlement allocation; unallocated bank movement.
- Independent certification, changed source population invalidation, reversal and replacement preserve the exact original tax-component provenance.
- PostgreSQL integration tests prove company isolation, transaction atomicity, database constraints and live-population revalidation.

## Release rule

Until the mapping, policy, allocation and tests above exist, the IPSAS 24 adapter must continue to reject VAT/WHT-bearing AR/AP invoices. Do not mark this work complete based only on a passing tax-free test suite. The reporting entity must approve the tax treatment; software implementation cannot make that accounting-policy decision.
