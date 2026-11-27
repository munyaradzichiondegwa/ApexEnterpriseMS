# Enterprise platform foundation

This change extends the existing CustomerOrganisation (tenant) and Company (legal entity) model. It does not replace either identity.

## Included in this implementation

- Company-level branches and explicit user/company membership records. Both are company-scoped and receive the existing fail-closed EF tenant filters. Membership uniqueness prevents duplicate active relationship rows for a company/user pair.
- Subscription plan catalogue, tenant subscription lifecycle state, seat/company limits and module entitlement catalogue data.
- Country jurisdiction reference profiles and tenant-selected jurisdiction settings for currency, fiscal-year start, reporting framework and reviewable tax configuration.
- Reusable deployment profile records for commercial, MDA, SOE, NGO and multinational templates. Templates are configuration data; they do not assert that statutory rules are legally current.
- Tenant onboarding lifecycle records and one-time administrator invitation records. Invitation tokens are returned once to the authorised platform operator; only SHA-256 hashes are persisted. The initial administrator must still complete a separately implemented acceptance/password-creation flow.
- A separate platform control-plane API guarded by the `PlatformAdmin` policy and `platform_admin=true` claim. Tenant `SUPER_ADMIN` is not sufficient.
- Platform audit events, time-bounded support grants, operational check records and lifecycle-job metadata for future provider-backed diagnostics and disaster-recovery orchestration.

## Control-plane endpoints

All endpoints below require an authenticated token issued by the trusted platform identity with `platform_admin=true`. The existing tenant login does not issue this claim. Do not add it to tenant roles or ordinary user tokens.

- `GET /api/platform/plans`
- `POST /api/platform/plans`
- `POST /api/platform/tenants` — atomically creates a tenant, first legal company, subscription, onboarding record and hashed administrator invitation.
- `GET /api/platform/tenants/{tenantId}`
- `POST /api/platform/tenants/{tenantId}/subscriptions/{subscriptionId}/lifecycle` — Active, Suspended or Cancelled.

The provisioning endpoint returns the one-time invitation token to the platform operator. A configured email provider must deliver it to the named administrator. The token acceptance flow, administrator user creation, password setup and MFA enrolment remain required before self-service onboarding is complete.

## Important implementation boundaries

- The schema migration creates new tables only. It does not assign any existing company to a tenant and does not alter accounting records.
- Existing nullable legacy `Companies.TenantId` values remain untouched until an approved mapping register and controlled backfill are supplied.
- Subscription state and limits are persisted, but enforcing entitlements across every module endpoint and background job is not complete in this change. Do not treat a plan record alone as an enforcement boundary.
- Country profiles contain defaults only. Tax rules, statutory packs and effective dates require verified jurisdiction-specific legal sources and controlled updates.
- Operational checks, support grants and lifecycle jobs are records/contracts. Live SLA probes, cloud backup/restore execution, support-session impersonation, billing collection and email delivery require provider adapters, secrets, approval workflows and operational runbooks.
- Platform identity issuance and key separation must be completed in the deployment identity provider. The claim is accepted only after the existing JWT signature, issuer, audience and lifetime validation succeeds.

## Deployment

Apply the EF migration using the deployment migration identity. Do not enable RLS until the tenant assignment backfill is complete and the RLS rollout has been approved. Run the full CI pipeline, including the migration drift check, before merging.
