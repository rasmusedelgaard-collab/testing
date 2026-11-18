# ADR-0006: Shared access code + seeded test users on the interim test site (exception to "never build our own login")

- **Status:** Accepted (user-approved 2026-11-18), scoped exception, time-limited.
- **Implementation note (2026-11-18):** in the .NET rewrite ([ADR-0007](0007-dotnet-10-rewrite.md)) the same rules and guards apply; the go-live removal list for the .NET files is in [dotnet.md §8.3](../architecture/dotnet.md#83-test-access-code-mode-auth_modetest-access-code-adr-0006-rules-unchanged).
- **Date:** 2026-11-18 (updated 2026-11-18: guards implemented after the security review)
- **One-way door:** No. The mode is a config switch (`AUTH_MODE`) plus one router and one seed script; it is removed at Azure go-live. The real risk is not reversibility but **misuse outside its scope**, which the guards below address.
- **Related:** ADR-0002 (RLS session context), ADR-0003 (Entra ID authentication), `docs/architecture/overview.md` §12.3.

## Context
- CLAUDE.md §3: "Authentication: Microsoft Entra ID SSO — never build our own login."
- Until Azure is live, the test environment runs on MonsterASP.NET shared hosting (overview §12.3) at `https://crmtest.runasp.net`.
- Testers need to exercise all role and team combinations (Admin, Manager, User, team A/B, no team) to verify RLS and the UI. Setting up a separate "CRM (test-interim)" Entra app registration, test accounts, app-role assignments and team groups for an interim host is slow and gives testers no easy way to switch role.
- The interim host holds **test data only**. There are no real users, no customer or personal data.
- RLS must behave exactly as in production so test results remain valid (ADR-0002).

## Options considered
| Option | Pros | Cons |
|---|---|---|
| A. Entra on the interim host (separate test app registration) | No exception to CLAUDE.md. Same code path as live. | Needs Entra admin work, test accounts and group setup per role/team. Switching role means switching account. Delays testing. |
| **B. Shared access code, then pick one of 7 seeded test users** (`AUTH_MODE=test-access-code`) | Fast to set up. Testers switch role in seconds. Session is built from the DB row, so RLS context is identical to Entra. | Is our own (minimal) login: shared secret, no per-person identity, no MFA. Must never reach live. |
| C. Host-level basic auth (IIS) + DEMO_MODE | No app code | DEMO_MODE has no database and no RLS, so it tests nothing that matters. DEMO_MODE is also refused in production. |
| D. Open site, no gate | Simplest | Anyone on the internet can write data. Rejected. |

## Decision
Option B, **only for the interim public test site on MonsterASP.NET (`https://crmtest.runasp.net`)**.

- **Switch:** `AUTH_MODE=test-access-code` (default `entra`). Validated in `src/config.ts`.
- **Gate:** a shared access code in host app setting `TEST_ACCESS_CODE`, **at least 24 characters with at least 10 distinct characters** (config refuses to start otherwise). Never in the repo.
  - Compared in **constant time** (`codeMatches` in `src/auth/testLogin.ts`: both sides SHA-256-hashed, then `timingSafeEqual`, so length is not leaked).
  - **Rate limited:**
    - 5 failed attempts per client per 15 minutes (`AttemptLimiter`, `src/auth/rateLimiter.ts`), then HTTP 429. The client key is `req.ip`; if no address is available, the session id is used, so there is never one shared "unknown" bucket that one client could lock for everyone.
    - A **global cap** of 50 failed attempts per 15 minutes across all clients locks the code form for everyone (logged as a warning). A successful code does not reset the global counter. This bounds distributed guessing.
    - All `/auth/*` routes are also limited to 120 requests per minute per client IP (`src/middleware/rateLimit.ts`).
    - `req.ip` is the real client address: `TRUST_PROXY` (default 1 over https) sets express `trust proxy`, and `enableXFF="true"` in `deploy/iis/web.config` makes iisnode pass `X-Forwarded-For`.
    - All limiters are in memory and per instance, which is adequate for a single-instance host.
  - A verified code is valid in the session for 10 minutes to pick a user.
- **User picker:** only the **7 fixed seeded users** in `TEST_USERS` (`src/auth/testLogin.ts`) can be selected; their rows are created by `db/seed/test-users.sql` (fixed `EntraOid` values, `@test.invalid` e-mails). Any other oid is rejected with 403.
- **Session and RLS:** after selection, `establishSession` builds the session from the `crm.Users` row (UserId, Role, TeamId) exactly as an Entra login does. Everything downstream (`withRlsContext`, `SESSION_CONTEXT`, the RLS predicates) is the **same code path** as with Entra. No RLS bypass exists in this mode.
- **Visibility:** a test banner is rendered on **every page** (`res.locals.testBanner` in `src/app.ts`), and a warning is logged at startup (`src/server.ts`).
- **Logging:** wrong codes and lockout attempts are logged with IP; successful test logins are logged with oid and role (no code value is ever logged).

### Constraints (conditions of the approval)
1. **Never used with real data.** Only test data on the interim host. The seed script header says so; do not run it against a database with real data.
2. **Never enabled in the Azure live environment.** See guard below.
3. **Removed at go-live:** delete `src/auth/testLogin.ts`, `src/auth/rateLimiter.ts` (only used by test login), `src/db/testUserRepo.ts`, `src/views/pages/test-login-code.ejs` and `test-login-user.ejs`, `db/seed/test-users.sql`, the `test-access-code` value from `AUTH_MODES`, the `TEST_ACCESS_CODE` setting and the test banner; delete the seeded users from any database that is carried forward. Track this as a go-live checklist item.

### Guard against use in Azure live (implemented after the security review, finding M1)
Implemented in `loadConfig` (`src/config.ts`) and unit-tested in `tests/config.test.ts`. The process throws `ConfigError` and does not start when:
- `AUTH_MODE=test-access-code` and the environment is **live**: `LIVE=true`, **or** `WEBSITE_INSTANCE_ID` is set (always set by Azure App Service).
- `AUTH_MODE=test-access-code` and `ENTRA_CLIENT_SECRET` is set. A real Entra secret next to the test mode indicates a real environment.
- `DB_AUTH_MODE=sql-password` and the environment is live (same live test). Azure must use `managed-identity`.
- `TEST_ACCESS_CODE` is shorter than 24 characters or has fewer than 10 distinct characters while the mode is on.
- `DEMO_MODE=true` with any of: `NODE_ENV=production`, live or Azure (`WEBSITE_INSTANCE_ID` or `WEBSITE_SITE_NAME`), `AUTH_MODE=test-access-code`, or any `DB_*` variable set. `NODE_ENV=test` (auth bypass) is refused when live or on Azure.

Other safeguards that remain: `AUTH_MODE` defaults to `entra`, so `test-access-code` must be set explicitly.

**Design notes:**
- `NODE_ENV=production` alone is not a trigger, because the MonsterASP test site runs the production build.
- Live detection deliberately uses `WEBSITE_INSTANCE_ID` and not `WEBSITE_SITE_NAME` alone. It is unconfirmed whether the MonsterASP IIS/iisnode host sets `WEBSITE_SITE_NAME`, and the test mode must keep working there. `LIVE=true` makes the refusal independent of the host.
- **Operational requirement:** set `LIVE=true` on the Azure live App Service and on any future live host. Never set it on the interim test host. Do not set `ENTRA_CLIENT_SECRET` on the interim host while it runs in this mode.

**Residual risk:** a live host that is not Azure App Service and lacks `LIVE=true` and an Entra client secret would not be detected. This is covered by the operational requirement above and the go-live checklist (removal of this mode, constraint 3).

### MonsterASP database-owner gap
MonsterASP shared hosting supplies one SQL login (the database owner) and, as far as known, no second login for a low-privilege app user. Then the app connects as the owner (overview §12.3). RLS predicates still filter the owner's queries, but the owner could disable the security policy or read base tables, so a SQL injection or compromised app could bypass RLS. Combined with a shared access code, this is acceptable **only** because the host holds test data. It must not carry over to Azure, where `DB_AUTH_MODE=managed-identity` with the low-privilege `crm_app` user is mandatory. RLS proof on the interim host comes from the RLS integration script running as a `WITHOUT LOGIN` user in `crm_app_role`.

## Consequences
- **Easier:** testers can verify every role/team combination quickly; RLS results are representative of live because the session-to-RLS path is shared.
- **Costs/risks:**
  - A shared secret gives no per-person accountability on the test site; audit rows record the seeded test user, not the tester. Acceptable for test data.
  - If the code leaks, anyone can use the test site until the code is rotated (change the host setting and restart). Rotate when a tester leaves the project.
  - Main risk is accidental activation in live. Mitigated by the default `entra`, the banner and startup warning, and the implemented startup refusals above (live flag, App Service detection, Entra client secret present).
- **Supersedes in part:** the "CRM (test-interim)" Entra app registration described in overview §12.3 is not needed while this mode is used.
- **Reversibility:** fully reversible; removal at go-live is required, not optional.
