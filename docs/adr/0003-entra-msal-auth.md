# ADR-0003: Authentication with Microsoft Entra ID (MSAL Node, auth-code + PKCE), roles from app roles, teams from groups

- **Status:** Accepted (user-approved 2026-11-18).
- **Library partly superseded by [ADR-0007](0007-dotnet-10-rewrite.md) (2026-11-18):** `@azure/msal-node` is replaced by Microsoft.Identity.Web with the same flow (auth-code + PKCE, `response_mode=query`, callback `/auth/redirect`), role, team and overage rules.
  - Entra ID, MSAL auth-code + PKCE, and app roles `CRM.Admin`, `CRM.Manager` and `CRM.User` (product owner decision in the plan).
  - Team mapping via Entra security groups (option T1) and the Admin "Add team" form (user decision, 2nd round, 2026-11-18).
- **Date:** 2026-11-18
- **One-way door:**
  - Entra ID as the identity provider is effectively one-way, because user IDs (`oid`) are stored as the identity key.
  - Team mapping is reversible.

## Context
- Users are employees in one Entra tenant.
- CLAUDE.md: never build our own login.
- Roles must be managed centrally in Entra, not edited in the app (UI spec, assumption A4).
- RLS needs a stable `UserId`, a `Role` and a `TeamId` per request (ADR-0002).
- Team membership is not defined in the plan. The UI spec says "Roles and membership are managed in Microsoft Entra ID".

## Options considered
**Protocol and library**
| Option | Pros | Cons |
|---|---|---|
| **A. `@azure/msal-node` confidential client, auth-code + PKCE, server-side session** | Microsoft-supported. Tokens never reach the browser. PKCE plus a client secret protects code redemption. | Needs a client secret, stored in Key Vault, with rotation. |
| B. App Service Authentication ("Easy Auth") | No code | Less control over roles and groups mapping, the session and local dev. Behaviour is harder to test locally. |
| C. passport-azure-ad | Familiar | Deprecated and archived by Microsoft. |

**Role source:** Entra **app roles** in the `roles` claim (chosen), or group IDs mapped to roles. App roles are named and scoped to the app, and admins can assign groups to them.

**Team source** (decided: T1)
| Option | Pros | Cons |
|---|---|---|
| **T1. Entra security group per team, mapped in `crm.Teams.EntraGroupId`; groups claim limited to "groups assigned to the application"** | Membership is managed in Entra, as the UI spec states. No overage for normal tenants. Read at every login. | Admin must register each team's group object ID once. A user must be in exactly one team group; a user in several gets no team (fail closed), a logged warning and a UI badge until Entra is fixed. |
| T2. Admin assigns a team per user in the CRM | Simple UI | Contradicts the UI spec (users and teams are read-only, managed in Entra). A second place to maintain membership. |
| T3. Entra `department` attribute | No groups needed | Free text, often inconsistent. Not reliably in tokens. |

## Decision
- **Library:** `@azure/msal-node` `ConfidentialClientApplication`.
  - Authority is `https://login.microsoftonline.com/{ENTRA_TENANT_ID}` (single tenant).
  - Scopes are `openid profile email`.
  - `response_mode=query`, so the callback is a top-level GET and the `SameSite=Lax` session cookie is sent.
- **Login:** `/auth/login`:
  - Generates the PKCE verifier and challenge (S256), plus `state` and `nonce` (32 random bytes each).
  - Stores them, and a validated `returnTo`, in the session.
- **Callback:** `/auth/redirect`:
  1. Compares `state` in constant time.
  2. Calls `acquireTokenByCode` with the verifier.
  3. Checks `tid` equals the configured tenant, and `nonce` matches.
  4. Maps the role: Admin beats Manager beats User. No role means `/no-access`.
  5. Resolves the team (T1).
  6. Calls `crm.usp_UpsertUserOnLogin`.
  7. Calls `session.regenerate()` to prevent fixation, then stores `{userId, oid, name, email, role, teamId, authAt}` and a new CSRF token.
- **Tokens:** access, ID and refresh tokens are **not** persisted. The app calls no downstream APIs, and the MSAL token cache is in memory and discarded.
- **Signout:** POST `/auth/signout` with CSRF. It destroys the session, then redirects to the Entra logout endpoint with `post_logout_redirect_uri=/signed-out`.
- **Role and team freshness:** both are captured at login. Changes in Entra take effect at the next sign-in, at the latest after the 10 h absolute session timeout. This is acceptable for an internal CRM. An Admin who must cut access immediately disables the user in Entra and deletes their rows from `app.Sessions` (runbook).
- **Client credential:** a client secret in Key Vault, referenced from App Service settings. Hardening option for later: replace the secret with a federated identity credential using the web app's managed identity (MSAL `clientAssertion`), which removes the secret entirely.
- **Local and test:**
  - Developers use a separate "CRM (dev)" app registration with a localhost redirect URI.
  - Playwright uses `/__test/login`, which only exists when `NODE_ENV=test` and the app is not on App Service. Startup fails if `AUTH_TEST_BYPASS` is set in any other mode.
- **Team mapping:** option T1, approved by the user 2026-11-18.
  - **Teams are Entra security groups**, one group per team, stored as `crm.Teams.EntraGroupId` (unique).
  - **Groups claim** is configured as "Groups assigned to the application" (ID token, group IDs). Each team group must be assigned to the enterprise app.
  - **Resolution at login:** `usp_UpsertUserOnLogin` intersects the `groups` claim with active `crm.Teams.EntraGroupId` values and stores the result as `crm.Users.TeamId`. No match gives NULL. **A user must be in exactly one team group** (user decision 2026-11-18): more than one match gives `TeamId` NULL (fail closed: own records only), a logged warning with the `oid`, and `crm.Users.TeamStatus = 'multiple'`, which shows the warning badge on Settings > Users & teams. No "pick one" rule is applied, so team membership can never be guessed.
  - **Overage** (`_claim_names` contains `groups`, or `hasgroups` is true): no Graph call. `TeamId` is NULL (fail closed: own records only) and an **error** is logged with the user's `oid`.
  - **Admin "Add team" form** on Settings > Users & teams: team name and Entra group object ID (GUID, unique). Writes are Admin-only (route check plus `fn_AdminWrite`) and audited. Users and memberships stay read-only in the CRM.

## Consequences
- **Easier:**
  - Central access management.
  - MFA and Conditional Access come from the tenant for free.
  - Leaver handling: disable the user in Entra.
- **Harder:**
  - Secret rotation every 12 months; this goes in the runbook and a calendar reminder.
  - Each environment needs its own app registration.
- **Risks:**
  - Groups claim overage: mitigated by "groups assigned to the application" (overage only if a user is in more than 200 groups assigned to the app). On overage, `TeamId` is NULL (fail closed) and the app logs an error.
  - Wrong group object ID typed into "Add team": the team simply gets no members at next sign-in. Mitigation: the Users & teams page shows a member count per team, so an empty team is visible.
  - Stale role in a long session: bounded by the 10 h absolute timeout.
  - Open redirect via `returnTo`: only relative paths starting with a single `/` are accepted.
- **Reversibility:**
  - The team source can change (T1 to T2) with a migration and a UI change.
  - The identity provider cannot change cheaply.
