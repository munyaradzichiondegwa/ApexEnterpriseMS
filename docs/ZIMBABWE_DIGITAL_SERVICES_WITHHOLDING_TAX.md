# Zimbabwe Digital Services Withholding Tax (DSWT) — implementation basis

**Status:** Rules evaluator implemented; accounting integration and ZIMRA operational confirmation remain release gates.

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
- ZIMRA Public Notice 05/2026 states the intermediary return is due by the 10th and payment by the 15th of the following month.
- Physical goods are excluded and remain subject to import VAT/customs treatment.
- Local suppliers are excluded.
- Zero-rated/exempt services retain their ordinary treatment.
- Where the service is consumed in Zimbabwe but payment is made outside Zimbabwe, the non-resident supplier accounts directly to ZIMRA where registered.
- Registered non-resident suppliers claim DSWT withheld as a credit in their VAT return.

## Who bears it?

The law's collection mechanics are explicit: the intermediary withholds from the amount being remitted to the foreign supplier and remits it to ZIMRA. The supplier receives a certificate and, if VAT-registered, claims the withholding as a credit against its VAT return. The Ministry describes this as a VAT collection mechanism rather than a new tax on all cardholders.

There is a public interpretive inconsistency: a ZIMRA Head of Technical Services interview reported by The Herald on 16 January 2026 described the recipient as the person who pays the tax. That statement does not alter the statutory withholding workflow or ZIMRA Public Notice 05/2026. The system must therefore record both the statutory withholding mechanics and the commercial contract's agreed gross-up/pass-through treatment. Do not automatically add the tax as a customer surcharge.

## Accounting design decisions

1. Record the gross foreign supplier payment as the underlying payable/cash remittance instruction.
2. Record DSWT withheld as a separate tax-withholding liability/remittance item, with a certificate reference.
3. Do not treat the withholding as an ordinary supplier WHT or an additional IMTT.
4. Do not add a second customer charge by default. Any gross-up or recovery from the customer must be explicitly configured, contractually supported and approved.
5. Supplier VAT registration status, service classification, place of consumption, payment route and prior VAT accounting are evidence-backed inputs.
6. Do not apply both DSWT withholding and recipient self-accounted VAT to the same supply without a documented statutory basis.
7. Do not post or report DST as an IPSAS 24 budget actual until the approved public-sector tax budget treatment and accounting policy mapping are in place.

## Open production gates

- ZIMRA confirmation of the treatment where a supplier is registered but the customer/recipient also claims input VAT.
- Confirm exact due-date configuration against any subsequent ZIMRA notice.
- Approved GL accounts, tax-code mapping, certificate numbering, monthly return and reconciliation workflow.
- Bank/intermediary statement integration and source evidence.
- Tax counsel review of legal incidence versus commercial pass-through/gross-up.
