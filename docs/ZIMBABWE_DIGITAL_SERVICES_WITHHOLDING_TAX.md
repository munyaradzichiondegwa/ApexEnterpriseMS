# Zimbabwe Digital Services Withholding Tax (DSWT) — implementation basis

**Status:** Rules evaluator and company-scoped withholding evidence register implemented. GL posting, AP cash-settlement integration, return export/submission and ZIMRA confirmation remain release gates.

## Authoritative sources reviewed

1. Finance Act No. 7 of 2025, section 44 substitutes section 13A of the VAT Act and section 49 inserts the Second Schedule (Digital Services Withholding Tax).
2. ZIMRA Public Notice 05 of 2026, issued 19 January 2026.
3. Ministry of Finance press statement dated 7 January 2026 clarifying scope and double-taxation intent.

## Statutory and administrative treatment

- Effective date: 1 January 2026.
- Qualifying supply: electronic service supplied by a non-resident and consumed/utilised in Zimbabwe.
- A Zimbabwe intermediary withholds from the amount to be remitted abroad.
- Supplier not VAT-registered in Zimbabwe: 15.5% of the payment amount.
- Supplier VAT-registered in Zimbabwe: tax fraction 3/23.
- Intermediary issues a withholding certificate and maintains payment records.
- The ordinary monthly filing/payment cycle remains: return by the 10th and payment by the 15th of the following month, subject to the applicable statutory calendar and ZIMRA notices.
- Operational update: Public Notices 23/2026 and 34/2026 said DSWT returns had to be filed manually while TaRMS was not configured. Public Notice 50/2026 (4 September 2026) superseded that interim process: the DSWT return is now available in TaRMS. The taxpayer must first register the new tax type in TaRMS (Taxpayer Information → Taxpayer Profile → Tax Type → New Tax Type); pending returns are then generated from the registration effective date. Earlier manual returns should also be completed in TaRMS to clear pending-return flags.
- Physical goods are excluded and remain subject to import VAT/customs treatment.
- Local suppliers are excluded.
- Zero-rated/exempt services retain their ordinary treatment.
- Where the service is consumed in Zimbabwe but payment is made outside Zimbabwe, the non-resident supplier accounts directly to ZIMRA where registered.
- Registered non-resident suppliers claim DSWT withheld as a credit in their VAT return.

## Keep DSWT separate from other non-resident taxes

ZIMRA Public Notice 24 of 2026 (9 April 2026) separately reminds local taxpayers that foreign payments for royalties, dividends, management fees, professional fees and imported services can trigger taxes under the Income Tax Act and VAT Act. These obligations must be classified and assessed independently; the DSWT register must not be used as a substitute for ordinary non-resident withholding tax or imported-services VAT accounting.

## Who bears it?

The law's collection mechanics are explicit: the intermediary withholds from the amount being remitted to the foreign supplier and remits it to ZIMRA. The intermediary must issue a withholding certificate containing the payer, supplier, payment amount and tax withheld; retain that certificate as transaction evidence. A VAT-registered non-resident supplier may claim the withheld amount as a credit through its VAT return under ZIMRA's procedure. The Ministry describes this as a VAT collection mechanism rather than a new tax on all cardholders.

There is a public interpretive inconsistency: a ZIMRA Head of Technical Services interview reported by The Herald on 16 January 2026 described the recipient as the person who pays the tax. That statement does not alter the statutory withholding workflow or ZIMRA Public Notice 05/2026. The system must therefore record both the statutory withholding mechanics and the commercial contract's agreed gross-up/pass-through treatment. Do not automatically add the tax as a customer surcharge.

## AP and bank settlement accounting

Where a Zimbabwe intermediary deducts DSWT from a gross foreign-service payment, the AP settlement must be represented at the gross amount debited from the payer's bank account. The supplier receives the net remittance; the intermediary withholds and remits the difference. For a USD 100 gross payment at 15.5%, the AP/bank settlement is USD 100, the supplier's net remittance is USD 84.50 and DSWT withheld is USD 15.50.

ApexERP must not:
- reduce the AP settlement to the supplier's net amount while also clearing the full gross invoice;
- post the same DSWT amount as a second expense or a second payable when it is already included in the gross bank debit; or
- infer a withholding from a payment amount alone without intermediary evidence.

The AP reconciliation control therefore requires a posted AP payment for the same supplier, currency and gross amount, and requires the payment to retain its originating company bank account. A unique company-scoped link prevents one AP payment from being reconciled to multiple DSWT records. The existing AP payment journal remains Dr AP / Cr Bank for the gross settlement; the DSWT register separately preserves the net supplier remittance and withholding evidence.

This is not a blanket accounting conclusion for every contractual arrangement. If a contract requires a gross-up or the bank debits a different amount, record the actual bank evidence and obtain tax/accounting approval before configuring a different journal treatment.

## ApexERP implementation status

- The pure evaluator applies the published 15.5% rate for suppliers not registered for VAT in Zimbabwe and the 3/23 tax fraction for registered suppliers.
- A company-scoped DSWT register stores intermediary payment reference, supplier/intermediary identity, country, currency, gross amount, rate, withheld amount, net supplier remittance, service classification, VAT status, tax period, certificate, remittance date and evidence reference.
- The register has a unique `(CompanyId, PaymentReference)` key and uses the existing fail-closed company query filter and PostgreSQL RLS pattern.
- The register service validates the tax decision and evidence, supports certificate issuance and ZIMRA remittance status, and produces a monthly company-scoped register.
- A monthly return preparation summary and CSV export map the USD register to the fields shown on ZIMRA's published DSWT return (TIN, taxpayer, tax year/period, declaration currency, taxable income and calculated tax). Non-USD periods are blocked until transaction-level approved USD conversion evidence is available.
- Each legal company now stores its Zimbabwe ZIMRA TIN in its tax profile. Tenant-authorised tax-profile endpoints expose and update that field; DSWT return generation rejects a missing or mismatched TIN rather than trusting a caller-supplied value. The input validator rejects blank values, unsupported characters and values over 40 characters.
- Tax-profile changes are written to the immutable audit log in the same unit of work as the company update. Audit before/after values are masked to the final four characters so the full taxpayer identifier is not copied into the audit payload.
- AP reconciliation links a DSWT record to a posted AP payment only when supplier, currency, gross amount, originating bank account and AP allocation total agree. The link is unique per company/AP payment.
- Bank-backed AP payments create a book-side bank transaction linked to the posted AP journal. The AP payment can retain the external bank transfer reference (separate from ApexERP's internal payment number), which is used for exact statement-row matching. DSWT bank reconciliation then requires an authenticated statement row already matched one-to-one to that exact bank transaction; it verifies gross debit, currency, bank account and AP journal before storing the statement-row link.
- This register is not yet a GL posting, an AP payment automation, a TaRMS filing integration or a ZIMRA-approved return. No journal is posted by this service.

## Accounting design decisions

1. Record the gross foreign supplier payment as the AP settlement and gross bank debit when the intermediary deducts DSWT from that gross payment.
2. Preserve the supplier-net and DSWT-withheld split in the DSWT evidence register, with the intermediary certificate and bank statement link. Do not create a second expense or payable for the same withheld amount when it is already included in the gross bank debit.
3. Do not treat DSWT as ordinary supplier WHT or additional IMTT. A separate journal is only appropriate where bank evidence shows a separate debit or an approved contract/accounting policy requires a different gross-up treatment.
4. Do not add a second customer charge by default. Any gross-up or recovery from the customer must be explicitly configured, contractually supported and approved.
5. Supplier VAT registration status, service classification, place of consumption, payment route and prior VAT accounting are evidence-backed inputs.
6. Do not apply both DSWT withholding and recipient self-accounted VAT to the same supply without a documented statutory basis.
7. Do not post or report DST as an IPSAS 24 budget actual until the approved public-sector tax budget treatment and accounting policy mapping are in place.

## Open production gates

- ZIMRA confirmation of the treatment where a supplier is registered but the customer/recipient also claims input VAT.
- Confirm the tax agent's TaRMS tax-type registration, effective date and generated return periods; implement a validated TaRMS-ready return export against the current form schema.
- Approved tax certificate numbering policy and validated monthly TaRMS return reconciliation/export.
- Any exceptional gross-up or separate-debit accounting policy and related control-account mapping.
- Tax counsel review of legal incidence versus commercial pass-through/gross-up.
