# Audit remediation

This records what was changed in response to the repository audit (`apex.docx`, 10 findings with a phased plan) and the design document *Translating Accounting Principles into Enterprise Software*, why, how each change was verified, and what is deliberately **not** done yet.

It covers the audit's Phase 1 (P0) and Phase 2 (P1) in full. Phase 3 and Phase 4 items that are new features rather than fixes are listed under [Not done yet](#not-done-yet).

## Contents

1. [The ten audit findings](#the-ten-audit-findings)
2. [Further defects found while fixing](#further-defects-found-while-fixing)
3. [Behaviour changes](#behaviour-changes)
4. [Deploying this change](#deploying-this-change)
5. [How it was verified, and what was not](#how-it-was-verified-and-what-was-not)
6. [Not done yet](#not-done-yet)

---

## The ten audit findings

| # | Severity | Finding | What was done | Where |
|---|---|---|---|---|
| 1 | Critical | A hard-coded fallback JWT secret in `Program.cs` | Removed. The API now **refuses to start** outside Development when `Jwt:Secret` is missing, under 32 characters, a known placeholder, or low-variety. The committed `appsettings.json` also held a placeholder secret, which a code-only fix would have left active, so that is removed too; the development key lives only in `appsettings.Development.json`. The compose default is a dev-only string the API rejects outside Development. The same fail-fast covers the database connection string and the web origin. | `Program.cs`, `Security/StartupConfigValidator.cs`, `appsettings*.json`, `docker-compose.yml`, `.env.example` |
| 2 | Critical | No permission checks on GL actions (the `permissions` claim was never evaluated) | A permission system: `[HasPermission]` on sensitive actions plus a **deny-by-default convention** that gives every other endpoint `{module}.read` / `{module}.write`. The permissions the audit names (`gl.accounts.*`, `gl.journals.create/approve/post/reverse`, `accounting.period.close`, `reports.financial.read`) are implemented as specified. Applied to all 177 endpoints, including admin, payroll, fixed assets, dividends, group and reporting. | `API/Authorization/*`, `Security/Permissions.cs`, controllers |
| 3 | Critical | Trial balance and income statement dropped inactive accounts | The `IsActive` filter is removed everywhere it hid history (trial balance, income statement, consolidation). Fixing this properly also required three related corrections, listed in the next section. New `integrity-check` report. | `ReportingService.cs`, `ConsolidationService.cs` |
| 4 | High | Reversal was not atomic | One transaction covers the reversal journal, balance updates and the original's status. Concurrent attempts on the same journal are serialised, and a unique index on `ReversalOfId` makes a second reversal impossible. The reversal date cannot precede the original. | `GlPostingService.ReverseAsync`, migration |
| 5 | High | Journal numbering used a count (race) | Numbers are allocated under a transaction-scoped PostgreSQL advisory lock, with the existing unique index as backstop. The same race existed in every other document-number generator (AR, AP, PO, GRN, sales orders, work orders, tenders, verification batches, inter-company); all now take the lock. | `GlPostingService`, `DbContextTransactionExtensions`, the AR, AP, supply-chain, CRM, tender/verification and inter-company services, `ManufacturingController` |
| 6 | High | Reversal lost grant / fund-centre tags | The reversal copies cost-centre, fund-centre and grant tags from the original. | `GlPostingService.ReverseAsync` |
| 7 | High | Exchange rate not validated | Rate must be positive, currency a valid 3-letter code, lines single-sided and non-negative, amounts rounded to the storage scale (as PostgreSQL does). The database repeats the amount and rate rules as check constraints. | `Services/JournalRules.cs`, migration |
| 8 | High | Inter-company mirror failure silently swallowed | The mirror is now idempotent and **atomic** (the old code committed the mirror invoice, journal and link separately, so a failure left an orphan Draft invoice in the counterparty's books and a retry created a duplicate). A failure is isolated with a savepoint, raises an **exception item** (owner, status, history), and the response reports `interCompany.status` = `NotApplicable` / `Completed` / `Failed`. New Exceptions queue with retry, resolve and dismiss, in the API and the UI. | `InterCompanyService.TryMirrorAsync`, `ExceptionQueueService`, `ExceptionsController`, `app/exceptions` |
| 9 | High | Tenant filter failed open when no company was set | The filter now fails closed. Requests with a token lacking a valid `company_id` are refused, and the audit of every `IgnoreQueryFilters()` call turned up a serious cross-tenant path (next section). | `ApexErpDbContext`, `Program.cs`, `GroupController` |
| 10 | High | Frontend: unguarded `JSON.parse`; "Loading" forever; silent localhost API fallback | Session reads are validated and can never throw; loading always ends; a loading screen that offers a way out after 8 seconds; error boundaries so a client exception never leaves a blank page; 20-second request timeout and plain-language error messages; the session ends cleanly when the token expires. A production build without `NEXT_PUBLIC_API_URL` shows a configuration screen (and **fails a Vercel build** with a clear message) instead of calling localhost. Navigation and pages respect permissions. | `lib/session.ts`, `lib/auth.tsx`, `lib/api.ts`, `lib/config.ts`, `components/*`, `app/error.tsx` |

**Phase 2 workflow.** Manual journals now follow Draft → Submitted → Approved → Posted → Reversed (with Rejected and Cancelled), with segregation of duties, idempotent transitions and a per-journal audit history. Closing a period is refused while unposted journals exist and is serialised against posting; reopening requires a reason. The GL page has the matching interface.

## Further defects found while fixing

These were not in the audit; they surfaced while reading the code the audit points at.

| Severity | Defect | Fix |
|---|---|---|
| Critical | **Any signed-in user could create an Administrator role and a user holding it** (`POST /admin/roles`, `/admin/users` were open to everyone), and could assign another company's role. | Admin endpoints need `admin.*.manage`; a caller can only grant permissions they hold; roles must belong to the caller's company; e-mail addresses are unique system-wide; minimum password policy. |
| Critical | **Cross-tenant data exposure**: `GET /group/companies` listed every company; `PUT /group/companies/{id}/structure` let any user attach *any* company under their own; `consolidated-statements` then read that company's ledger with filters off. | Group features are limited to the caller's own group; a company can only be attached under an outside parent if that parent has registered it as a linked customer/supplier (consent from both sides); netting is limited to group members. |
| High | **A refresh token was a valid Bearer access token** (same key, issuer and audience, no `company_id`), which also made the fail-open filter exploitable. | Refresh tokens use a different audience; bearer validation also rejects them explicitly. |
| High | **Reports excluded reversed originals** while including their (posted) reversal, so every reversal distorted balances. The same `Posted`-only filter was in 12 places, including grant spend tracking. | Every ledger query that filtered on `Posted` now includes `Reversed`. |
| High | **First posting in a new month would fail**: Npgsql 6+ refuses to write a `DateTime` of kind Unspecified (`new DateTime(y, m, 1)`, or any date-only JSON value) to `timestamp with time zone`, and no legacy-timestamp switch was set. (Documented Npgsql behaviour; not reproduced in the environment these changes were made in.) | Every `DateTime` is normalised to UTC in the DbContext. |
| High | **The balance sheet could not balance from the second year**: only current-calendar-year profit reached equity, and there is no year-end close. | Prior-period and current-year (fiscal-year-aware) unclosed profit are both shown; the sheet balances whether or not closing entries exist. |
| Medium | **Date windows dropped same-day entries** (`EntryDate <= asOf` with a midnight `asOf`), including a reversal posted "now". | As-of and period-end dates include the whole day. |
| Medium | Posting and the document update were separate commits in every module (invoice posted, status not saved, can be posted twice). | One database transaction per mutating request; a 4xx/5xx result rolls back partial writes. |
| Medium | Running account balances used read-modify-write (lost updates). | Atomic `UPDATE … SET balance = balance + x` in a fixed account order. |
| Medium | JWT claim mapping: depending on handler settings `sub` is remapped to a long URI name, which would break `UserId`. | Claim mapping disabled; code also falls back to the mapped name. |
| Medium | Workflow approvals: any user could approve any request, including their own; tier role not checked. | Assigned approver / tier role required; no self-approval; one person cannot approve two tiers. |
| Low | Bank import could match one system transaction to several statement lines; ambiguous matches were guessed. | Already-matched rows are excluded; ties go to manual review. |
| Low | `.github/workflows/build.yml` was documented but did not exist; `bin/`, `obj/` and `tsconfig.tsbuildinfo` were committed; Docker passed `NEXT_PUBLIC_API_URL` at run time though Next.js needs it at build time; the database port was open to the network. | Workflow added; `.gitignore` added; build argument added; port bound to 127.0.0.1. |

### `IgnoreQueryFilters()` review

The audit asked for every call to be reviewed. There are 46 call sites in 14 files; each one is either cross-tenant by design or filters by company explicitly:

| Purpose | Sites | Verdict |
|---|---|---|
| Sign-in and e-mail uniqueness (`AuthService`, admin user creation) | 3 | Cross-tenant by design (the e-mail is the sign-in key); the duplicate-account ambiguity is closed. |
| Posting engine, exception queue, group scope, notes | 17 | Explicit `CompanyId ==` on every query. |
| Document numbering (AR, AP, PO, GRN, sales orders, work orders, tenders, verification batches) | 10 | Explicit company filter; now serialised by a lock. |
| Inter-company and consolidation | 16 | Cross-company by design. The exposure was in how they were *reached*: the group controller let any user choose which companies took part. That is closed by group scoping and consent. |

## Behaviour changes

These are deliberate and worth telling users and integrators about.

- **`POST /gl/journal-entries` now creates a Draft** instead of posting immediately. Use `submit` → `approve` → `post`. In Development one user may do all steps; in production the preparer cannot approve their own journal.
- **Every endpoint needs a permission.** Until other roles are defined only the Administrator role (`*`) can do anything. Custom roles created earlier used keys that no longer exist.
- **Reports change for books that contain reversals**: reversed originals now appear (and offset), so affected balances are corrected, not new.
- `POST /ar/invoices/{id}/post` returns `{ invoice, interCompany }` instead of the invoice alone.
- Login/register results include `permissions`. Sessions saved by the old app have none, so users are asked to sign in again once.
- Passwords must be at least 10 characters.
- Posting to a deactivated account is refused (reversals excepted).
- The API will not start in Production without the settings in the README's *Configuration and Secrets*.

## Deploying this change

1. **Apply the changes** (patch or files) and review them.
2. **Stop tracking build output** (it is now git-ignored; this only removes it from the index):
   `git rm -r --cached backend/src/ApexERP.API/bin backend/src/ApexERP.API/obj backend/src/ApexERP.Application/bin backend/src/ApexERP.Application/obj backend/src/ApexERP.Domain/bin backend/src/ApexERP.Domain/obj backend/src/ApexERP.Infrastructure/bin backend/src/ApexERP.Infrastructure/obj frontend/tsconfig.tsbuildinfo`
3. **Existing database only:** run the four pre-check queries in the header of `20261024120000_AuditHardeningPhase1And2.cs` (duplicate periods, double reversals, double mirrors, invalid journal lines). Fix anything they return before the migration runs, or it cannot create the matching index or constraint.
4. **Set the configuration**: a strong `Jwt__Secret`, the production connection string, `Frontend__Origin`, and `NEXT_PUBLIC_API_URL` for the web build.
5. **Review roles**: create the roles your people actually need (see *Roles and Permissions*). An approver and a poster must be different people from the preparer unless you accept the reduced control.
6. **Run the tests** (below) before anything else.

## How it was verified, and what was not

The environment these changes were made in could not reach NuGet, so the packages EF Core, Npgsql, JwtBearer and xUnit were unavailable. That limits what was proven.

**Verified**

- **Backend compiles.** The Domain, Application, DbContext and API layers (and the hand-written migration) compile against the real .NET 8 / ASP.NET Core 8 framework, with signature-faithful stand-ins for the unavailable packages. The original, unmodified code compiled against the same stand-ins first, to show the stand-ins do not invent errors.
- **188 unit tests pass.** The repository's own test files were run through a minimal xUnit-compatible runner. They cover permissions (including privilege escalation), startup configuration, journal validation, the state machine and segregation of duties, numbering, reporting dates, the fiscal calendar, group-structure rules, password policy, the authorization handler and policy provider, the transaction policy, and a **guard that builds the real endpoint list and fails if any action is reachable without a permission**. That guard was mutation-checked: disabling the convention makes it fail and list exactly the open endpoints.
- **PostgreSQL 16, real server:**
  - the SQL equivalent of the migration's `Up()` and `Down()` operations applies and round-trips against tables rebuilt from the original `InitialCreate`;
  - before the migration the schema accepts negative amounts, two-sided lines, zero or negative FX rates, a second reversal, duplicate periods and double mirrors; after it, all are rejected while valid data (including zero/zero lines) is still accepted;
  - a SQL replica of the numbering algorithm, run by 8 concurrent clients × 25 postings: the **old approach lost 174 of 200 postings** to duplicate numbers, while the transaction-lock approach saved all 200 with unique, gapless numbers;
  - the period lock makes a close wait for an in-flight posting (about 1.5 s) without blocking other periods;
  - the atomic balance update ended at exactly 200 where the old read-modify-write pattern ended at 27.
- **Frontend:** type-check, 45 unit tests, and a full production build (25 routes) pass. The session test that covers the audit's crash was mutation-checked against the original unguarded `JSON.parse` and fails there. The Vercel guard was exercised directly.

**Not verified: please run these**

1. `dotnet build` and `dotnet test backend/tests/ApexERP.Tests` with the real packages.
2. **The PostgreSQL integration tests** (`APEX_TEST_DB=… dotnet test`). They were written and type-checked but never executed, and they are the check on everything that depends on how EF Core really behaves: query translation (`StartsWith`, correlated `Sum`, `ExecuteUpdate`), savepoints, the immutability guard, the report corrections and the inter-company paths. **This is the single most valuable next step.**
3. `dotnet ef migrations add Drift` — it should generate an empty migration (delete it afterwards). The migration and model snapshot were edited by hand to match the model; this confirms they agree.
4. `./scripts/smoke-test.sh` against `docker compose up`, a click-through of the UI (journal workflow, exceptions page, group page, permission-based navigation), and a deployment on your host.
5. GitHub Actions (the workflow was syntax-checked only).

## Not done yet

Audit Phase 3 and 4 items that are features rather than fixes, plus gaps noticed on the way:

- Refresh-token **rotation with revocation**, two-factor authentication, login rate limiting and lockout. (Refresh tokens are no longer usable as access tokens, but there is still no refresh endpoint: sessions end after 15 minutes and the user signs in again.)
- Tokens are held in `localStorage`; HttpOnly cookies would reduce exposure to script injection.
- Month-end close checklist, a reconciliation exception workflow with approvals, three-way-match controls in procure-to-pay, approval-chain integration for journals, and scheduled jobs (payroll, depreciation, FDMS retries).
- Idempotency keys on the API; an outbox for integration events.
- Many endpoints still bind domain entities straight from the request body (the new and rewritten ones use request records). Replace with request DTOs to remove over-posting.
- PostgreSQL row-level security as a second barrier behind the EF filters; a tenant write-guard for cross-company services.
- Cross-company posting in inter-company netting is one-sided by design (the initiating company posts both legs); it should require the counterparty's confirmation.
- PAYE bands in `seed.sql` are illustrative and must be verified against the current ZIMRA notice before real payroll.
- Per-company (rather than per-deployment) journal-approval policy and a role editor in the UI.
