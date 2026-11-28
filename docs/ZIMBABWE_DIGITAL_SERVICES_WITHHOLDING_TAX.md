# Zimbabwe Digital Services Withholding Tax (DSWT) — implementation basis

**Status (28 November 2026):** The statutory evaluator, company-scoped withholding register, certificate/remittance evidence, AP gross-payment reconciliation, authenticated bank-statement reconciliation, USD monthly return preparation/CSV, and evidence-backed manual TaRMS filing workflow are implemented. The AP payment remains posted at the gross amount (Dr AP / Cr Bank); no duplicate DSWT expense or payable is created because the intermediary's withholding is deducted from the same gross remittance. Direct TaRMS API transmission is not implemented and must remain disabled until ZIMRA supplies an authorised interface specification and credentials. Legal/tax review, actual contract/gross-up evidence and taxpayer-specific TaRMS registration remain operational acceptance gates.

## Separate tax: Electronic Commerce Operators' Tax (DST) — do not confuse with DSWT

Zimbabwe has two distinct digital-economy tax mechanisms which must not be collapsed into one ApexERP tax code:

1. **Electronic Commerce Operators' Tax / Digital Services Tax (DST):** section 12A of the Income Tax Act [Chapter 23:06] taxes qualifying Zimbabwe-source receipts of non-resident satellite broadcasting services and electronic-commerce operators. The rate under section 14(2)(k) is 5% of the relevant gross revenue. Finance Act No. 7 of 2025, section 14, removes the former US$500,000 revenue threshold with effect from 1 January 2026. The statutory taxpayer is the non-resident operator/service provider receiving the relevant Zimbabwe-source revenue; it is not a general 5% surcharge imposed by ApexERP on every Zimbabwean customer invoice. The regime includes representative-taxpayer and quarterly payment/return obligations under section 12A. The exact return timetable and registration procedure should be confirmed against current ZIMRA directions and the taxpayer's profile before filing.
2. **Digital Services Withholding Tax (DSWT):** section 13A of the VAT Act, as substituted by Finance Act No. 7 of 2025, and the Second Schedule impose an intermediary withholding mechanism on qualifying foreign digital-service payments from 1 January 2026. ZIMRA Public Notice 05 of 2026 describes the VAT collection rates as 15.5% for a supplier not registered for Zimbabwe VAT and 3/23 where registered. The intermediary withholds from the amount being remitted, issues the payer a certificate and remits to ZIMRA. A registered non-resident supplier claims the amount withheld as a credit against its VAT liability.

**Who bears DST?** The statutory liability for the 5% Electronic Commerce Operators' Tax rests on the qualifying non-resident operator/service provider, because section 12A says that operator shall pay tax on the relevant revenue. The payer/customer is not made the statutory taxpayer by section 12A. The provider may seek to recover the economic cost through its pricing, but any pass-through is a commercial pricing/contract matter; do not automatically add 5% to a local customer's invoice or treat the customer as the statutory debtor. A local representative taxpayer is an administration/collection mechanism, not an automatic transfer of the underlying tax liability to the Zimbabwean customer.

**Who bears DSWT?** The intermediary has the statutory withholding and remittance duty and is personally liable for failure to withhold/remit under the Second Schedule. The withholding is deducted from the amount remitted to the offshore supplier; the payer receives the withholding certificate. Whether the supplier or customer ultimately absorbs the cost commercially depends on the agreed price and any enforceable gross-up/pass-through clause. Preserve gross payment, withheld amount and net remittance evidence; do not infer an additional customer surcharge.

**Interaction:** DST (5% income-tax charge on the operator's relevant gross Zimbabwe-source revenue) and DSWT (VAT collection withholding on qualifying foreign digital-service payments) arise under different Acts and have different taxpayers, bases and collection mechanics. They are not interchangeable rates and should not be combined as a single 20.5% charge. The sources reviewed do not establish a general credit of DSWT against the 5% DST; the express DSWT credit described by ZIMRA is against the non-resident supplier's VAT return. Any potential interaction, treaty relief, registration status or exceptional overlap must be confirmed with ZIMRA/tax counsel for the supplier and transaction before ApexERP automates it.

Engineering consequence: keep distinct tax types, legal taxpayers, tax bases, rates, tax periods, filing calendars, evidence and GL mappings. The current DSWT module does not implement the 5% Electronic Commerce Operators' Tax return or operator registration workflow. Do not mark DST support complete.

Sources: Finance Act No. 7 of 2025, section 14 (removal of the US$500,000 threshold from Income Tax Act section 12A) and section 44/Second Schedule (VAT Act section 13A/DSWT); ZIMRA Public Notice 05 of 2026; ZIMRA's Income Tax Act [Chapter 23:06] updated to 1 December 2024; Baker Tilly Zimbabwe, “Digital Service Tax and Digital Services Withholding Tax in Zimbabwe” (11 February 2026).

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

## Who bears it? — statutory collection versus economic burden

The statute and ZIMRA Public Notice 05 of 2026 distinguish the party responsible for collection from the supplier's VAT liability and the commercial allocation of the cost:

- **Statutory collection agent:** the Zimbabwe intermediary (bank, mobile-money operator or other prescribed financial institution) must withhold the tax when processing the foreign payment and remit it to the Commissioner-General. The intermediary must issue the prescribed withholding certificate and maintain payment records. Under the Second Schedule, an intermediary that fails to withhold or remit is personally liable for the amount and the statutory additional charge.
- **Foreign supplier's VAT position:** a non-resident digital-services supplier remains responsible for VAT registration/accounting where the statutory conditions and threshold apply. A supplier registered for Zimbabwe VAT claims DSWT withheld as a credit in its VAT return and pays the remaining balance. ZIMRA states that prices charged by registered non-resident suppliers must be VAT-inclusive.
- **Payment mechanics:** the withholding is deducted from the amount being remitted to the foreign supplier. The payer's bank/payment transaction is the gross payment; the supplier receives the net remittance and the intermediary remits the withheld amount to ZIMRA.
- **Economic incidence:** do not assume from the withholding mechanism alone that the Zimbabwe customer must pay an additional surcharge, or that the foreign supplier must absorb the cost in every contract. The final commercial burden depends on the agreed price and any enforceable gross-up/pass-through clause. The application must preserve the gross amount, withheld amount, supplier net remittance and supporting contract/payment evidence; any gross-up or recovery must be separately approved and must never be inferred automatically.

The system must distinguish (a) the intermediary's statutory duty to withhold/remit, (b) the non-resident supplier's VAT registration and credit position, and (c) the contractual allocation of the commercial cost. A certificate proves the withholding event; it does not, by itself, prove that the customer agreed to a separate surcharge.

Sources: Finance Act No. 7 of 2025, section 44 and Second Schedule; ZIMRA Public Notice 05 of 2026 (19 January 2026), particularly sections 4–7; Ministry of Finance clarification of 7 January 2026.
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
- Each legal company now stores its Zimbabwe ZIMRA TIN in its tax profile. Tenant-authorised tax-profile endpoints expose and update that field; DSWT return generation rejects a missing or mismatched TIN rather than trusting a caller-supplied value. The input validator rejects blank values, control characters and values over 40 characters; it does not assume an undocumented TIN character pattern.
- Tax-profile changes are written to the immutable audit log in the same unit of work as the company update. Audit before/after values are masked to the final four characters so the full taxpayer identifier is not copied into the audit payload.
- AP reconciliation links a DSWT record to a posted AP payment only when supplier, currency, gross amount, originating bank account and AP allocation total agree. The link is unique per company/AP payment.
- Bank-backed AP payments create a book-side bank transaction linked to the posted AP journal. The AP payment can retain the external bank transfer reference (separate from ApexERP's internal payment number), which is used for exact statement-row matching. DSWT bank reconciliation then requires an authenticated statement row already matched one-to-one to that exact bank transaction; it verifies gross debit, currency, bank account and AP journal before storing the statement-row link.
- The filing workflow creates a versioned, immutable monthly return snapshot containing the prepared declaration JSON, SHA-256 payload hash and deterministic idempotency key. A unique company/year/period/version constraint prevents duplicate versions; repeating preparation with an unchanged source register returns the same snapshot, while changed source data requires a new version.
- Filing progression is evidence-backed: Prepared → manually Submitted → Acknowledged or Rejected → Payment Confirmed. Submission reference/evidence, TaRMS acknowledgement reference/evidence, and exact USD payment reference/amount/evidence are retained and audited. Payment confirmation is refused unless the acknowledged return amount and currency reconcile exactly.
- The workflow records manual TaRMS activity only. It does not send network requests, simulate acknowledgements, or claim a return was filed. Direct TaRMS automation remains disabled until ZIMRA provides an authorised interface specification, payload schema, authentication method and sandbox/production credentials.
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
- Confirm the tax agent's TaRMS tax-type registration, effective date and generated return periods; obtain ZIMRA's authoritative electronic interface specification, payload schema, authentication method and sandbox/production credentials before enabling direct TaRMS transmission.
- Confirm the approved TaRMS filing evidence and acknowledgement procedure, reconcile the generated return against the live TaRMS form, and approve the tax certificate numbering policy.
- Any exceptional gross-up or separate-debit accounting policy and related control-account mapping.
- Tax counsel review of legal incidence versus commercial pass-through/gross-up.
