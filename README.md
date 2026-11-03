# ApexEnterpriseMS Enterprise Resource Planning System

> **ApexERP is a multi-tenant, IFRS-compliant ERP platform for businesses, NGOs, and public sector institutions across Zimbabwe and the region.**

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Next.js](https://img.shields.io/badge/Next.js-15.5.28-black?style=for-the-badge&logo=next.js&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=for-the-badge&logo=postgresql&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-5-3178C6?style=for-the-badge&logo=typescript&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

[![Build](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml/badge.svg)](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml)

---

## Overview

ApexERP is a multi-tenant Enterprise Resource Planning platform built for businesses, NGOs, and public sector institutions operating in Zimbabwe and the wider region. It runs real, working business logic — not UI mockups — across all 16 core ERP modules on a hybrid **ASP.NET Core 8 Web API + Next.js 15.5.28** architecture, backed by **PostgreSQL 16**.

At its core, ApexERP ships a complete **five-statement IFRS reporting suite** computed live from posted ledger data, alongside native **Zimbabwe regulatory compliance** — PAYE, NSSA, AIDS Levy, VAT, Withholding Tax, and genuine ZIMRA FDMS fiscal signing.

---

## Table of Contents

- [Features](#features)
- [Financial Reporting](#financial-reporting)
- [Tech Stack](#tech-stack)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [Configuration and Secrets](#configuration-and-secrets)
- [Security and Multi-Tenancy](#security-and-multi-tenancy)
- [Roles and Permissions](#roles-and-permissions)
- [Accounting Controls](#accounting-controls)
- [Testing](#testing)
- [Regulatory Compliance](#regulatory-compliance)
- [External Integrations](#external-integrations)
- [Development Status](#development-status)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)
- [Author](#author)

---

## Features

### Financial Management & General Ledger
- Chart of accounts, cost centres, and accounting periods
- Shared double-entry posting engine (`GlPostingService`) used by every module — validates debit/credit balance, enforces open-period rules, auto-numbers entries, maintains running balances, supports reversal
- Inter-company mirroring — auto-creates a mirror AP invoice in a linked group company, with periodic netting and settlement
- Multi-entity consolidation — aggregates subsidiary trial balances, eliminates inter-company balances, calculates non-controlling interest

### Accounts Receivable & Payable
- Customer and supplier invoicing with per-line VAT codes
- Receipting and payments with multi-invoice allocation
- Withholding tax calculation on non-compliant suppliers
- AR and AP ageing analysis (current / 30 / 60 / 90+)
- Fund-centre and grant tagging with real-time budget enforcement

### Cash & Bank
- Bank account management
- Statement import with auto-matching reconciliation (date ± 3 days, exact amount) and manual reconciliation

### Inventory & Supply Chain
- Real perpetual **FIFO** and **AVCO** costing
- Purchase orders, goods receipt, and three-way matching
- Vendor management and low-stock alerts

### Manufacturing
- Bill of Materials with scrap factors
- Standard-cost roll-up
- Work order release and completion at actual cost

### Payroll & HR
- Employee and department management
- ZIMRA PAYE from database-stored tax bands, NSSA (3% employee / 3% employer), AIDS Levy (3% of PAYE)
- Gross-to-net payroll runs with GL posting and payslip retrieval

### CRM & Sales
- Lead pipeline and opportunity tracking
- Sales orders that convert directly into draft AR invoices

### Project Management
- Grant-linkable projects, tasks, and timesheets with approval
- Budget-vs-actual tracking

### Fixed Assets
- Asset register with barcode/RFID tagging
- Straight-line and reducing-balance depreciation, run as a monthly batch
- IAS 16 revaluation to fair value, with disposal gain/loss

### Grants, Donors & NGO Management
- Grant master with donor, award, and restriction tracking
- GL-account-level budgets with real-time over-budget blocking
- Indirect cost recovery and sub-award disbursement tracking
- Beneficiary/distribution registers and donor financial reporting

### Public Sector & Government
- Vote / Program / Sub-Program fund hierarchy
- Commitment (encumbrance) accounting as a budgetary-control ledger, separate from the GAAP GL
- Real-time budget utilisation tracking with over-90%-used alerts and year-end carry-forward
- PPDPA e-tendering with server-side bid timestamping, late-bid rejection, and weighted evaluation scoring
- Barcode/RFID asset verification against the fixed asset register

### Platform Administration
- JWT authentication and role-based access control (RBAC)
- Multi-tenant company, user, and role administration
- Configurable multi-tier approval chain engine (up to 5 sequential tiers with per-tier SLA tracking), reusable across purchase orders, journal entries, payments, and more
- Immutable, append-only audit log across all modules

---

## Financial Reporting

Every statement below is computed live from posted ledger data — not templated — and all five are available individually or in a single call via `GET /api/v1/reports/full-statement-pack`.

1. **Statement of Financial Position** (Balance Sheet)
2. **Statement of Profit or Loss and Other Comprehensive Income** — including a real OCI section (asset revaluation surplus, foreign operation translation reserve)
3. **Statement of Changes in Equity** — reconciles opening balance, profit transfer, OCI movement, dividends, and share capital movements to the Balance Sheet
4. **Statement of Cash Flows** (indirect method) — working-capital movements derived generically from `GlAccount.CashFlowCategory` tagging rather than hardcoded account codes
5. **Notes to the Financial Statements** — accounting policies, PPE movement schedule, inventory breakdown, AR/AP ageing, share capital movements, related-party disclosures, and IFRS 8 segment reporting

---

## Tech Stack

**Backend**
- ASP.NET Core 8, C#
- Entity Framework Core (versioned migrations)
- PostgreSQL 16
- JWT authentication, BCrypt password hashing
- Docker

**Frontend**
- Next.js 15.5.28 (App Router)
- React, TypeScript
- Tailwind CSS
- Axios

**Infrastructure**
- Docker Compose (Postgres + API + Web in one command)
- GitHub Actions CI (restore, build, migration check, frontend type-check and build)

---

## Architecture

```text
ApexERP/
├── backend/                         ASP.NET Core 8 solution (C#)
│   ├── src/
│   │   ├── ApexERP.Domain/          Entities, enums — no dependencies
│   │   ├── ApexERP.Application/     Services (business logic), Security (JWT/BCrypt, permissions, config checks)
│   │   ├── ApexERP.Infrastructure/  EF Core DbContext, tenant query filters, migrations
│   │   └── ApexERP.API/             Controllers, authorization, request transaction filter, Program.cs, Dockerfile
│   ├── tests/ApexERP.Tests/         xUnit unit tests + PostgreSQL integration tests
│   └── database/
│       ├── schema.sql               Reference schema for manual bootstrap without the .NET SDK
│       └── seed.sql                 Zimbabwe chart of accounts, VAT/WHT codes, PAYE bands
├── frontend/                        Next.js 15.5.28 (App Router, TypeScript, Tailwind)
│   ├── app/                         One route per module (dashboard, gl, ar, ap, inventory, group, public-sector, grants, reports, ...)
│   ├── components/AppShell.tsx      Responsive, permission-aware sidebar (desktop) / hamburger drawer (mobile)
│   ├── lib/                         API client, auth context, session storage, permissions, config
│   └── tests/                       Vitest unit tests
├── docs/AUDIT_REMEDIATION.md        What the security/accounting audit found and what was done about it
├── scripts/                         Migration generators, smoke test
└── docker-compose.yml               Postgres + API + Web, one command to run everything
```

**Multi-tenancy** — every business table carries a `company_id`. EF Core global query filters, set per-request by tenant middleware from the JWT, scope every query automatically — the application-level equivalent of PostgreSQL row-level security. The filters **fail closed**: with no tenant context a query returns nothing. Cross-tenant services (`InterCompanyService`, `ConsolidationService`, sign-in) explicitly opt out with `IgnoreQueryFilters()`, since their job is to read and write across companies — each such query still filters explicitly by the relevant `companyId`, and group features are limited to the caller's own group.

**GL posting pattern** — no controller writes directly to `journal_entries` or `journal_lines`. Every module posts through `GlPostingService`, the single source of truth for balance validation, period-open checks, entry numbering, and account balance maintenance. Posted lines can carry a `FundCentreId` and/or `GrantId`, so one posting engine feeds the GAAP books, public sector budget-vs-actual, and donor reporting simultaneously. Each mutating API request runs in one database transaction, and the posting engine takes transaction-scoped locks for numbering and period changes (see [Accounting Controls](#accounting-controls)).

**Frontend polish** — a responsive `AppShell` (persistent sidebar on desktop, hamburger-triggered drawer on mobile), proper `viewport` meta and theme colour, mobile-first Tailwind layouts, a PWA manifest with install icons, and horizontally-scrolling data tables on narrow screens.

---

## Getting Started

### Prerequisites
- Docker Desktop
- .NET 8 SDK (for local backend dev and migration generation)
- Node.js 20+
- PostgreSQL 16 (only if running outside Docker)

### Clone the repository

```bash
git clone https://github.com/munyaradzichiondegwa/ApexEnterpriseMS.git
cd ApexEnterpriseMS
```

### Quick start (Docker)

```bash
docker compose up --build
```

The EF Core migrations are committed to the repository and applied automatically when the API starts, so there is nothing to generate for a first run. This compose file is a **local development stack** (it runs the API in the `Development` environment); see [Configuration and Secrets](#configuration-and-secrets) before deploying anywhere real.

| Service | URL |
|---|---|
| Web app | http://localhost:3000 |
| API | http://localhost:8080 |
| Swagger | http://localhost:8080/swagger |
| PostgreSQL | 127.0.0.1:5432 (`apexerp` / `apexerp_dev_password` — development only; reachable from this machine only) |

**First run**
1. Open [localhost:3000/register](http://localhost:3000/register) and create your company and admin user (password: at least 10 characters). This seeds a Company, an Administrator role holding every permission, and your user.
2. Run `backend/database/seed.sql` against the new database (fill in your company UUID from the `companies` table) to load a starter Zimbabwe chart of accounts, VAT/WHT tax codes, and PAYE bands.
   > ⚠️ The PAYE bands in `seed.sql` are illustrative placeholders modelled on ZIMRA's tax-table structure. Verify and replace them with the exact bands from the current ZIMRA Public Notice before relying on them for real payroll.
3. Log in and start creating customers, suppliers, and inventory items, and posting transactions. Manual journals follow the [approval workflow](#accounting-controls); in Development one user may prepare, approve and post their own journal so a single-person setup can try it out.

### Manual setup

**Backend** — `dotnet run` uses the committed launch profile (`Properties/launchSettings.json`), which selects the `Development` environment, so the development-only settings in `appsettings.Development.json` apply. You need a local PostgreSQL 16 matching its connection string.
```bash
cd backend
dotnet restore
dotnet build
dotnet run --project src/ApexERP.API
```

**Frontend**
```bash
cd frontend
npm ci
npm run dev
```

### Database migrations

ApexERP uses real, versioned EF Core migrations (never `EnsureCreated()` or hand-run SQL). The migrations are committed under `backend/src/ApexERP.Infrastructure/Data/Migrations`, and `Program.cs` applies any pending ones on startup, so every environment gets the same schema.

When you change an entity class under `ApexERP.Domain/Entities` (or the model configuration in `ApexErpDbContext`), generate a new migration and commit it:

```bash
.\scripts\generate-migrations.ps1 -MigrationName AddCustomerLoyaltyPoints     # Windows PowerShell
./scripts/generate-migrations.sh AddCustomerLoyaltyPoints                      # macOS/Linux
```

`AuditHardeningPhase1And2` adds the exception queue, the unique/check constraints that protect the ledger, and is documented in [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md). Before applying it to a database that already holds data, run the four pre-check queries in that migration's header comment; any rows they return must be fixed first.

---

## Configuration and Secrets

The API checks its security-critical configuration when it starts and **refuses to start outside `Development`** if anything is missing or unsafe. There are no fallback secrets in the code.

| Setting | Environment variable | Required | Notes |
|---|---|---|---|
| JWT signing key | `Jwt__Secret` | Always | At least 32 characters. Outside Development it is also rejected if it looks like a placeholder or has too little variety. Generate one with `openssl rand -base64 48`. |
| Token issuer | `Jwt__Issuer` | Always | `ApexERP` by default (`appsettings.json`). |
| Database | `ConnectionStrings__Default` | Always | Production must not use the development password. |
| Web app origin(s) | `Frontend__Origin` | Outside Development | Comma-separated real `http(s)` origins, never `localhost`. |
| Journal self-approval | `Controls__JournalApproval__AllowSelfApproval` | Optional | `false` by default (segregation of duties). `true` only for single-user development. |
| Swagger UI | `Swagger__Enabled` | Optional | On in Development; elsewhere only when set to `true`. |

Development-only values live in `appsettings.Development.json`. In production, supply the secrets through environment variables or your platform's secret store, and set `ASPNETCORE_ENVIRONMENT=Production`. `.env.example` lists what `docker compose` reads.

**Web app (Vercel or any host).** `NEXT_PUBLIC_API_URL` is compiled into the browser bundle at **build time**, so it must be set in the hosting project's environment variables *before* the build. A build on Vercel without it fails with a clear message, and a production build without it shows a configuration screen instead of silently calling `localhost`. After deploying, set the API's `Frontend__Origin` to the web app's URL, otherwise browsers cannot call the API.

---

## Security and Multi-Tenancy

- JWT authentication. Access tokens last 15 minutes; refresh tokens use a different audience, so they can never be used as access tokens
- BCrypt password hashing, with a minimum password policy (10+ characters)
- **Deny-by-default RBAC** — every endpoint requires a permission (see [Roles and Permissions](#roles-and-permissions)); a test fails if any endpoint is left open
- **Tenant isolation fails closed** — EF Core query filters return nothing when there is no tenant context, and requests without a valid `company_id` are refused. Code that must work across tenants (sign-in, inter-company posting, consolidation) opts in explicitly
- Group, consolidation and inter-company features are scoped to the caller's *own* group; a company can only be attached under another company when both sides have agreed
- Immutable, append-only audit log, and a ledger that cannot be edited once posted (corrections are made by reversal)
- One database transaction per mutating request, so multi-step postings are all-or-nothing
- Configurable multi-tier approval chain engine (up to 5 sequential tiers); approvers must hold the tier's role and cannot approve their own requests

---

## Roles and Permissions

A role's permissions are a JSON array of keys, for example `["gl.journals.create","reports.financial.read"]`. The Administrator role created at registration holds `["*"]`. A key ending in `.*` grants a whole branch (`"gl.*"`).

- **Module defaults** — any endpoint without a more specific key needs `{module}.read` (GET) or `{module}.write` (anything else), for example `payroll.read`, `ar.write`, `fixed-assets.write`.
- **Sensitive actions have their own keys**, including `gl.accounts.manage`, `gl.journals.create`, `gl.journals.approve`, `gl.journals.post`, `gl.journals.reverse`, `accounting.period.close`, `accounting.period.reopen`, `reports.financial.read`, `ar.invoices.post`, `ap.invoices.post`, `ap.payments.create`, `payroll.runs.post`, `payroll.config.manage`, `fixed-assets.depreciate`, `fixed-assets.dispose`, `fixed-assets.revalue`, `equity.dividends.manage`, `group.structure.manage`, `group.consolidation.read`, `group.netting.execute`, `exceptions.manage`, `workflow.decide`, `admin.users.manage`, `admin.roles.manage` and `admin.company.manage`. `GET /api/v1/admin/permissions` returns the full list.
- **No privilege escalation** — a user can only grant a role or assign a user a role containing permissions they hold themselves.
- Roles are created with `POST /api/v1/admin/roles` (`{ "name": "Accountant", "permissionsJson": "[\"gl.*\",\"reports.financial.read\"]" }`) and users with `POST /api/v1/admin/users`.
- The navigation hides modules a user cannot use, but the **API enforces every permission** regardless of what the interface shows.

> If you created custom roles before this change, their old keys (for example `gl.journal.post`) no longer match the catalogue. Edit them to use the keys above; the Administrator role is unaffected.

---

## Accounting Controls

**Manual journals** follow a controlled lifecycle: `Draft → Submitted → Approved → Posted → Reversed`, with `Rejected` and `Cancelled` as exits.

- Creating a journal saves a **draft**; nothing reaches the ledger or any account balance until it is posted.
- **Segregation of duties** — the person who prepared a journal cannot approve it (unless `AllowSelfApproval` is enabled, which is only the Development default).
- Posting re-validates the journal from its stored lines, takes the final `JE-yyyyMM-nnnnn` number, and updates balances in one transaction. Repeating a request is harmless.
- System postings (AR, AP, payroll, inventory…) post immediately through the same engine.

**Posting rules** — every journal must balance in base currency; lines are single-sided and non-negative; the exchange rate must be positive and the currency a valid 3-letter code; the period must be open; deactivated accounts accept no new postings (reversals excepted). The database enforces the amount and rate rules again as check constraints.

**Reversal** — a posted journal can be reversed once, atomically, and the reversal keeps the original's cost-centre, fund-centre and grant tags so dimension reports net to zero. A unique index makes a second reversal impossible even under concurrent requests.

**Periods** — closing is refused while the period still holds unposted journals, is serialised against posting, and can be undone only by an audited reopen with a reason.

**Numbering** — journal and document numbers are allocated under a transaction-scoped lock, so concurrent requests cannot collide.

**Reports** — ledger reports include posted *and* reversed journals (a reversal offsets its original), include deactivated accounts with history, treat "as of" dates as the whole day, and show prior-period and current-year profit separately on the balance sheet so it balances whether or not year-end closing entries were posted. `GET /api/v1/reports/integrity-check` returns the control totals (trial-balance difference, accounting-equation difference, unbalanced journals, journals awaiting approval); every difference should be zero.

**Exceptions** — work that cannot complete automatically, such as an inter-company mirror that fails after the local invoice posted, becomes a visible item in **Exceptions** (retry, resolve or dismiss with a note) instead of being swallowed.

**Audit trail** — each journal's history (who did what, when) is available at `GET /api/v1/gl/journal-entries/{id}/history`.

---

## Testing

```bash
# Backend unit tests (no database needed)
dotnet test backend/tests/ApexERP.Tests

# Backend integration tests against a real PostgreSQL (the user must be allowed to create databases)
APEX_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet test backend/tests/ApexERP.Tests

# Frontend
cd frontend && npm test && npm run typecheck

# End-to-end smoke test against a running stack (Development API)
./scripts/smoke-test.sh
```

The integration tests create a throw-away database per test, apply the real migrations to it, and cover tenant isolation, concurrent numbering, atomic and single reversal, reports, the approval workflow, period locking and ledger immutability. They are skipped when `APEX_TEST_DB` is not set. GitHub Actions runs the unit tests, the integration tests (against a PostgreSQL service) and the frontend checks on every push.

---

## Regulatory Compliance

**IFRS**
- IAS 1 — full five-statement presentation
- IAS 16 — property, plant & equipment revaluation to fair value, with the surplus recognised in OCI/equity

**Zimbabwe tax and statutory compliance**
- PAYE, calculated from database-stored ZIMRA tax bands
- NSSA contributions (3% employee / 3% employer)
- AIDS Levy (3% of PAYE)
- VAT and Withholding Tax with configurable tax codes
- Digital Services Tax (DST)
- ZIMRA FDMS fiscalisation — NIST P-256 ECDSA invoice signing, the 16-character MD5 QR digest ZIMRA's spec requires, and a 72-hour offline retry queue

---

## External Integrations

Each integration is isolated behind a service boundary, so a production credential or hardware connection drops in without touching business logic.

| Integration | Status |
|---|---|
| ZIMRA FDMS fiscal signing | Crypto and offline queue implemented; production device certificate requires ZIMRA registration |
| ZIMRA e-filing (VAT/PAYE portal submission) | Calculations complete; portal submission call is a placeholder |
| EcoCash / OneMoney / PayNow | Payment methods modelled; confirmation webhook not yet wired |
| ZimSwitch direct debit | Not implemented — bank statement CSV import with auto-matching is available instead |
| SMS OTP / TOTP two-factor auth | Fields exist on the user model; verification flow not yet wired into login |
| Redis-backed refresh tokens | Currently issued as signed JWTs; Redis-backed rotation planned |
| RabbitMQ / Hangfire scheduling | Payroll runs, depreciation batches, and FDMS retries are callable on-demand via API; scheduled automation not yet wired |
| PPDPA public e-tendering portal | Bid timestamping, late-bid rejection, and weighted scoring implemented; public vendor self-registration site not built |
| Barcode / RFID hardware | Matching logic implemented; physical scanner/reader integration pending |
| S3, Meilisearch, SignalR | Not yet implemented |

---

## Development Status

The core is functionally complete across all 16 modules, and the financial core has been hardened against a source-level audit; see [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md) for what changed, why, how it was verified, and what is still open. The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests the backend and frontend on every push, and `scripts/smoke-test.sh` exercises the posting lifecycle and access control end to end.

`backend/database/schema.sql` mirrors the original 14-module core for anyone bootstrapping without the .NET SDK; it has not been updated for the Group, Public Sector, Grants and control tables added since. The authoritative schema is always the committed EF Core migrations.

---

## Roadmap

Not yet done (details and rationale in [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md)):

- Refresh-token rotation with server-side revocation, two-factor authentication, login rate limiting and lockout
- Month-end close checklist, bank-reconciliation exception workflow with approvals, and three-way-match controls in procurement
- Scheduled automation (payroll, depreciation, FDMS retries) and idempotency keys on the API
- Request DTOs in place of binding entities directly; PostgreSQL row-level security as a second tenant barrier
- React Native mobile apps, predictive analytics and AI-assisted insights, a vendor self-service portal, an API marketplace

---

## Contributing

Contributions are welcome.

1. Fork the repository
2. Create a feature branch
   ```bash
   git checkout -b feature/new-feature
   ```
3. Commit your changes
   ```bash
   git commit -m "Add new feature"
   ```
4. Push the branch
   ```bash
   git push origin feature/new-feature
   ```
5. Open a pull request

---

## License

This project is licensed under the MIT License.

---

## Author

**Munyaradzi (Bloodshed) Chiondegwa**
Full-Stack Software Developer · Fintech, Governance & Business Systems Specialist · Deaf Community Advocate

Munyaradzi holds an MBA and BAcc (Hons) from Zimbabwe Open University and a BSc in Software Development at BYU-Idaho. His work sits at the intersection of financial governance, regulatory compliance, and software engineering — building systems like ApexERP that combine accounting rigour with modern application architecture.

[![Portfolio](https://img.shields.io/badge/Portfolio-Visit-00C7B7?style=flat-square&logo=netlify&logoColor=white)](https://munyaradzichiondegwa.netlify.app/)
[![LinkedIn](https://img.shields.io/badge/LinkedIn-Connect-0A66C2?style=flat-square&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/munyaradzi-chiondegwa/)
[![GitHub](https://img.shields.io/badge/GitHub-Follow-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/munyaradzichiondegwa)
[![Email](https://img.shields.io/badge/Email-Contact-D14836?style=flat-square&logo=gmail&logoColor=white)](mailto:chiondegwabm@gmail.com)

---

<p align="center">Built with ASP.NET Core, PostgreSQL, and Next.js — for Zimbabwe, for Africa, and beyond.</p>
