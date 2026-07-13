# ApexEnterpriseMS

> **ApexERP is a multi-tenant, IFRS-compliant ERP platform for businesses, NGOs, and public sector institutions across Zimbabwe and the region.**

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Next.js](https://img.shields.io/badge/Next.js-14-black?style=for-the-badge&logo=next.js&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=for-the-badge&logo=postgresql&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-5-3178C6?style=for-the-badge&logo=typescript&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

[![Build](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml/badge.svg)](https://github.com/munyaradzichiondegwa/ApexEnterpriseMS/actions/workflows/build.yml)

---

## Overview

ApexERP is a multi-tenant Enterprise Resource Planning platform built for businesses, NGOs, and public sector institutions operating in Zimbabwe and the wider region. It runs real, working business logic — not UI mockups — across all 16 core ERP modules on a hybrid **ASP.NET Core 8 Web API + Next.js 14** architecture, backed by **PostgreSQL 16**.

At its core, ApexERP ships a complete **five-statement IFRS reporting suite** computed live from posted ledger data, alongside native **Zimbabwe regulatory compliance** — PAYE, NSSA, AIDS Levy, VAT, Withholding Tax, and genuine ZIMRA FDMS fiscal signing.

---

## Table of Contents

- [Features](#features)
- [Financial Reporting](#financial-reporting)
- [Tech Stack](#tech-stack)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [Security and Multi-Tenancy](#security-and-multi-tenancy)
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
- Next.js 14 (App Router)
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
│   │   ├── ApexERP.Application/     Services (business logic), Security (JWT/BCrypt)
│   │   ├── ApexERP.Infrastructure/  EF Core DbContext, tenant query filters
│   │   └── ApexERP.API/             Controllers, Program.cs, Dockerfile
│   └── database/
│       ├── schema.sql               Reference schema for manual bootstrap without the .NET SDK
│       └── seed.sql                 Zimbabwe chart of accounts, VAT/WHT codes, PAYE bands
├── frontend/                        Next.js 14 (App Router, TypeScript, Tailwind)
│   ├── app/                         One route per module (dashboard, gl, ar, ap, inventory, group, public-sector, grants, reports, ...)
│   ├── components/AppShell.tsx      Responsive sidebar (desktop) / hamburger drawer (mobile)
│   └── lib/                         Axios API client + auth context
└── docker-compose.yml               Postgres + API + Web, one command to run everything
```

**Multi-tenancy** — every business table carries a `company_id`. EF Core global query filters, set per-request by tenant middleware from the JWT, scope every query automatically — the application-level equivalent of PostgreSQL row-level security. Cross-tenant services (`InterCompanyService`, `ConsolidationService`) explicitly bypass those filters, since their job is to read and write across a company group — each query still explicitly filters by the relevant `companyId`.

**GL posting pattern** — no controller writes directly to `journal_entries` or `journal_lines`. Every module posts through `GlPostingService`, the single source of truth for balance validation, period-open checks, entry numbering, and account balance maintenance. Posted lines can carry a `FundCentreId` and/or `GrantId`, so one posting engine feeds the GAAP books, public sector budget-vs-actual, and donor reporting simultaneously.

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

Generate the initial migration (one-time, needs Docker and internet access):

```bash
# Windows PowerShell
.\scripts\generate-migrations.ps1

# macOS/Linux
./scripts/generate-migrations.sh
```

Then bring the stack up:

```bash
docker compose up --build
```

| Service | URL |
|---|---|
| Web app | http://localhost:3000 |
| API | http://localhost:8080 |
| Swagger | http://localhost:8080/swagger |
| PostgreSQL | localhost:5432 (`apexerp` / `apexerp_dev_password` — change before any real deployment) |

**First run**
1. Open [localhost:3000/register](http://localhost:3000/register) and create your company and admin user — this seeds a Company, an Administrator role with full permissions, and your user.
2. Run `backend/database/seed.sql` against the new database (fill in your company UUID from the `companies` table) to load a starter Zimbabwe chart of accounts, VAT/WHT tax codes, and PAYE bands.
   > ⚠️ The PAYE bands in `seed.sql` are illustrative placeholders modelled on ZIMRA's tax-table structure. Verify and replace them with the exact bands from the current ZIMRA Public Notice before relying on them for real payroll.
3. Log in and start creating customers, suppliers, and inventory items, and posting transactions.

### Manual setup

**Backend**
```bash
cd backend
dotnet restore
dotnet build
dotnet run --project src/ApexERP.API
```

**Frontend**
```bash
cd frontend
npm install
npm run dev
```

### Database migrations

ApexERP uses real, versioned EF Core migrations rather than `EnsureCreated()` or hand-run SQL. Generate them once with the scripts above, commit the generated files, and every environment applies the same schema automatically from then on. Re-run the script with a descriptive name any time an entity class under `ApexERP.Domain/Entities` changes, e.g.:

```bash
.\scripts\generate-migrations.ps1 -MigrationName AddCustomerLoyaltyPoints
```

`Program.cs` calls `db.Database.Migrate()` on startup, so `docker compose up` applies any pending migrations automatically — no manual `dotnet ef database update` step needed once migrations exist.

---

## Security and Multi-Tenancy

- JWT authentication with `company_id` and role claims
- BCrypt password hashing
- Role-based access control (RBAC) enforced at the API layer
- Per-tenant data isolation via EF Core global query filters
- Immutable, append-only audit log
- Configurable multi-tier approval chain engine (up to 5 sequential tiers with per-tier SLA tracking)

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

The backend domain layer (60+ entities across all 16 modules) and the Next.js frontend (24 routes) both build cleanly. The Application, Infrastructure, and API layers depend on NuGet package restore, so run a full `dotnet build` in an environment with normal outbound access before deploying. A GitHub Actions workflow (`.github/workflows/build.yml`) runs the full restore/build/migration-check pipeline on push, and `scripts/smoke-test.sh` provides a curl-based end-to-end check of the GL posting engine (register → post a journal entry → pull the trial balance).

`backend/database/schema.sql` mirrors the original 14-module core for anyone bootstrapping without the .NET SDK; it has not yet been updated for the Group, Public Sector, and Grants tables added since. The authoritative schema is always whatever `dotnet ef migrations` generates from `ApexERP.Domain/Entities`.

---

## Roadmap

- React Native mobile apps
- Predictive analytics and AI-assisted financial insights
- Full Playwright/xUnit automated test suites
- Workflow engine upgrade beyond the current generic approval model
- Redis-backed refresh-token rotation
- RabbitMQ/Hangfire scheduled automation for payroll, depreciation, and FDMS retries
- Public-facing vendor self-registration portal
- SMS OTP / TOTP two-factor authentication
- API marketplace

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

Munyaradzi holds an MBA and BAcc (Hons) from Zimbabwe Open University and is completing a BSc in Software Development at BYU-Idaho. His work sits at the intersection of financial governance, regulatory compliance, and software engineering — building systems like ApexERP that combine accounting rigour with modern application architecture.

[![Portfolio](https://img.shields.io/badge/Portfolio-Visit-00C7B7?style=flat-square&logo=netlify&logoColor=white)](https://munyaradzichiondegwa.netlify.app/)
[![LinkedIn](https://img.shields.io/badge/LinkedIn-Connect-0A66C2?style=flat-square&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/munyaradzi-chiondegwa/)
[![GitHub](https://img.shields.io/badge/GitHub-Follow-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/munyaradzichiondegwa)
[![Email](https://img.shields.io/badge/Email-Contact-D14836?style=flat-square&logo=gmail&logoColor=white)](mailto:chiondegwabm@gmail.com)

---

<p align="center">Built with ASP.NET Core, PostgreSQL, and Next.js — for Zimbabwe, for Africa, and beyond.</p>



