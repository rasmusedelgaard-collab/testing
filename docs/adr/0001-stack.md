# ADR-0001: Application stack: Node.js 22 / TypeScript, Express 5 + EJS, Azure App Service + Azure SQL

- **Status:** Accepted (user-approved 2026-11-18). The product owner chose the language, hosting and database in the approved plan, and serverless SQL with auto-pause in prod in the 2nd round. Library choices are architect recommendations within that decision.
- **Partly superseded by [ADR-0007](0007-dotnet-10-rewrite.md) (2026-11-18):** the Node.js/Express/EJS stack, `mssql`, vitest/ESLint and iisnode hosting are replaced by .NET 10 / ASP.NET Core Razor Pages, Microsoft.Data.SqlClient + Dapper, xUnit and IIS in-process hosting. Azure SQL, serverless auto-pause, the migration rules and the interim MonsterASP decision stay in force.
- **Date:** 2026-11-18
- **One-way door:** **Yes, partly.**
  - Azure SQL is the system of record, and RLS is built on SQL Server features. Moving to another database means rewriting the security layer. Product owner sign-off was given via the plan.
  - Express versus another Node framework is reversible at moderate cost.

## Context
- The CRM is internal, for about 10 to 200 users, and is used during business hours.
- It runs on Azure: App Service and Azure SQL. Sign-in is Entra ID.
- The database must enforce row-level security (RLS), not just the app.
- The team works in TypeScript.
- The UI spec asks for server-rendered pages that work with JavaScript turned off, with charts as the only exception (progressive enhancement).
- Dependencies must use permissive licences and be widely used (CLAUDE.md §3).

## Options considered
| Option | Pros | Cons |
|---|---|---|
| **A. Express 5 + EJS (SSR), `mssql` driver** | Boring and proven. Express 5 handles async errors natively. Server rendering matches the progressive-enhancement UI spec. Small attack surface: no SPA, no public API, no token handling in the browser. | Hand-written SQL and repositories. Fewer built-in conventions. |
| B. Next.js / React SSR | Rich UI ecosystem | Heavier. Splits the server and client trust boundary. Its auth patterns don't fit the server-held MSAL session as cleanly. Overkill for forms and lists. |
| C. NestJS | Structure and dependency injection | More framework than this size needs. Steeper learning curve. |
| D. An ORM (Prisma/TypeORM) instead of `mssql` | Less SQL to write | Awkward with per-connection `SESSION_CONTEXT`, transaction-scoped connections and RLS. Hides the exact SQL that security review must see. |

## Decision
Option A, with these libraries (all MIT or Apache-2.0; latest majors checked on 2026-11-18):
- **Runtime:** Node.js 22 LTS, TypeScript (strict).
- **Web:** `express@5`, `ejs`, `helmet`, `express-session`, `zod` for validation, `@azure/msal-node` for auth, `mssql@12` (with `tedious`) for SQL.
- **Observability:** `pino` for logging. `@azure/monitor-opentelemetry` sends telemetry to App Insights.
- **Charts:** `chart.js@4`, vendored into `src/public/vendor/` with no CDN.
- **Tests and lint:** `vitest` for unit tests, `@playwright/test` for e2e, ESLint with typescript-eslint.
- **Database migrations:** a small in-repo runner. It applies `db/migrations/NNN_*.sql` in order, splits on `GO`, and records each version and SHA-256 checksum in `dbo.SchemaVersions`. It runs as the deployer identity, never as `crm_app`.
- **Hosting:** Azure App Service Linux (Node 22) and Azure SQL Database (serverless), in an EU region. See overview §12.
- **Interim test hosting (user decision 2026-11-18):** until Azure go-live, the test environment runs on MonsterASP.NET (Windows/IIS with iisnode, shared MSSQL), test data only. The same build runs there because:
  - DB auth is selected by `DB_AUTH_MODE` = `managed-identity` | `default-azure` | `sql-password` (`DB_USER`/`DB_PASSWORD` from host app settings, never in the repo);
  - the app listens on `process.env.PORT`, which may be a named pipe under iisnode;
  - migrations run as the DB owner login, and the app uses a separate low-privilege SQL user in `crm_app_role` if the host allows it. Otherwise connecting as the owner is a documented test-only gap (the owner could disable RLS policies).
  - The host's database is not serverless, so the auto-pause retry logic is harmless there.
  - Details in overview §12.3. This is reversible and temporary; it adds no production dependency.
- **Prod SQL (user decision 2026-11-18):** serverless with **auto-pause after 60 minutes** in every environment, including prod. Public endpoint with "Allow Azure services" and Entra-only authentication. The app retries transient errors (40613 and others) with backoff up to about 60 s and shows a "Waking up the database" page if the database is still not available (overview §8.1).

## Consequences
- **Easier:**
  - Security review, because all SQL is explicit and parameterised.
  - RLS context handling, because we control connections.
  - Accessibility, because HTML comes from the server.
- **Harder:**
  - Rich client interactions (accepted, as the UI spec avoids them).
  - SQL must be written and reviewed by hand. Mitigation: repository pattern, plus a lint rule that keeps SQL inside `src/db/`.
- **Risk: the `mssql` pool does not reset connections.** This is handled in ADR-0002.
- **Cost (estimate):**
  - App Service B1/P0v3: about 13–60 EUR/month.
  - SQL serverless at 1–2 vCores with auto-pause 60 min: compute is billed only while the database is running, plus storage always. For business-hours use this is well below the about 150–200 EUR/month of an always-on database. In dev it is close to zero.
- **Trade-off: cold start.** The first request after about an hour idle (typically the first user each morning) waits while the database resumes, up to about 60 s. Accepted by the user for an internal tool. Mitigations: retry with backoff, a friendly "waking up" page, no background jobs or health probes that touch the database (they would also prevent the pause). Reversible with a one-line Bicep change (`autoPauseDelay = -1`); no app change needed.
- **Trade-off: public endpoint.** "Allow Azure services" admits connection attempts from any Azure tenant's resources, but Entra-only auth means only principals created as database users can sign in. Private endpoint is a later hardening option.
  - Key Vault and Application Insights: small.
- **Reversibility:**
  - The framework is moderate to reverse: routes and views would be rewritten, but repositories and SQL survive.
  - The database is a one-way door: see the note at the top.
