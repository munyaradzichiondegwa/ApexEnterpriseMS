# Zimbabwe public procurement requirements

**Research baseline:** 28 November 2026  
**Scope:** Map the Public Procurement and Disposal of Public Assets Act [Chapter 22:23] and PRAZ tendering guidance to ApexERP's existing public-sector procurement module. This is a product-control mapping, not a legal opinion.

## Authoritative sources

1. [Public Procurement and Disposal of Public Assets Act [Chapter 22:23] — current ZimLII text](https://zimlii.org/akn/zw/act/2017/5/eng%402020-02-07). Relevant provisions include procurement planning and evaluation committee requirements (sections 17–20), bidding documents (section 40), bid submission and deadline handling (section 43), public bid opening and opening records (section 46), qualification/responsiveness review (section 47), evaluation criteria and currency conversion (section 50), proposed award notification and the 14-day standstill before contract signing (section 55), award publication within one month (section 68), and procurement records (section 69). The Act defines a “day” as a day other than Saturday, Sunday or a public holiday.
2. [PRAZ — Tendering procedures](https://www.praz.org.zw/tendering/). PRAZ describes the sequence from accounting-officer requirement identification and budgeting through specifications, advertising, bid receipt, evaluation, award, contract signature and implementation. It states minimum advertised periods of 20 days for national tenders and 40 days for international tenders, referring to section 19(1) of the Procurement Regulations.
3. [PRAZ — e-GP portal](https://egp.praz.org.zw/) is the relevant external platform to assess for any required electronic procurement workflow. ApexERP's internal tender workflow is not itself proof of PRAZ e-GP integration.

## Existing ApexERP controls

- Tender notices have a server-enforced submission deadline; late bids are retained as rejected and cannot be scored.
- Vendors must be approved, not blacklisted, and hold current tax-clearance and registration validity.
- Scoring uses a single locked weighting matrix across bids; all eligible bids must be scored before award approval.
- Only the highest-ranked eligible bid can be submitted for award approval; an independent workflow approval is required before award.
- Added immutable audit-log events for tender creation, bid submission (including late-rejection status), evaluation start, bid scoring (before/after scores and weights), and award (including selected bid and approval workflow reference).

## Gaps requiring further implementation

| Legal / PRAZ requirement | Current status | Required control |
|---|---|---|
| Minimum advertisement periods | Tender has a deadline, but no national/international procurement scope or working-day calendar validation | Capture procurement scope and compute minimum periods using a Zimbabwe public-holiday calendar; reject shorter periods |
| Approved procurement method and standard bidding documents | Specifications are free text; no method or versioned PRAZ standard-document register | Store method, approval, document version and evidence of the applicable PRAZ standard bidding document |
| Procurement management unit and evaluation committee | Weighted scoring exists, but no committee membership/appointment register and role-composition validation | Record appointed committee, required expertise, adviser/non-voting status, declarations and recusals |
| Public bid opening | Bid submissions and scores exist; no public opening event or signed opening register | Record opening date/time/place, attendees, bidder, price, discounts/alternatives, bid security and essential documents; preserve immutable opening record |
| Evaluation report and criteria | Locked weights and scores exist | Preserve published criteria/version, responsiveness/qualification findings, arithmetic corrections, currency-conversion source/date and signed evaluation report |
| Proposed award notice and standstill | Award approval exists; no bidder notification record or standstill clock | Record proposed-award notice to successful and unsuccessful bidders; prevent contract signature until the statutory 14-day period has elapsed |
| Contract execution and performance security | Award status exists; no executed contract/signature or performance-security register | Separate proposed award, final award and signed contract; capture authorised signatory, signature date and any required performance security |
| Publication of award | No PRAZ award-publication evidence | Record publication channel, notice, date and submission to PRAZ within one month after award |
| Procurement record | Tender and bid entities plus new audit events exist; no complete procurement-file manifest | Retain every notice, bidding document, clarification, bid, opening record, evaluation, challenge, decision, award and contract under a single reference |
| Challenges / review | No section 73 challenge workflow | Record challenge, filing date, affected procurement stage, suspension/decision and outcome; block prohibited progression while a challenge is active |
| PRAZ e-GP | No live PRAZ connector or certification established | Treat credentials, interface, sandbox and certification as external deployment gates; do not claim e-GP compliance from the internal tender module |

## Implementation boundaries

- The statutory 14-day standstill is before contract signing; do not conflate it with the tender's bid-submission period or silently treat an internal award status as proof that a contract may be signed.
- The Act's definition of “day” excludes weekends and public holidays. A 20/40-day validation cannot be safely implemented as simple calendar-day arithmetic without an approved Zimbabwe holiday calendar and the current applicable regulations.
- PRAZ's advertised 20/40-day periods are presented on its guidance page with reference to Procurement Regulations section 19(1); validate the current regulation and any category-specific exceptions before hard enforcement.
- The internal application does not replace procurement-authority approvals, required PRAZ standard documents, statutory publication or the external e-GP platform.
- No live PRAZ connection, notification delivery, legal sign-off or public portal should be marked complete until independently tested.

## Next executable work

1. Add procurement-scope and regulation-version metadata, with a versioned Zimbabwe working-day calendar.
2. Add immutable bid-opening and evaluation-report records.
3. Separate proposed-award notification, standstill expiry, contract execution and award publication.
4. Add section 73 challenge and procurement-file completeness workflows.
5. Build and certify PRAZ e-GP integration only against the Authority's approved interface and credentials.
