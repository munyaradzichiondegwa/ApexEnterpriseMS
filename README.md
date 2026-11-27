<p align="center">
  <img src="docs/images/apexerp-hero.png" alt="ApexERP: Integrate. Optimize. Scale." width="100%">
</p>

<h1 align="center">ApexERP</h1>

<h3 align="center">Integrate. Optimize. Scale.</h3>

<p align="center">
  The multi-tenant, IFRS-compliant ERP platform for businesses, NGOs, and public sector institutions across Zimbabwe and the region.
</p>

<p align="center">
  <a href="https://apex-enterprise-ms.vercel.app"><b>Live demo</b></a> |
  <a href="#getting-started">Getting started</a> |
  <a href="#features">Features</a> |
  <a href="#financial-reporting">Financial reporting</a> |
  <a href="#regulatory-compliance">Compliance</a>
</p>

<p align="center">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white">
  <img alt="Next.js" src="https://img.shields.io/badge/Next.js-15.5.28-black?style=for-the-badge&logo=next.js&logoColor=white">
  <img alt="PostgreSQL" src="https://img.shields.io/badge/PostgreSQL-16-336791?style=for-the-badge&logo=postgresql&logoColor=white">
  <img alt="TypeScript" src="https://img.shields.io/badge/TypeScript-5-3178C6?style=for-the-badge&logo=typescript&logoColor=white">
  <img alt="License" src="https://img.shields.io/badge/License-MIT-green?style=for-the-badge">
</p>

<p align="center">
  <a href="https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml">
    <img alt="Build" src="https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml/badge.svg">
  </a>
</p>

---

## What is ApexERP?

> **ApexERP is an integrated, multi-tenant ERP platform that lets an organisation _integrate_ finance, operations, people, and compliance on a single ledger; _optimize_ how it runs, reports, and complies; and _scale_ from one company to a corporate group, an NGO portfolio, or a government institution.**

*Integrate. Optimize. Scale.* describes how the platform is built, not only how it is marketed.

| | What it means in ApexERP |
|---|---|
| **Integrate** | Every module posts through one double-entry engine (`GlPostingService`), so accounting, payroll, inventory, procurement, manufacturing, projects, grants, and public sector budgets all feed one set of books. Sales orders convert into AR invoices, payroll runs post to the GL, and inter-company transactions mirror across group entities. |
| **Optimize** | Five IFRS statements are computed live from posted ledger data, not templated. Perpetual FIFO and AVCO costing, three-way matching, auto-matching bank reconciliation, multi-tier approval chains with SLA tracking, and real-time budget enforcement replace manual work. Zimbabwe tax and statutory rules (PAYE, NSSA, AIDS Levy, VAT, Withholding Tax, ZIMRA FDMS) are built in. |
| **Scale** | Every business table is tenant-scoped, so many companies share one deployment with isolated data. Multi-entity consolidation, inter-company netting, and fund-centre and grant tagging take the same platform from a single SME to a group, an NGO, or a public sector body. The stack runs from one `docker compose up` to a hosted deployment. |

ApexERP runs real, working business logic across all 16 core ERP modules, not UI mockups. It uses a hybrid **ASP.NET Core 8 Web API + Next.js 15.5.28** architecture backed by **PostgreSQL 16**.

### Who it is for

- **Businesses:** general ledger, receivables and payables, inventory, manufacturing, payroll, CRM, fixed assets, and group consolidation.
- **NGOs:** grant and donor management, restriction tracking, indirect cost recovery, sub-awards, beneficiary registers, and donor financial reporting.
- **Public sector institutions:** Vote, Program, and Sub-Program fund hierarchy, commitment accounting, PPDPA e-tendering, and asset verification.

---

## Table of Contents

- [Live Demo](#live-demo)
- [Features](#features)
- [Financial Reporting](#financial-reporting)
- [Tech Stack](#tech-stack)
- [Architecture](#architecture)
- [Product architecture and consolidation acceptance register](docs/PRODUCT_ARCHITECTURE_ROADMAP.md)
- [Getting Started](#getting-started)
- [Configuration and Secrets](#configuration-and-secrets)
- [Security and Multi-Tenancy](#security-and-multi-tenancy)
- [Roles and Permissions](#roles-and-permissions)
- [Accounting Controls](#accounting-controls)
- [Testing](#testing)
- [Regulatory Compliance](#regulatory-compliance)
- [External Integrations](#external-integrations)
- [Operations and Documentation](#operations-and-documentation)
- [Development Status](#development-status)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)
- [Author](#author)

---

## Live Demo

The web application is deployed on Vercel: **[apex-enterprise-ms.vercel.app](https://apex-enterprise-ms.vercel.app)**

To explore the API, run the stack locally (see [Getting Started](#getting-started)) and open Swagger at `http://localhost:8080/swagger`.

---

## Features

### Financial Management and General Ledger
- Chart of accounts, cost centres, and accounting periods
- Shared double-entry posting engine (`GlPostingService`) used by every module. It validates debit/credit balance, enforces open-period rules, auto-numbers entries, maintains running balances, and supports reversal
- Inter-company mirroring: auto-creates a mirror AP invoice in a linked group company, with periodic netting and settlement
- Multi-entity consolidation: aggregates subsidiary trial balances, eliminates inter-company balances, and calculates non-controlling interest

### Accounts Receivable and Payable
- Customer and supplier invoicing with per-line VAT codes
- Receipting and payments with multi-invoice allocation
- Withholding tax calculation on non-compliant suppliers
- AR and AP ageing analysis (current / 30 / 60 / 90+)
- Fund-centre and grant tagging with real-time budget enforcement

### Cash and Bank
- Bank account management
- Statement import with auto-matching reconciliation (date ± 3 days, exact amount) and manual reconciliation

### Inventory and Supply Chain
- Real perpetual **FIFO** and **AVCO** costing
- Purchase orders, goods receipt, and three-way matching
- Vendor management and low-stock alerts

### Manufacturing
- Bill of Materials with scrap factors
- Standard-cost roll-up
- Work order release and completion at actual cost

### Payroll and HR
- Employee and department management
- ZIMRA PAYE from database-stored tax bands, NSSA (3% employee / 3% employer), and AIDS Levy (3% of PAYE)
- Gross-to-net payroll runs with GL posting and payslip retrieval

### CRM and Sales
- Lead pipeline and opportunity tracking
- Sales orders that convert directly into draft AR invoices

### Project Management
- Grant-linkable projects, tasks, and timesheets with approval
- Budget-versus-actual tracking

### Fixed Assets
- Asset register with barcode/RFID tagging
- Straight-line and reducing-balance depreciation, run as a monthly batch
- IAS 16 revaluation to fair value, with disposal gain/loss

### Grants, Donors, and NGO Management
- Grant master with donor, award, and restriction tracking
- GL-account-level budgets with real-time over-budget blocking
- Indirect cost recovery and sub-award disbursement tracking
- Beneficiary and distribution registers, and donor financial reporting

### Public Sector and Government
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

### Controls, Automation and Operations
- New screens: Accounting Automation, Payables Controls, Receivables Controls, Reconciliation, Period Close, Audit Workspace, Governance and Approvals, Integrations and Jobs, Account Security, and an executive dashboard on the Dashboard page
- Upgrade, configuration, backup and recovery, monitoring and runbook guidance (see [Operations and Documentation](#operations-and-documentation))

---

## Financial Reporting

Every statement below is computed live from posted ledger data, not templated. All five are available individually or in a single call via `GET /api/v1/reports/full-statement-pack`.

1. **Statement of Financial Position** (Balance Sheet)
2. **Statement of Profit or Loss and Other Comprehensive Income**, including a real OCI section (asset revaluation surplus and foreign operation translation reserve)
3. **Statement of Changes in Equity**, reconciling opening balance, profit transfer, OCI movement, dividends, and share capital movements to the Balance Sheet
4. **Statement of Cash Flows** (indirect method), with working-capital movements derived from `GlAccount.CashFlowCategory` tagging rather than hardcoded account codes
5. **Notes to the Financial Statements**: accounting policies, PPE movement schedule, inventory breakdown, AR/AP ageing, share capital movements, related-party disclosures, and IFRS 8 segment reporting

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
- Vitest for unit tests

**Infrastructure**
- Docker Compose (Postgres, API, and Web in one command)
- GitHub Actions CI (restore, build, migration check, frontend type-check and build)

---

## Architecture

```text
ApexERP/
├── backend/                         ASP.NET Core 8 solution (C#)
│   ├── src/
│   │   ├── ApexERP.Domain/          Entities, enums (no dependencies)
│   │   ├── ApexERP.Application/     Services (business logic), Security (JWT/BCrypt, permissions, config checks)
│   │   ├── ApexERP.Infrastructure/  EF Core DbContext, tenant query filters, migrations
│   │   └── ApexERP.API/             Controllers, authorization, request transaction filter, Program.cs, Dockerfile
│   ├── tests/ApexERP.Tests/         xUnit unit tests and PostgreSQL integration tests
│   └── database/
│       ├── hardening/               SQL guards: append-only audit log and ledger, optional row-level security
│       └── seed.sql                 Zimbabwe chart of accounts, VAT/WHT codes, PAYE bands
├── frontend/                        Next.js 15.5.28 (App Router, TypeScript, Tailwind)
│   ├── app/                         One route per module (dashboard, gl, ar, ap, inventory, group, public-sector, grants, reports, ...)
│   ├── components/AppShell.tsx      Responsive, permission-aware sidebar (desktop) / hamburger drawer (mobile)
│   ├── lib/                         API client, auth context, session storage, permissions, config
│   └── tests/                       Vitest unit tests
├── docs/
│   ├── images/apexerp-hero.png      README hero image
│   ├── AUDIT_REMEDIATION.md         What the security/accounting audit found and what was done about it
│   ├── OPERATIONS.md                Upgrades, configuration, backups, monitoring, runbooks
│   └── READINESS_TRACKER.md         Status of every readiness specification item
├── scripts/                         Migration generators, schema export, smoke test
└── docker-compose.yml               Postgres, API, and Web, one command to run everything
```

**Integrate: the GL posting pattern.** No controller writes directly to `journal_entries` or `journal_lines`. Every module posts through `GlPostingService`, the single source of truth for balance validation, period-open checks, entry numbering, and account balance maintenance. Posted lines can carry a `FundCentreId` and/or `GrantId`, so one posting engine feeds the GAAP books, public sector budget-versus-actual, and donor reporting at the same time. Each mutating API request runs in one database transaction, and the posting engine takes transaction-scoped locks for numbering and period changes.

**Scale: multi-tenancy.** Every business table carries a `company_id`. EF Core global query filters, set per request by tenant middleware from the JWT, scope every query automatically. This is the application-level equivalent of PostgreSQL row-level security. The filters **fail closed**: with no tenant context a query returns nothing. Cross-tenant services (`InterCompanyService`, `ConsolidationService`, sign-in) explicitly opt out with `IgnoreQueryFilters()`, since their job is to read and write across companies. Each such query still filters explicitly by the relevant `companyId`, and group features are limited to the caller's own group.

**Frontend.** A responsive `AppShell` (persistent sidebar on desktop, hamburger-triggered drawer on mobile), proper `viewport` meta and theme colour, mobile-first Tailwind layouts, a PWA manifest with install icons, and horizontally scrolling data tables on narrow screens.

---

## Getting Started

### Prerequisites
- Docker Desktop
- .NET 8 SDK (for local backend development and migration generation)
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

The EF Core migrations are committed to the repository. The local Development configuration applies pending migrations on startup for convenience. This compose file is a **local development stack** (it runs the API in the `Development` environment and uses a development database owner). Production deployments must apply migrations separately with a deployment identity, then run the API with a least-privilege runtime role. See [Database migrations](#database-migrations) and [Configuration and Secrets](#configuration-and-secrets).

| Service | URL |
|---|---|
| Web app | http://localhost:3000 |
| API | http://localhost:8080 |
| Swagger | http://localhost:8080/swagger |
| PostgreSQL | 127.0.0.1:5432 (`apexerp` / `apexerp_dev_password`: development only, reachable from this machine only) |

**First run**
1. Open [localhost:3000/register](http://localhost:3000/register) and create your company and admin user (password: at least 10 characters). This seeds a Company, an Administrator role holding every permission, and your user.
2. Run `backend/database/seed.sql` against the new database (fill in your company UUID from the `companies` table) to load a starter Zimbabwe chart of accounts, VAT/WHT tax codes, and PAYE bands.
   > ⚠️ The PAYE bands in `seed.sql` are illustrative placeholders modelled on ZIMRA's tax-table structure. Verify and replace them with the exact bands from the current ZIMRA Public Notice before relying on them for real payroll.
3. Log in and start creating customers, suppliers, and inventory items, and posting transactions. Manual journals follow the [approval workflow](#accounting-controls). In Development, one user may prepare, approve and post their own journal so a single-person setup can try it out.

### Manual setup

**Backend.** `dotnet run` uses the committed launch profile (`Properties/launchSettings.json`), which selects the `Development` environment, so the development-only settings in `appsettings.Development.json` apply. You need a local PostgreSQL 16 matching its connection string.

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

ApexERP uses real, versioned EF Core migrations (never `EnsureCreated()` or hand-run SQL). The migrations are committed under `backend/src/ApexERP.Infrastructure/Data/Migrations`. Automatic migration is enabled only in the local Development configuration. Outside Development, `Database__ApplyMigrationsOnStartup` defaults to `false` and the API refuses to start if automatic migration is requested. Apply migrations as a separate deployment step using a dedicated schema-migration identity; start the API afterwards with a runtime identity that has no DDL privileges, is not a table owner, and has no `BYPASSRLS` privilege.

When you change an entity class under `ApexERP.Domain/Entities` (or the model configuration in `ApexErpDbContext`), generate a new migration and commit it:

```powershell
.\scripts\generate-migrations.ps1 -MigrationName AddCustomerLoyaltyPoints     # Windows PowerShell
```

```bash
./scripts/generate-migrations.sh AddCustomerLoyaltyPoints                      # macOS/Linux
```

The `AuditHardeningPhase1And2` migration adds the exception queue and the unique and check constraints that protect the ledger. It is documented in [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md). **Before applying any migration to a database that already holds data**, review its preconditions and prepare any required data backfill. The tenant-foundation migration deliberately leaves `Companies.TenantId` nullable and does not assign existing companies to tenants.

---

## Configuration and Secrets

The API checks its security-critical configuration when it starts and **refuses to start outside `Development`** if anything is missing or unsafe. There are no fallback secrets in the code.

| Setting | Environment variable | Required | Notes |
|---|---|---|---|
| JWT signing key | `Jwt__Secret` | Always | At least 32 characters. Outside Development it is also rejected if it looks like a placeholder or has too little variety. Generate one with `openssl rand -base64 48`. |
| Token issuer | `Jwt__Issuer` | Always | `ApexERP` by default (`appsettings.json`). |
| Database | `ConnectionStrings__Default` | Always | Use a least-privilege runtime identity. Production must not use the development password or a schema-owner account. |
| Startup migrations | `Database__ApplyMigrationsOnStartup` | Optional | `false` outside Development. Production schema changes must run as a separate deployment step. |
| PostgreSQL RLS | `Security__RowLevelSecurity__Enabled` | Optional | `false` by default. When `true`, startup verifies that tenant tables have `FORCE ROW LEVEL SECURITY` and the runtime role is neither superuser, `BYPASSRLS`, nor owner of protected tables. |
| Web app origin(s) | `Frontend__Origin` | Outside Development | Comma-separated real `http(s)` origins, never `localhost`. |
| Journal self-approval | `Controls__JournalApproval__AllowSelfApproval` | Optional | `false` by default (segregation of duties). Set to `true` only for single-user development. |
| Swagger UI | `Swagger__Enabled` | Optional | On in Development; elsewhere only when set to `true`. |

Development-only values live in `appsettings.Development.json`. In production, supply secrets through environment variables or your platform's secret store, and set `ASPNETCORE_ENVIRONMENT=Production`. `.env.example` lists what `docker compose` reads.

**Web app (Vercel or any host).** `NEXT_PUBLIC_API_URL` is compiled into the browser bundle at **build time**, so it must be set in the hosting project's environment variables *before* the build. A build on Vercel without it fails with a clear message, and a production build without it shows a configuration screen instead of silently calling `localhost`. After deploying, set the API's `Frontend__Origin` to the web app's URL, otherwise browsers cannot call the API.

---

## Security and Multi-Tenancy

- JWT authentication. Access tokens last 15 minutes. Refresh tokens use a different audience, so they can never be used as access tokens.
- BCrypt password hashing, with a minimum password policy (10+ characters).
- **Deny-by-default RBAC.** Every endpoint requires a permission (see [Roles and Permissions](#roles-and-permissions)), and a test fails if any endpoint is left open.
- **Tenant isolation fails closed.** Tenant and company query filters return nothing when their identity is missing, and authenticated requests without valid `tenant_id` and `company_id` claims are refused. PostgreSQL RLS is an additional boundary; `IgnoreQueryFilters()` does not bypass it. Cross-company operations use narrowly scoped `CrossTenantScope`, while platform operations use a named `SystemAccessScope`. A missing company identity never grants bypass.
- Group, consolidation and inter-company features are scoped to the caller's *own* group. A company can only be attached under another company when both sides have agreed.
- Immutable, append-only audit log. A ledger cannot be edited once posted; corrections are made by reversal.
- One database transaction per mutating request, so multi-step postings are all-or-nothing.
- Configurable multi-tier approval chain engine (up to 5 sequential tiers). Approvers must hold the tier's role and cannot approve their own requests.

---

## Roles and Permissions

A role's permissions are a JSON array of keys, for example `["gl.journals.create","reports.financial.read"]`. The Administrator role created at registration holds `["*"]`. A key ending in `.*` grants a whole branch (`"gl.*"`).

- **Module defaults.** Any endpoint without a more specific key needs `{module}.read` (GET) or `{module}.write` (anything else), for example `payroll.read`, `ar.write`, `fixed-assets.write`.
- **Sensitive actions have their own keys**, including `gl.accounts.manage`, `gl.journals.create`, `gl.journals.approve`, `gl.journals.post`, `gl.journals.reverse`, `accounting.period.close`, `accounting.period.reopen`, `reports.financial.read`, `ar.invoices.post`, `ap.invoices.post`, `ap.payments.create`, `payroll.runs.post`, `payroll.config.manage`, `fixed-assets.depreciate`, `fixed-assets.dispose`, `fixed-assets.revalue`, `equity.dividends.manage`, `group.structure.manage`, `group.consolidation.read`, `group.netting.execute`, `exceptions.manage`, `workflow.decide`, `admin.users.manage`, `admin.roles.manage` and `admin.company.manage`. `GET /api/v1/admin/permissions` returns the full list.
- **No privilege escalation.** A user can only grant a role, or assign a user a role, that contains permissions they hold themselves.
- Roles are created with `POST /api/v1/admin/roles`, for example `{ "name": "Accountant", "permissionsJson": "[\"gl.*\",\"reports.financial.read\"]" }`. Users are created with `POST /api/v1/admin/users`.
- The navigation hides modules a user cannot use, but the **API enforces every permission** regardless of what the interface shows.

> If you created custom roles before this change, their old keys (for example `gl.journal.post`) no longer match the catalogue. Edit them to use the keys above. The Administrator role is unaffected.

---

## Accounting Controls

**Manual journals** follow a controlled lifecycle: `Draft → Submitted → Approved → Posted → Reversed`, with `Rejected` and `Cancelled` as exits.

- Creating a journal saves a **draft**. Nothing reaches the ledger or any account balance until it is posted.
- **Segregation of duties.** The person who prepared a journal cannot approve it, unless `AllowSelfApproval` is enabled (which is only the Development default).
- Posting re-validates the journal from its stored lines, takes the final `JE-yyyyMM-nnnnn` number, and updates balances in one transaction. Repeating a request is harmless.
- System postings (AR, AP, payroll, inventory, and so on) post immediately through the same engine.

**Posting rules.** Every journal must balance in base currency. Lines are single-sided and non-negative. The exchange rate must be positive and the currency a valid 3-letter code. The period must be open, and deactivated accounts accept no new postings (reversals excepted). The database enforces the amount and rate rules again as check constraints.

**Reversal.** A posted journal can be reversed once, atomically. The reversal keeps the original's cost-centre, fund-centre and grant tags, so dimension reports net to zero. A unique index makes a second reversal impossible even under concurrent requests.

**Periods.** Closing is refused while the period still holds unposted journals. Closing is serialised against posting, and can be undone only by an audited reopen with a reason.

**Numbering.** Journal and document numbers are allocated under a transaction-scoped lock, so concurrent requests cannot collide.

**Reports.** Ledger reports include posted *and* reversed journals (a reversal offsets its original). They include deactivated accounts with history, treat "as of" dates as the whole day, and show prior-period and current-year profit separately on the balance sheet, so it balances whether or not year-end closing entries were posted. `GET /api/v1/reports/integrity-check` returns the control totals (trial-balance difference, accounting-equation difference, unbalanced journals, journals awaiting approval). Every difference should be zero.

**Exceptions.** Work that cannot complete automatically, such as an inter-company mirror that fails after the local invoice posted, becomes a visible item in **Exceptions**. It can be retried, resolved or dismissed with a note, rather than being silently swallowed.

**Audit trail.** Each journal's history (who did what, and when) is available at `GET /api/v1/gl/journal-entries/{id}/history`.

---

## Testing

```bash
# Backend unit tests (no database needed)
dotnet test backend/tests/ApexERP.Tests

# Backend integration tests against a disposable PostgreSQL server (the user must be allowed to create databases and roles)
APEX_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet test backend/tests/ApexERP.Tests

# Frontend
cd frontend && npm test && npm run typecheck

# End-to-end smoke test against a running stack (Development API)
./scripts/smoke-test.sh
```

The integration tests create a throw-away database per test, apply the real migrations to it, and cover tenant isolation, PostgreSQL RLS under a non-owner/non-`BYPASSRLS` role, concurrent numbering, atomic and single reversal, reports, the approval workflow, period locking and ledger immutability. They are skipped when `APEX_TEST_DB` is not set. The connection user must be allowed to create databases and roles (`CREATEDB` and `CREATEROLE`, or a superuser). GitHub Actions runs these tests against an isolated PostgreSQL service.

---

## Regulatory Compliance

**IFRS**
- IAS 1: full five-statement presentation
- IAS 16: property, plant and equipment revaluation to fair value, with the surplus recognised in OCI/equity

**Zimbabwe tax and statutory compliance**
- PAYE, calculated from database-stored ZIMRA tax bands
- NSSA contributions (3% employee / 3% employer)
- AIDS Levy (3% of PAYE)
- VAT and Withholding Tax with configurable tax codes
- Digital Services Tax (DST)
- ZIMRA FDMS fiscalisation: NIST P-256 ECDSA invoice signing, the 16-character MD5 QR digest ZIMRA's specification requires, and a 72-hour offline retry queue

---

## External Integrations

Each integration is isolated behind a service boundary, so a production credential or hardware connection drops in without touching business logic.

| Integration | Status |
|---|---|
| ZIMRA FDMS fiscal signing | Crypto and offline queue implemented. A production device certificate requires ZIMRA registration. |
| ZIMRA e-filing (VAT/PAYE portal submission) | Calculations complete. The portal submission call is a placeholder. |
| EcoCash / OneMoney / PayNow | Payment methods modelled. Confirmation webhook not yet wired. |
| ZimSwitch direct debit | Not implemented. Bank statement CSV import with auto-matching is available instead. |
| SMS OTP / TOTP two-factor auth | Fields exist on the user model. Verification flow not yet wired into login. |
| Redis-backed refresh tokens | Currently issued as signed JWTs. Redis-backed rotation planned. |
| RabbitMQ / Hangfire scheduling | Payroll runs, depreciation batches, and FDMS retries are callable on demand via the API. Scheduled automation not yet wired. |
| PPDPA public e-tendering portal | Bid timestamping, late-bid rejection, and weighted scoring implemented. Public vendor self-registration site not built. |
| Barcode / RFID hardware | Matching logic implemented. Physical scanner/reader integration pending. |
| S3, Meilisearch, SignalR | Not yet implemented. |

---

## Operations and Documentation

| Document | Purpose |
|---|---|
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Upgrade steps (generate the migration first), configuration, backups and recovery, monitoring and runbooks |
| [docs/READINESS_TRACKER.md](docs/READINESS_TRACKER.md) | Status of every item in the APEXERP readiness specification |
| [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md) | What the security and accounting audit found, what changed, how it was verified, and what is still open |

---

## Development Status

The core is functionally complete across all 16 modules, and the financial core has been hardened against a source-level audit. See [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md) for details.

The GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests the backend and frontend on every push. `scripts/smoke-test.sh` exercises the posting lifecycle and access control end to end.

The authoritative schema is always the committed EF Core migrations. `scripts/export-schema.sh` writes them out as one idempotent SQL script (`backend/database/schema.generated.sql`) for DBAs and audits. The older hand-written `schema.sql` was removed because it drifted.

**Repository snapshot:** 12 commits · 0 stars · 0 forks · MIT licence

---

## Roadmap

Not yet done (details and rationale in [docs/AUDIT_REMEDIATION.md](docs/AUDIT_REMEDIATION.md)):

- Refresh-token rotation with server-side revocation, two-factor authentication, login rate limiting and lockout
- Month-end close checklist, bank-reconciliation exception workflow with approvals, and three-way-match controls in procurement
- Scheduled automation (payroll, depreciation, FDMS retries) and idempotency keys on the API
- Request DTOs in place of binding entities directly, and PostgreSQL row-level security as a second tenant barrier
- React Native mobile apps, predictive analytics and AI-assisted insights, a vendor self-service portal, and an API marketplace

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

This project is licensed under the MIT License. See the `LICENSE` file.

---

## Author

**Munyaradzi (Bloodshed) Chiondegwa**
Full-Stack Software Developer · Fintech, Governance and Business Systems Specialist · Deaf Community Advocate

Munyaradzi holds an MBA and a Bachelor of Accounting (Hons) from Zimbabwe Open University, and both a BSc in Software Development and an Associate of Applied Science in Software Development from BYU-Idaho. His work sits at the intersection of financial governance, regulatory compliance, and software engineering, building systems like ApexERP that combine accounting rigour with modern application architecture.

[![Portfolio](https://img.shields.io/badge/Portfolio-Visit-00C7B7?style=flat-square&logo=netlify&logoColor=white)](https://munyaradzichiondegwa.netlify.app/)
[![LinkedIn](https://img.shields.io/badge/LinkedIn-Connect-0A66C2?style=flat-square&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/munyaradzi-chiondegwa/)
[![GitHub](https://img.shields.io/badge/GitHub-Follow-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/munyaradzichiondegwa)
[![Email](https://img.shields.io/badge/Email-Contact-D14836?style=flat-square&logo=gmail&logoColor=white)](mailto:chiondegwabm@gmail.com)

---

<p align="center">
  <b>ApexERP: Integrate. Optimize. Scale.</b><br>
  Built with ASP.NET Core, PostgreSQL, and Next.js, for Zimbabwe, for Africa, and beyond.
</p>

