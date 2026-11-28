# Zimbabwe public-sector financial reporting requirements

**Research baseline:** 28 November 2026  
**Purpose:** Translate authoritative Zimbabwean and IPSAS material into product requirements without presenting an incomplete ERP workflow as statutory compliance.

## Current regulatory and standards position

1. The Public Accountants and Auditors (Prescription of International Standards) Regulations, 2026 (Statutory Instrument 137 of 2026) replace SI 41 of 2019. IFAC's Zimbabwe jurisdiction profile records that the current statutory schedule includes IPSAS and that the public-sector transition to accrual IPSAS remains phased; it assesses jurisdictional adoption as *partially adopted*, not fully operationalised. See [IFAC — Zimbabwe](https://www.ifac.org/about-ifac/membership/profile/zimbabwe).
2. PAAB identifies the Public Sector Accounting Standards Committee as the body dealing with technical public-sector accounting matters, including recommending IPSAS adoption and local pronouncements. See [PAAB — Public Sector Accounting Standards](https://paab.org.zw/public-sector-accounting-standards/) and [PAAB — ZAPB](https://paab.org.zw/zapb/).
3. IPSASB's [2026 Handbook](https://www.ipsasb.org/publications/2026-handbook-international-public-sector-accounting-standards-board-pronouncements), published 17 August 2026, contains the IPSAS Accounting Standards and first IPSASB SRS Standard published as at 31 January 2026. It is the current international reference set; local applicability and effective dates still need to be checked against Zimbabwe's prescribed framework.
4. Treasury Circular No. 3 of 2025 is an official source for the 2024 transitional financial-statement submission. It requires, at minimum, a Statement of Financial Position, Statement of Financial Performance, Statement of Changes in Net Assets, Statement of Cash Flows, Statement of Comparison of Budget and Actual Amounts, and notes. It also lists a disclosure checklist, asset register, inventory valuation report, creditors listing, debtors listing and lease schedule; entities managing funds must submit separate fund financial statements. This circular is year-specific (2024) and must not be treated as proof that the same template or deadline is the latest 2026 Treasury instruction. See [Treasury Circular No. 3 of 2025 (PDF)](https://zimtreasury.co.zw/wp-content/uploads/2025/03/Treasury-Circular-No-3-of-2025-on-2024-IPSAS-Transitional-Financial-Statements-1.pdf).
5. The Zimbabwe Financial Reporting Manual describes the IPSAS reporting-entity scope and the relationship between IPSAS, the national framework and prescribed government reporting. See [Zimbabwe Financial Reporting Manual (Treasury PDF)](https://zimtreasury.co.zw/wp-content/uploads/2025/03/zfrm-soft-copy-1-1.pdf).
6. Public Finance Management (General) Regulations, 2019 require the government Chart of Accounts to use standard fiscal, budget and performance classifications and to remain compatible with Government Finance Statistics Manual classifications. This is why ApexERP must not invent official economic, functional or programme codes. See [SI 135 of 2019 (PDF)](https://www.veritaszim.net/sites/veritas_d/files/SI%202019%20-%20135%20Public%20Finance%20Management%20%28General%29%20Regulations%2C%202019.pdf).
7. ZIMRA's PAYE guidance states that salaries containing both ZWG and USD components use the USD tax tables and the tax due is apportioned accordingly. The exact conversion/apportionment method must be implemented against approved payroll policy and verified against ZIMRA guidance; ApexERP's current 2026 baseline explicitly does not claim to implement mixed-currency apportionment. See [ZIMRA — PAYE](https://zimra.co.zw/domestic-taxes/individual/pay-as-you-earn-paye).

## ApexERP capability mapping

| Treasury / IPSAS reporting requirement | Existing ApexERP capability | Remaining implementation or acceptance |
|---|---|---|
| Statement of Financial Position | Existing five-statement financial reporting suite | Validate public-sector account presentation and IPSAS-specific disclosures against the current PAAB/Treasury template |
| Statement of Financial Performance | Existing income statement / OCI reporting | Validate IPSAS presentation, revenue/expense classifications and public-sector disclosures |
| Statement of Changes in Net Assets | Existing changes-in-equity reporting | Confirm public-sector net-assets terminology and required reserve/disclosure mapping |
| Statement of Cash Flows | Existing cash-flow reporting | Confirm IPSAS-specific classifications and disclosure requirements |
| Statement of Comparison of Budget and Actual Amounts | Vote/Programme/Sub-Programme expenditure schedule, controlled approved allocations and posted-GL actuals | A full IPSAS 24 comparison needs the approved budget basis and presentation basis, original/final budget, actual-on-comparable-basis and required reconciliation. Current allocations are expense-only; revenue budgets, amendments and comparable-basis reconciliation are not yet a complete statutory statement |
| Notes to financial statements | Financial-statement notes service and tenant-authored notes | Add/validate a Zimbabwe IPSAS disclosure checklist, policy/basis notes, judgements/estimates and material explanatory notes; support IPSAS 33 transition disclosures where applicable |
| Disclosure checklist | No claim of a Treasury-approved complete checklist | Obtain current PAAB/Treasury checklist and version it by reporting year and reporting-entity class |
| Asset register | Fixed-asset module | Validate PPE, intangible, biological-asset and investment-property schedules and opening balances |
| Inventory valuation | Inventory module | Validate valuation report, method, write-downs and tie-out to GL |
| Creditors and debtors listings | AP and AR subledgers | Validate reporting-date ageing, completeness and GL reconciliation |
| Lease schedule | Existing lease accounting capability must be mapped and tested | Confirm completeness, measurement and Treasury schedule format |
| Separate fund financial statements | Fund-centre / fund tagging and reporting | Current Vote hierarchy is not proof of a complete separately balanced fund reporting entity; fund-level statement boundaries and eliminations need explicit acceptance |

## Implementation controls

- Keep public-sector reporting separate from commercial IFRS presentation profiles; do not relabel IFRS reports as IPSAS-compliant without standards mapping.
- Treat the current 2026 IPSASB Handbook as the international reference set, but confirm Zimbabwe prescription, local adaptations and effective dates with PAAB/Treasury before enabling a statutory template.
- Require report metadata: reporting entity, fund, reporting period, presentation currency, accounting basis, budget basis, framework/version and preparer/reviewer approvals.
- Reconcile every statement and supporting schedule to posted GL/subledger sources; expose unresolved differences rather than silently balancing or suppressing them.
- Preserve source provenance and effective dates for all statutory templates and classification dictionaries.
- Do not ship a fabricated government Chart of Accounts. Import only an authorised Treasury/Accountant-General dictionary with version, effective date and source document.
- A 2024 Treasury circular or a green CI run is not evidence of 2026 legal approval or operational IPSAS compliance.

## Immediate next work

1. Obtain the current 2026/2027 Treasury reporting circular, current Zimbabwe Financial Reporting Manual amendments and PAAB-approved disclosure checklist.
2. Implement the complete IPSAS 24 budget comparison only after the approved-budget data model includes original budget, final budget/amendments, actual comparable-basis amounts and the basis reconciliation.
3. Implement the statement/note templates as versioned reporting profiles with effective dates and review controls.
4. Add PostgreSQL acceptance for each statement, fund boundary, budget reconciliation and note completeness before making any compliance claim.
