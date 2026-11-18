# ADR-0004: Server-side sessions in Azure SQL (`app.Sessions`) with a small custom express-session store

- **Status:** Proposed
- **Date:** 2026-11-18
- **One-way door:** No. The store is behind the `express-session` `Store` interface and can be swapped for Redis in a day.

## Context
- The session holds the identity used for RLS (`userId`, `role`, `teamId`), the CSRF token, and the transient MSAL `state`, `nonce` and PKCE verifier.
- App Service can run more than one instance and restarts on deploy, so in-memory sessions are not acceptable in production.
- The plan rules out Redis to keep cost and moving parts down.
- Sessions must **not** be subject to RLS:
  - They are read before any identity context exists.
  - They must never be readable through the RLS connection pool.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| **A. Custom `Store` (about 100 lines) over `systemPool`, table `app.Sessions`** | No new dependency. Uses our `mssql@12` and managed identity config. Needs only DML grants on one table. Easy to unit-test. | We own a little code: get/set/destroy/touch plus expiry cleanup. |
| B. `connect-mssql-v2` | Ready-made | Depends on `mssql` ^11, giving a second driver copy with separate auth config. Its README recommends `db_ddladmin`, which breaks least privilege. Single maintainer. |
| C. Azure Cache for Redis + `connect-redis` | Fast, standard | Extra paid resource (at least about 15 EUR/month), plus network rules and secrets. Not justified at this scale. |
| D. Cookie sessions (signed and encrypted cookie) | Stateless | Cannot revoke a session server-side. Size limits. Larger blast radius if the secret leaks. |
| E. Table in schema `crm` | One schema | Sits next to the RLS-protected tables and invites someone to add it to a policy. A separate `app` schema keeps the boundary obvious. |

## Decision
Option A, with the table `app.Sessions (Sid varchar(128) PK, Data nvarchar(max), ExpiresAt datetime2(0), IX on ExpiresAt)`. DDL is in overview §4.1.

- **Store operations (`src/auth/sessionStore.ts`, extends `session.Store`):**
  - `get`: `SELECT Data FROM app.Sessions WHERE Sid=@sid AND ExpiresAt > SYSUTCDATETIME()`.
  - `set`: `MERGE` / upsert with the expiry from `cookie.expires`, or idle minutes.
  - `destroy`: delete the row.
  - `touch`: update `ExpiresAt` only.
- **Cleanup:** every 15 min, on each instance, `DELETE TOP (1000) FROM app.Sessions WHERE ExpiresAt < SYSUTCDATETIME()`, repeated while rows are affected.
- **Pool:** runs on `systemPool`, never on `rlsPool`. No session context is set.
- **Grants:** `crm_app_role` gets SELECT, INSERT, UPDATE and DELETE on `app.Sessions` only.
- **`express-session` options:**
  - `name: '__Host-crm.sid'`
  - `secret`: `SESSION_SECRET`, split on commas for rotation.
  - `resave: false`, `saveUninitialized: false`, `rolling: true`
  - `cookie: { secure: true, httpOnly: true, sameSite: 'lax', path: '/', maxAge: SESSION_IDLE_MINUTES }`
  - `app.set('trust proxy', 1)`
  - Absolute timeout: middleware compares `session.user.authAt` with `SESSION_ABSOLUTE_HOURS`, then destroys the session and redirects to "Your session has ended".
- **Contents:** keep them minimal: `user {userId, oid, name, email, role, teamId, authAt}`, `csrfToken`, transient `auth {state, nonce, pkceVerifier, returnTo}` (deleted after the callback) and flash messages. **No tokens.**
- **Local dev and test:** `MemoryStore` is allowed only when `NODE_ENV !== 'production'`. Config validation enforces this.

## Consequences
- **Cost:** no new resources.
- **Load:** one extra read per request, and one write when `rolling` touches the session. Trivial at this scale.
- **Revocation:** a session can be revoked by deleting its row (runbook: "force sign-out of user X" means deleting rows whose `Data` contains their `oid`).
- **Personal data:** `Data` holds name and email for up to 10 h. Rows are removed on expiry. Logs never contain `Data`.
- **Risk: serverless SQL auto-pause in dev makes the first request slow.** It is acceptable in dev. Auto-pause is off in prod.
- **Reversibility:** high. Only the `store:` option changes.
