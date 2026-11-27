# Enterprise multi-tenant platform

## Product model

- **Tenant**: the independent customer organisation and commercial/security boundary.
- **Company**: a legal entity owned by one tenant. A tenant can own multiple companies.
- **Branch**: an operating location or organisational unit inside one company.
- **Department and cost centre**: existing ERP structures can now be assigned to a branch.
- **Company membership**: explicit user-to-company authorisation, including a company-specific role. A user can hold memberships in multiple companies within the same tenant.

A government department may be provisioned as an independent tenant or as a company within a wider government tenant. No automatic legacy company-to-tenant mapping is performed.

## Application capabilities

### Tenant onboarding
- Public self-registration creates a tenant, first company, administrator, primary membership, onboarding record and a 30-day trial subscription.
- Platform-assisted provisioning creates the same foundation and issues a 48-hour, one-time initial-administrator invitation.
- Initial administrator and company-user invitations store only SHA-256 token hashes. Tokens are returned once to the inviter only when SMTP delivery is not configured or fails.
- Invitation acceptance validates expiry, email, account uniqueness, tenant activity, subscription status and available seat entitlement before creating a user.
- The onboarding wizard tracks completion and validates that every active company has a primary jurisdiction, a deployment profile is selected, and an active administrator membership exists before marking onboarding complete.

### Organisational structure
- Legal companies can be created beneath an existing tenant with optional parent-company ownership information.
- Company roles are cloned from the current company; the creating administrator receives a company-specific membership.
- Company membership supports adding existing same-tenant users, expiring email invitations, reactivation, deactivation, role assignment and secure company switching.
- Access tokens and refresh-token rotation preserve the selected company. The API checks membership again on every switch/refresh.
- Branches are company-scoped and support parent-child hierarchy. Departments and cost centres have branch assignments.
- Company, membership, branch and invitation records have tenant-aware filters and database referential constraints.

### Subscription and licensing
- Plans define module codes, maximum seats, maximum companies, currency and price metadata.
- Tenant subscriptions support trial, active, suspended and cancelled lifecycle states, period end dates, plan changes and seat/company overrides.
- The API enforces subscription state and explicit module entitlements on tenant module routes.
- Company and seat usage are checked when provisioning companies, adding members and accepting invitations. Existing over-limit tenants retain access to organisation administration to remediate usage.
- Tenant users can view current subscription/usage, inspect public plans and submit a renewal request.
- A renewal request becomes a lifecycle job for the configured operations/billing worker. Actual payment collection requires a separately configured billing provider.

### Jurisdiction and deployment profiles
- Jurisdiction profiles contain country code, currency, time-zone and reporting-framework defaults. The initial catalogue includes Zimbabwe and selected regional/global markets; platform administrators can add further countries.
- Tenant jurisdiction records are associated with individual legal companies, allowing one tenant to operate in multiple countries and multiple companies in the same country.
- Each company can configure functional currency, fiscal-year start, reporting framework and time zone.
- Tax configuration remains pending statutory review until legally verified, effective-dated country packs are supplied. No tax rate or statutory obligation is inferred from a country default.
- Seeded deployment templates cover commercial SMEs, commercial enterprises, MDAs, SOEs, NGOs/development organisations and multinational groups. Applying a template sets the sector profile; it does not override accounting basis or fabricate statutory settings.

### Platform control plane
- Platform administration uses the separate `PlatformBearer` JWT scheme, signed with an RSA private key held by the platform identity issuer. The API validates only the configured RSA public key.
- Tenant `Administrator` or `SUPER_ADMIN` roles do not grant platform access.
- Platform endpoints manage tenants, plans, subscriptions, jurisdiction catalogue, deployment templates, operational checks, SLA policies/incidents, support grants and lifecycle jobs.
- Support grants are reason-coded, time-limited and require approval by a different platform operator. Approved support diagnostics access is audited.

### Customer operations and disaster recovery
- Tenant operational checks capture status, duration and diagnostic metadata.
- SLA policies define availability, response and resolution targets. SLA reporting computes availability over the configured monitoring window and reports response/resolution breaches for incidents. Response and resolution clocks use the configured local business-hours window; `BusinessHoursJson` accepts `{"start":"09:00","end":"17:00","days":[1,2,3,4,5]}` where days are `DayOfWeek` values (Sunday=0). An empty object means 24/7.
- Lifecycle jobs cover provisioning, diagnostics, backup, restore, export, offboarding and subscription renewal.
- A background dispatcher sends HMAC-SHA256 signed jobs to a configured external operations worker. The worker must send timestamped HMAC-signed callbacks; signatures are verified with a five-minute tolerance and constant-time comparison.
- The API stores job metadata and results only. Backup payloads, database credentials and cloud secrets remain with the operations worker.

## Key API routes

### Tenant routes (normal tenant JWT)
- `GET /api/v1/auth/companies`
- `POST /api/v1/auth/switch-company`
- `POST /api/v1/auth/accept-company-invitation`
- `GET /api/v1/organisation/companies`
- `POST /api/v1/organisation/companies`
- `PUT /api/v1/organisation/companies/{companyId}/deactivate`
- `GET|POST /api/v1/organisation/companies/{companyId}/branches`
- `GET|POST /api/v1/organisation/companies/{companyId}/members`
- `POST /api/v1/organisation/companies/{companyId}/invitations`
- `PUT /api/v1/organisation/members/{membershipId}/deactivate`
- `PUT /api/v1/organisation/companies/{companyId}/departments/{departmentId}/branch`
- `PUT /api/v1/organisation/companies/{companyId}/cost-centres/{costCentreId}/branch`
- `GET|PUT /api/v1/organisation/jurisdictions`
- `GET /api/v1/organisation/deployment-profiles`
- `PUT /api/v1/organisation/onboarding`
- `GET /api/v1/organisation/subscription`
- `GET /api/v1/organisation/subscription/plans`
- `POST /api/v1/organisation/subscription/renewal-request`

### Platform routes (separate RSA-signed PlatformBearer JWT)
- `GET|POST /api/platform/plans`, `PUT /api/platform/plans/{planId}`
- `GET /api/platform/overview`, `GET /api/platform/tenants`
- `POST /api/platform/tenants`, `GET /api/platform/tenants/{tenantId}`
- `POST /api/platform/tenants/{tenantId}/subscriptions/{subscriptionId}/lifecycle`
- `GET|POST /api/platform/jurisdictions`
- `GET|POST /api/platform/deployment-profiles`
- `POST /api/platform/tenants/{tenantId}/support-grants`
- `POST /api/platform/support-grants/{grantId}/approve|revoke`
- `GET /api/platform/support-grants/{grantId}/diagnostics`
- `POST /api/platform/tenants/{tenantId}/operational-checks`
- `GET /api/platform/tenants/{tenantId}/operational-checks`
- `GET /api/platform/tenants/{tenantId}/sla`
- `PUT /api/platform/tenants/{tenantId}/sla-policy`
- `POST /api/platform/tenants/{tenantId}/sla-incidents`
- `PUT /api/platform/sla-incidents/{incidentId}`
- `POST /api/platform/tenants/{tenantId}/lifecycle-jobs`
- `GET /api/platform/tenants/{tenantId}/lifecycle-jobs`
- `PUT /api/platform/lifecycle-jobs/{jobId}`

The worker callback is `POST /api/platform/lifecycle-jobs/{jobId}/callback`; it is anonymous at the HTTP layer but requires the configured timestamped HMAC signature.

## Configuration

### Platform identity
Set:
- `PLATFORM_JWT_ISSUER` (default `ApexERP.Platform`)
- `PLATFORM_JWT_AUDIENCE` (default `ApexERP.Platform.Api`)
- `PLATFORM_JWT_PUBLIC_KEY_PEM` (RSA public key PEM)

The platform identity issuer must sign platform access tokens with the corresponding private key and include `platform_admin=true` and a stable `sub` claim. The private key must never be placed in this repository or in the ERP API environment.

### Invitation delivery
Configure `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM` and `SMTP_ENABLE_SSL`. Without SMTP, invitation tokens are returned once to the authorised inviter for controlled delivery.

### Operations worker
Configure `OPERATIONS_WORKER_URL` and `OPERATIONS_WORKER_SECRET`. The worker receives JSON containing `jobId`, `tenantId`, `jobType`, `requestedBy` and `requestedAt`, with:
- `X-Apex-Timestamp`: Unix epoch seconds
- `X-Apex-Signature`: uppercase hex HMAC-SHA256 of `timestamp + newline + raw request body`

The callback uses the same headers and secret and accepts `status` (`Running`, `Succeeded`, `Failed`), optional `externalJobReference` and `resultSummary`.

## Database and deployment safety

- The migration adds platform tables, company membership, branch assignments, SLA tables, referential constraints, a default trial plan and sector/jurisdiction catalogues.
- Existing users receive a default membership for their already-known primary company. This does not infer or modify company-to-tenant ownership.
- No legacy company tenant assignment or accounting transaction is backfilled.
- Apply migrations using the dedicated migration identity. Do not enable PostgreSQL RLS until the approved legacy company-to-tenant mapping and backfill have been completed.
- When RLS is enabled, the API stamps tenant, company and user identity into the PostgreSQL session. A user can read their own user identity in another company only when an active membership exists; database write checks remain company-scoped.
- The operations worker must use a separate least-privilege service identity and must independently validate job signatures and tenant identifiers.

## External dependencies and legal controls

The tenant workspace is available at `/organisation`; the separate platform control-plane workspace is at `/platform/admin` and accepts a short-lived token issued by the configured platform identity provider. The application side now provides the workflows and enforcement boundaries. The following require deployment-specific credentials, providers or authorised legal content:
- Platform identity issuer/private-key custody.
- SMTP relay.
- Billing/payment gateway and invoice/tax receipt integration.
- Operations worker, cloud backup storage, encryption keys, restore isolation and DR runbooks.
- Monitoring/probe agents and SLA evidence sources.
- Country-specific tax/statutory packs with official sources, effective dates, approval and version history.

A configured deployment must not claim payment processing, email delivery, backup completion, statutory compliance or SLA availability until the corresponding provider or evidence source has confirmed the operation.


## Runtime configuration

The Docker Compose API service maps the following deployment settings into ASP.NET Core configuration:

- `PLATFORM_JWT_ISSUER`, `PLATFORM_JWT_AUDIENCE`, `PLATFORM_JWT_PUBLIC_KEY_PEM`: trusted RSA platform-token validation. Store the issuer's private signing key outside this application; the API receives only the public key. PEM newlines may be represented as `\\n`.
- `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM`, `SMTP_ENABLE_SSL`: administrator and user invitation delivery.
- `OPERATIONS_WORKER_URL`, `OPERATIONS_WORKER_SECRET`: signed job dispatch and callback verification for the separately deployed operations worker.

The public invitation endpoint is rate-limited and consumes a token once. The tenant must remain active, its initial company must remain active, and the subscription must be within its valid period before the administrator account is created. Provider secrets must be supplied through the deployment secret manager, not committed to this repository.
