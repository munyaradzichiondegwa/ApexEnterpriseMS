# Operating ApexERP

This guide covers what a person running the system needs: first-time upgrade steps, configuration, backups and recovery, monitoring, and what to do when something needs attention.

## 1. Upgrading to this version (do these in order)

1. **Generate and commit the database migration.** The code now has new tables and columns (controls, automation, sign-in security). Until the migration is applied, sign-in fails because the `Users` table lacks the new lockout columns.
   ```powershell
   .\scripts\generate-migrations.ps1 -MigrationName ControlsAndAutomation     # Windows, uses Docker (no .NET SDK needed)
   ```
   ```bash
   ./scripts/generate-migrations.sh ControlsAndAutomation                     # macOS / Linux
   ```
   Open the generated file once. It should create the new tables and add columns; existing rows keep working (`IsPostable` defaults to true, `MatchStatus` to `NotRequired`). Commit it. The API applies migrations when it starts.
2. Run `cd frontend && npm install` (new dependency: `qrcode`, used to enrol authenticator apps).
3. Start the stack (`docker compose up --build`). Everyone must **sign in again** once: tokens are no longer kept in browser storage.
4. Sign in as an administrator and, under **Governance & Approvals**, set the approval limits, materiality thresholds and settings that suit the company (section 2). Under **Administration > Roles**, grant the new permissions to the right roles (section 3).
5. For production, follow `docker-compose.prod.yml` and sections 4 to 6.

## 2. Configuration reference

| Setting | Where | Default | Meaning |
|---|---|---|---|
| `Auth:RefreshTokenCookieOnly` | API | `true` | The refresh token lives only in an HttpOnly cookie and is not returned in the response body. |
| `Auth:CookieSameSite` | API | `Strict` | Use `None` only if the browser calls the API on a different site than the web app (not recommended; the web app proxies `/backend` so this is normally unnecessary). |
| `Security:TrustForwardedHeaders` | API | `false` | `true` only when all traffic comes through a proxy you control; then the real client IP is used for rate limiting and the audit trail. |
| `Security:RowLevelSecurity:Enabled` | API | `false` | Database-enforced company isolation. Read section 5 before enabling. |
| `Jobs:Enabled` | API | `true` | Background scheduler on or off for this instance. |
| `FDMS_DEVICE_KEY_PEM` | environment | unset | ZIMRA fiscal device private key. Unset = invoices are recorded as **not fiscalised**. |
| `NEXT_PUBLIC_API_URL`, `API_PROXY_TARGET` | web build | | Public API URL, and where the web server reaches the API to forward `/backend/*`. |
| `NEXT_PUBLIC_DIRECT_API` | web build | unset | `true` makes the browser call the API origin directly (then `Auth:CookieSameSite=None` and CORS credentials are required). |

**Per-company settings** (Governance > Settings; all optional, all audited):

| Key | Effect |
|---|---|
| `controls.allowSelfApproval` | `true` lets a preparer approve their own work. Leave off. |
| `ap.match.quantityTolerancePct`, `ap.match.priceTolerancePct` | Tolerance for the three-way match. |
| `ap.directPayment.maxAmount` | Payments above this must go through an authorised payment run. |
| `period.close.requireChecklist` | Every period needs its close checklist complete before it can be closed. |
| `gl.fx.rateTolerancePct`, `gl.fx.rateMaxAgeDays`, `gl.fx.roundingAccountId`, `gl.fx.roundingTolerance` | Foreign-currency rate validation and rounding-difference posting. Off until set. |
| `integrations.arControlAccountId` | Trade debtors account used when a provider payment becomes a receipt. |
| `recon.ap.accounts`, `recon.ar.accounts` (and `recon.<area>.accounts`) | Comma-separated control-account ids. If unset the reconciliation uses the account most used by that module and tells you which one. |

## 3. Permissions added

| Permission | Allows |
|---|---|
| `controls.approve` | Approve accruals, prepayments, contracts, impairments, stock write-downs, credit notes, ECL runs, petty cash |
| `ap.payments.authorise` | Authorise a payment run (must not be its preparer) |
| `ap.suppliers.verify-bank` | Verify a supplier bank-detail change |
| `controls.governance.manage` / `.approve` | Change settings, SoD rules, delegations, approval limits / approve materiality, policies, statutory rates |
| `controls.exceptions.review` | Independently review and close a control exception |
| `controls.reconciliation.signoff`, `controls.bank-rec.certify`, `controls.period.certify` | Sign off reconciliations, certify bank reconciliations, certify a period |
| `controls.evidence.manage` | Attach evidence |
| `integrations.manage` | Configure payment integrations and requeue dead letters |

The module permissions `automation.*`, `controls.*` and `integrations.*` also exist (read/write). Segregation-of-duties conflicts between these are checked when a role is created (header `X-Accept-SoD-Risk` records a justified exception).

## 4. Backups and recovery

* **Backups:** `scripts/backup.sh` (daily via the `backup` service in `docker-compose.prod.yml`). Compressed, optionally AES-256 encrypted, checksummed, pruned after `BACKUP_RETENTION_DAYS`. **Copy backups off the machine** (`BACKUP_COPY_CMD`, for example `rclone copy {} remote:bucket`). A backup on the same disk as the database is not a backup.
* **Prove it restores:** run `scripts/restore-test.sh` at least weekly (CI runs it too). It restores into a scratch database, verifies the checksum, confirms the ledger still balances, reports the backup's age and the restore time, and exits non-zero on any failure.
* **Targets** (adjust to the business; the numbers below are what the default setup achieves):
  * RPO (data you can lose): up to 24 hours with the daily backup. For less, run `backup.sh` more often or enable PostgreSQL WAL archiving for point-in-time recovery.
  * RTO (time to be back): the restore time printed by `restore-test.sh` plus start-up; measure it at your real data size and write the figure here: ______.
* **Recovering:** stop the API; `pg_restore --clean --if-exists --no-owner -d apexerp <backup>` (decrypt first with `openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass env:BACKUP_PASSPHRASE`); start the API (it applies any newer migrations); run Audit > Control self-tests and the Reconciliation workspace.
* Keep the **backup passphrase and database password in a password manager or secrets vault, not on the server**.

## 5. Row-level security (optional hardening)

The API already filters every query by company. Row-level security makes **PostgreSQL itself** refuse other companies' rows even if code forgot a filter. To enable:

1. Take a backup. Deploy this version with `Security__RowLevelSecurity__Enabled=true` (`RLS_ENABLED=true`).
2. Run `psql -d apexerp -f backend/database/hardening/002_row_level_security.sql` as the table owner. (Undo: `002_row_level_security_disable.sql`.)
3. Confirm sign-in, a few screens, and the nightly jobs. Code that legitimately reads across companies (group consolidation, inter-company postings, the e-mail uniqueness check) runs inside an explicit `CrossTenantScope` and keeps working.
4. The database account the API uses **must not** be a superuser or have `BYPASSRLS`. Use a separate account for backups.

Enable the API flag first, then the policies; disable in the reverse order.

## 6. Monitoring and alerts

| Check | How | Alert when |
|---|---|---|
| API alive and database reachable | `GET /health/ready` (the compose health check uses it) | non-200 for 2 minutes |
| Backup freshness and restore | `scripts/restore-test.sh` exit code | any failure |
| Failed jobs | Integrations & Jobs > Background jobs; dashboard "job failures" | any failure in 24 h |
| Dead-lettered payment messages | Integrations > Messages, dashboard | any |
| Control exceptions | Dashboard, Audit > Exceptions | any critical, or overdue |
| Failing integrity tests | Audit > Control self-tests (runs nightly) | any failure |
| Sign-in abuse | Audit log actions `Auth.AccountLocked`, `Auth.RefreshReuseDetected`; HTTP 429 counts | spikes |
| Every request | `X-Correlation-Id` is returned and appears on every log line | use it to follow one request through the logs |

Logs go to the container's standard output (rotated by `docker-compose.prod.yml`); ship them to your log service. Build alerts on the audit-log actions above and on `health/ready`; the application surfaces the rest on the dashboard.

## 7. Runbooks

* **A user is locked out.** Administration > Users > Unlock (audited). Lockout is 5 failed attempts, then 5, 10, 20, 40, 60 minutes.
* **A user lost their phone.** Use a recovery code; if none, an administrator resets MFA (Governance, audited) and the user re-enrols under Account Security.
* **A suspected stolen session.** `Auth.RefreshReuseDetected` already ended that session family. Deactivating a user revokes all their sessions at once.
* **A job failed.** Integrations & Jobs > Background jobs shows the message. Fix the cause, then "Run due jobs now": a failed run retries up to three times, and a run that succeeded is never repeated.
* **A payment message is dead-lettered.** Read the problem (usually: invoice number not found, currency mismatch, no settlement account), fix it, then Try again.
* **Period will not close.** Period close shows which step blocks it. Blocked automatic steps resolve themselves when their condition is met; waivers need a written reason and are audited.
* **A reconciliation shows a difference.** Explain it (at least 15 characters) so a second person can review and sign off; it is never hidden.

## 8. Things that need your input (cannot be built without them)

* **ZIMRA fiscalisation.** Signing, canonical payloads, QR and the retry/72-hour rules are built. Submitting to ZIMRA needs the registered device, its key (`FDMS_DEVICE_KEY_PEM`), and ZIMRA's current API specification and certificate: implement `IFdmsGateway` and register it. Until then every invoice is honestly recorded as not fiscalised.
* **Provider-specific payment formats.** The webhook pipeline (signature check, idempotency, receipt posting, retry, dead letters, reconciliation) is built around a signed-JSON envelope. If EcoCash, OneMoney, PayNow, ZimSwitch or a bank sends a different format or signature scheme, add one `IPaymentProviderAdapter` class for it once you have their specification and sandbox credentials. Initiating payments (outbound) needs the providers' APIs as well.
* **Statutory rates and loss rates.** The product does not decide these for you. Enter NSSA/AIDS-levy rates (with the notice they come from) under Governance > Statutory rates, and the credit-loss matrix from your own history under Receivables controls. The figures shown on screen are examples only.
* **Hosting.** `docker-compose.prod.yml` and `.github/workflows/deploy.yml` assume a Docker host reachable by SSH and GitHub Environments named `staging` and `production` (add required reviewers to `production`). Offsite backup storage, TLS and DNS are yours to provide.
