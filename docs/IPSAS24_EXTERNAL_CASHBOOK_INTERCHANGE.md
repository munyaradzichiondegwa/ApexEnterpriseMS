# IPSAS 24 external cashbook interchange contract

**Status:** Engineering contract baseline; adapter and runtime integration are not implemented by this document.  
**Applies to:** External government cashbooks and subledgers that provide cashbook transaction populations for IPSAS 24 cash-budget actual reconciliation.

## 1. Purpose and non-goals

This contract defines the minimum evidence required before ApexERP can ingest an external cashbook as a distinct reporting source. It does not treat a bank statement as a cashbook, create accounting entries, authenticate the originating institution, determine budget classification, or replace the existing BankAccount, ReceivablesControl and PayablesControl source adapters.

A cashbook is a source ledger population. A bank statement is independent evidence of bank movements. They must remain separately identifiable and be reconciled, not added together as if they were separate cash flows.

## 2. Source identity and tenant boundary

Every registered source must be bound to:
- ApexERP tenant and company IDs (resolved from the authenticated session; never accepted from import rows);
- an approved reporting budget version and reporting-entity scope;
- a stable external system identifier, source-ledger identifier and source-account/code;
- source currency, accounting basis, timezone and effective period;
- an approved source owner and evidence contact.

The source identifier must be unique within the company and source system. A source cannot be silently reassigned to another company, budget version or reporting entity. All database reads and writes must be company-scoped and tenant-filtered.

## 3. Versioned interchange envelope

The first supported transport should be a versioned UTF-8 CSV package with an immutable original file retained alongside its SHA-256 digest. The envelope must carry:
- schema identifier and version;
- external system and ledger identifiers;
- source account/code and currency;
- reporting period start and exclusive end;
- opening and closing balances;
- debit and credit control totals;
- transaction count;
- export timestamp, exporter identity and source run/reference;
- source timezone and date convention;
- optional prior-period closing-balance reference.

A later API adapter may implement the same logical contract. The transport must not change the accounting or evidence semantics.

## 3A. Current parser implementation boundary

A strict parser implementation now exists as `ExternalCashbookCsvParser` (`apex-ipsas24-cashbook-v1`). It validates UTF-8, a fixed header, row identity, transaction/posting dates, currency consistency, debit/credit exclusivity, reversal references, duplicate IDs/fingerprints, declared row count, debit/credit totals and opening/closing balance. Its upload limit is 20 MB.

The parser is a pure validation component. It does **not** persist the original file or parsed rows, authenticate the source owner, register source systems/accounts, enforce cross-import idempotency, map source records to budget actuals, detect cross-source bank/AR/AP duplicates, or certify completeness. Those require the remaining persisted adapter and review workflow. Parser success alone is not evidence of an authenticated or complete source population.

## 4. Required transaction columns

Each transaction row must include:
- immutable external transaction ID, unique within source ledger and source account;
- transaction date and, where available, posting/effective date;
- source document/reference and description;
- debit and credit amounts (non-negative; exactly one side must be positive);
- currency;
- external account/code and any source dimensions supplied by the ledger;
- reversal indicator and original transaction ID for reversals;
- source line number.

Rows with missing identities, ambiguous signs, unsupported currencies, out-of-period dates, duplicate IDs, or inconsistent reversal references must be rejected or quarantined. No silent row dropping or inferred transaction identity is permitted.

## 5. Import lifecycle and immutable evidence

The intended lifecycle is: received → parsed/validated → pending source authentication → authenticated → reconciled → independently reviewed → certified. Rejected imports remain available as immutable evidence; corrections are new numbered import revisions linked to the rejected/superseded revision.

At receipt, retain original bytes, byte length, filename, media type, schema/parser version, digest, uploader, timestamp and parsed row fingerprints. The digest proves post-receipt integrity only. It does not prove the external system authored the file. Source-origin authentication must be a separate control (for example, independently confirmed source-owner evidence or a future verified system-to-system signature). The uploader cannot authenticate or certify their own batch.

## 6. Control-total validation

Before reconciliation, validate:
- parsed row count equals the declared transaction count;
- sum of debits and credits equals the declared totals;
- opening balance + credits − debits = closing balance, using the source's explicitly declared sign convention;
- currency is consistent across the batch;
- reporting-period coverage has no gaps or overlapping duplicate batches;
- consecutive batch closing/opening balances agree;
- duplicate file digests and duplicate external transaction IDs are detected idempotently.

Any mismatch blocks certification. Rounding tolerances, if required, must be explicit, currency-aware, approved configuration; never use an undocumented tolerance.

## 7. Reconciliation and no-double-counting rules

A cashbook transaction is not automatically a budget actual. Each source record must be:
1. mapped to one or more existing certified budget actual allocations whose amounts reconcile exactly to the source amount; or
2. assigned a reason-coded exclusion and independently reviewed.

Split allocations are permitted only when their sum equals the source transaction. One actual allocation cannot be consumed by multiple source records. Reversals must reference the original external transaction and be represented as linked compensating records; they must not overwrite original evidence.

The adapter must maintain explicit cross-source identity links. In particular:
- A cashbook line that represents a bank movement already represented by a BankAccount/BankTransaction source must be matched as the same economic movement, not counted again.
- Receipts/payments already represented by AR receipt or AP payment source records must be linked to those records and not added as a second actual.
- Transfers between registered cash/bank accounts must be paired and eliminated or classified under an approved policy; they are not revenue or expense merely because they appear in two ledgers.
- Unmatched or ambiguous duplicates block source certification and the IPSAS 24 comparable-actuals gate.

The cashbook source adapter must not create GL journals or budget actuals merely by importing a file. Existing approved accounting and source-actual workflows remain authoritative.

## 8. Mapping and classification

External account and transaction codes must map through company-scoped, effective-dated, independently approved mappings to the existing ApexERP source account, budget actual, Fund Centre, GL account and cost-centre dimensions as applicable. Unknown or expired mappings are exceptions, not candidates for automatic fuzzy mapping. Mapping changes must be versioned and must invalidate any affected source review or statement snapshot.

## 9. API and operational acceptance criteria

A production adapter is not complete until it has:
- tenant/company authorization and object-level access tests;
- immutable batch and row persistence plus schema-version migration;
- file size, row count, encoding and parser resource limits;
- idempotent duplicate handling and safe retry behaviour;
- control-total, opening/closing balance and period-continuity validation;
- record-level allocation/exclusion review and independent certification;
- bank/AR/AP cross-source duplicate and transfer detection;
- live-population revalidation at review, certification and statement generation;
- audit events for receipt, rejection, authentication, mapping, reconciliation, review and certification;
- API/UI workflows, operational error reporting and evidence export;
- PostgreSQL integration tests for valid imports, malformed rows, duplicate imports, cross-tenant access, stale mappings, duplicate economic movements, reversals, partial allocations, and changed source populations;
- a successful migration-drift and full CI run.

## 10. Required source-owner decisions before production activation

The entity/source owner must supply and approve the actual source export layout, stable transaction-ID semantics, date/time conventions, sign convention, currency precision, reversal semantics, control-total definitions, source authentication procedure, export frequency, correction policy, and authoritative source-system owner. Until those decisions and a representative redacted sample file are available, the adapter must remain disabled. A generic CSV parser without these source semantics is not an acceptable production integration.

## 11. Current ApexERP boundary

ApexERP already supports external bank-statement CSV evidence with independent bank confirmation, and its IPSAS 24 source register revalidates in-app bank, AR and AP populations. This cashbook contract is a design baseline only. It must not be represented as a deployed external cashbook integration or used to mark external source ingestion complete.
