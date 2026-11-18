# Internal CRM

Accounts, contacts, opportunities and activities on Node.js + TypeScript (Express 5, EJS), Azure SQL with
Row-Level Security, and Microsoft Entra ID sign-in. Design: [docs/architecture/overview.md](docs/architecture/overview.md),
ADRs in [docs/adr](docs/adr), UI spec in [docs/ux/ui-spec.md](docs/ux/ui-spec.md).

Status: **increment 1 (foundation)**: config, sign-in, sessions, RLS plumbing, layout, migrations. The CRM pages
themselves arrive in later increments (nav items show a placeholder).

## Quick start (demo mode, no Entra, no database)

```bash
npm install
cp .env.example .env        # DEMO_MODE=true is already set
npm run dev                 # http://localhost:3000
```

Open the site and choose Admin, Manager or User on the sign-in page. Demo mode uses fake users and in-memory
sessions. It refuses to start when `NODE_ENV=production` or on Azure App Service. `NODE_ENV=test` (what the
unit tests use) is also demo mode and additionally enables `GET /__test/login?role=Admin` for automation.

## Scripts

| Script | What it does |
|---|---|
| `npm run dev` | Run with reload (tsx), reads `.env` |
| `npm run build` | Compile to `dist/` and copy views and public assets |
| `npm start` | Run the compiled app (`node dist/server.js`) |
| `npm run package` | Build + assemble `package/` (dist, production node_modules, web.config, db scripts) for the MonsterASP deploy branch |
| `npm run package` | Build + assemble `package/` (dist, production node_modules, web.config, db scripts) for the MonsterASP deploy branch |
| `npm test` | Unit tests (vitest) |
| `npm run lint` | ESLint, including the restriction on importing the DB pool |
| `npm run typecheck` | `tsc --noEmit` (strict) |
| `npm run db:migrate` | Apply `db/migrations/*.sql`, record checksums, assert both security policies are enabled |
| `npm run db:seed:test` | Seed test users and teams (`db/seed/test-users.sql`), only for the interim test site |

Node 20 or newer is required at runtime (dev scripts use `--env-file-if-exists`, which needs Node 22.9+).

## Environment variables

All are validated at startup by `src/config.ts`; the process exits with a list of problems if something is wrong.

| Variable | Required | Notes |
|---|---|---|
| `NODE_ENV` | no | `development` (default), `test`, `production` |
| `PORT` | no | Default `3000`. Kept as a string: iisnode passes a Windows named pipe |
| `AUTH_MODE` | no | `entra` (default) or `test-access-code` (interim hosted test site) |
| `DEMO_MODE` | no | `true` = no Entra, no database. Refused in production / on App Service |
| `REDIRECT_URI` | entra | e.g. `https://host/auth/redirect`; https required in production. Its origin is used for the post-logout redirect |
| `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET` | entra | Single-tenant app registration (overview 12.2) |
| `TEST_ACCESS_CODE` | test-access-code | At least 16 characters, shared by testers |
| `SESSION_SECRET` | yes (not demo) | At least 32 characters; comma-separated list for rotation |
| `SESSION_IDLE_MINUTES` / `SESSION_ABSOLUTE_HOURS` | no | Defaults 60 / 10 |
| `DB_SERVER`, `DB_NAME` | yes (not demo) | |
| `DB_AUTH_MODE` | yes (not demo) | `managed-identity` (Azure App Service), `default-azure` (local dev, Azure CLI login), `sql-password` |
| `DB_USER`, `DB_PASSWORD` | sql-password only | Rejected for the other modes. Never commit them |
| `DB_TRUST_SERVER_CERT` | no | `true` only for hosts with a self-signed SQL certificate |
| `SQL_POOL_MAX` | no | Size of the RLS pool, default 10 |
| `LOG_LEVEL` | no | `info` default |

Secrets belong in the host's settings or Key Vault, never in the repository. `.env` is git-ignored.

## Database

```bash
# as the deployer / DB owner identity (set DB_* accordingly)
npm run db:migrate
```

Migrations: `001_schema` (tables, login upsert procedure), `002_rls` (predicates and policies), `003_grants`
(role `crm_app_role`), `004_seed` (settings, stages, picklists; `visibility.mode = private`). Create the app's
database user afterwards (`db/manual/create-app-user.sql`).

**Prove RLS before relying on it.** `db/tests/rls-integration.sql` runs against a dev database with `sqlcmd -b`,
rolls everything back and prints PASS/FAIL per case. It has not been run yet (no SQL Server in the build sandbox): it
is a release gate.

Serverless Azure SQL auto-pauses; the first request after idle can take up to about a minute. The app retries
transient errors (such as 40613) for up to ~60 s, then shows a "Waking up the database" page (HTTP 503).

## Interim test site (AUTH_MODE=test-access-code)

For a host without Entra. Testers enter a shared access code (max 5 wrong attempts per 15 minutes per IP), then pick
one of the seeded test users. The session is built from the user's `crm.Users` row exactly as an Entra login would,
so RLS behaves identically. Every page shows "TEST ENVIRONMENT — test data only". Run `npm run db:seed:test` once.
Test data only; never use this mode with real data.

## Deploying the interim test site

The TEST site runs on MonsterASP.NET (IIS + iisnode, branch `deploy` built by GitHub Actions). Build the deploy folder locally with
`npm run package` (output in `package/`, git-ignored). Setup, secrets handling, migrations, rollback and troubleshooting:
[docs/deploy/monsterasp.md](docs/deploy/monsterasp.md).

## Data-access rules (enforced by lint and tests)

- Only `src/db/withRlsContext.ts` may use the RLS pool; only `src/db/system.ts` the system pool.
- SQL and the `mssql` driver live in `src/db/` only.
- `process.env` is read only in `src/config.ts`.
