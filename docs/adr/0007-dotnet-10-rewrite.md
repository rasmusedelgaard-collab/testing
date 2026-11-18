# ADR-0007: Rewrite the application tier in .NET 10 (ASP.NET Core Razor Pages); keep database, RLS, UI spec and security decisions

- **Status:** Accepted for the rewrite itself (product owner decision 2026-11-18). The sub-decisions marked **(sign-off)** below are architect recommendations that still need product owner sign-off.
- **Date:** 2026-11-18
- **Supersedes:** the stack parts of [ADR-0001](0001-stack.md) (Node.js/Express/EJS, `mssql`, vitest, ESLint, iisnode hosting), the library parts of [ADR-0003](0003-entra-msal-auth.md) (`@azure/msal-node`) and the mechanism of [ADR-0004](0004-session-store.md) (`express-session` + custom store), plus decision 5 of [ADR-0002](0002-rls-session-context.md) (`ResettingConnectionPool`). All *rules* in those ADRs (RLS, roles, teams, overage, timeouts, revocation, retry, fail-closed refusals) stay in force.
- **Build reference:** [docs/architecture/dotnet.md](../architecture/dotnet.md).
- **One-way door:** **Partly.**
  - The language/runtime switch is expensive to reverse (every route, view and test is rewritten). The product owner has made this decision.
  - **Deleting the Node code at cutover** is the real point of no return for the team's working knowledge: after deletion, Node fixes stop and the two code bases diverge. It is gated by the cutover acceptance criteria (dotnet.md §16) and needs product owner sign-off at that moment.
  - The database is **unchanged** (migrations 001–004, RLS, grants). No new one-way door on data.

## Context
- The interim test host, MonsterASP.NET, is Windows/IIS and .NET-first. Its Node.js is v16, but the Node app needs Node 20 or newer, so the Node app cannot run there reliably (iisnode deploys and smoke tests have been fragile).
- The target host is Azure App Service with Entra ID sign-in. ASP.NET Core with Microsoft.Identity.Web is a first-class option there.
- Requirements that do not change: the SQL schema and RLS design (ADR-0002), Entra app roles + groups-to-team (ADR-0003), server-side revocable sessions with idle/absolute timeouts and a 5-minute `IsActive` re-check (ADR-0004), FX rules (ADR-0005), the test-access-code exception and its guards (ADR-0006), the UI spec v0.3 and its reviews, CSP without inline script, WCAG 2.2 AA.
- CLAUDE.md §3: dependencies must be widely used with permissive licences (MIT, Apache-2.0, BSD).

## Options considered

### Web framework
| Option | Pros | Cons |
|---|---|---|
| **A. ASP.NET Core Razor Pages (server-rendered)** | One page = one handler + one view, which maps 1:1 to the Express routes and EJS views. Built-in antiforgery, rate limiting, cookie auth, Data Protection, tag helpers. No client trust boundary. | Razor Pages conventions to learn; generic entity pages need a custom route constraint. |
| B. ASP.NET Core MVC controllers + views | Familiar to many .NET developers | More ceremony (controllers + view folders) for the same result. |
| C. Blazor (Server or WASM) | Rich interactivity | SignalR circuit (Server) or SPA (WASM): breaks "works with JS off", changes the security model. Rejected. |
| D. Minimal APIs + a templating library | Small | Re-implements what Razor Pages gives for free (antiforgery wiring, model state, layouts). |

### Data access
| Option | Pros | Cons |
|---|---|---|
| **A. Microsoft.Data.SqlClient + Dapper (Apache-2.0)** | Explicit, reviewable SQL (same as today); thin mapping; full control of the connection and transaction, which RLS needs. | Hand-written SQL (as today). |
| B. Plain ADO.NET | No dependency | Much mapping boilerplate. |
| C. EF Core | Productive for CRUD | Hides SQL from security review; awkward with per-connection `SESSION_CONTEXT`; EF migrations would compete with the SQL migrations. Rejected (as in ADR-0001 option D). |

### Migrations
| Option | Pros | Cons |
|---|---|---|
| **A. Small migrator inside the app (`dotnet Crm.Web.dll migrate`)** | Byte-compatible with the existing `dbo.SchemaVersions` (Version, Name, SHA-256 checksum) already populated on the test database; same `GO` splitting; same "policies enabled" assertion; zero dependencies; about 150 lines, unit- and SQL-tested. | We own the code (we already do, in TypeScript). |
| B. DbUp (MIT) | Mature | Its default journal is also called `dbo.SchemaVersions` but with different columns (`Id`, `ScriptName`, `Applied`), so it would collide with the existing table or need a second journal; no checksum verification of applied scripts (our "refuse if an applied file changed" rule); the policy assertion would still be custom code. |
| C. EF Core migrations | — | Not using EF. |

### Sessions
| Option | Pros | Cons |
|---|---|---|
| **A. Cookie authentication with a server-side ticket store (`ITicketStore`) in the existing `app.Sessions` table** | Built-in cookie handler; the cookie carries only a protected random session key; the session row holds minimal JSON (no tokens) exactly as ADR-0004 requires; revocation by deleting rows keeps working; no schema change; same grants. | About 100 lines (store + JSON mapping), as today. One DB read per request (as today). |
| B. Cookie authentication with the ticket in the cookie only | No DB read per request | Cannot revoke a session server-side (ADR-0004 option D, rejected for this reason); name/email in a client-held cookie. The 5-minute `IsActive` check limits but does not replace revocation. |
| C. `AddSession()` (ASP.NET Core session state) + `IDistributedCache` SQL | Built-in | Session state is not authentication; needs the `Microsoft.Extensions.Caching.SqlServer` schema (new table, migration 005), and we would still need cookie auth on top. Two mechanisms for one job. |

### Secrets on the IIS test host
| Option | Pros | Cons |
|---|---|---|
| **A. Environment variables in `web.config` `<aspNetCore><environmentVariables>`, written by the deploy job** | IIS never serves `web.config` (hard-wired, even when the ASP.NET Core Module fails to load); the app reads the same environment variable names as on Azure App Service (one code path); a missing value fails startup validation (fail closed). | Plain text at rest in the site folder (true for every option on shared hosting); a deploy rewrites it, so manual edits on the host are lost (intended: the deploy is the source of truth). |
| B. `appsettings.Production.json` written at deploy | Familiar .NET pattern | If the ASP.NET Core handler mapping is ever broken, IIS's static file handler can serve `.json` files from the site root: a secret leak path that option A does not have. Different key names from Azure (two config paths). |
| C. Host control panel environment variables | Nothing on disk in the deploy | Unknown whether MonsterASP offers this; not automatable from CI. |

## Decision
1. **Runtime and framework:** .NET 10 (LTS), ASP.NET Core **Razor Pages** with tag helpers, server-rendered, no SPA, no Blazor. Existing CSS/JS (`src/public`) and the vendored Chart.js are reused unchanged in `wwwroot`. CSP stays `script-src 'self'; style-src 'self'` with no inline script or style.
2. **Solution layout:** `dotnet/Crm.slnx` with `dotnet/src/Crm.Web` (one web project) and `dotnet/tests/Crm.Tests`. Node code stays untouched until cutover; at cutover the Node app is deleted and the .NET tree is moved to the repository root (`src/Crm.Web`, `tests/Crm.Tests`, `Crm.slnx`) so CLAUDE.md §5 stays valid. (dotnet.md §2, §16)
3. **Data:** Microsoft.Data.SqlClient + Dapper. No EF. One `SqlConnection` + one `SqlTransaction` per request for RLS work, context set by `sp_set_session_context … @read_only = 1` behind the existing stale-context guard (51000). MARS off; one command at a time per transaction, enforced by a wrapper. Two separate pools (distinct connection strings) for RLS and system work. (dotnet.md §6)
4. **Connection reset (verified claim, kept guard):** SqlClient always resets a pooled connection on reuse. The reset is *deferred*: the driver sets the TDS `RESETCONNECTION` flag on the first request sent after `Open()` returns a pooled connection, so the server runs `sp_reset_connection` (which clears `SESSION_CONTEXT`, temp tables, open transactions) before executing that request. The old "`Connection Reset=false`" keyword is no longer honoured. Unlike the Node design (reset on release), the stale context therefore sits on an idle pooled connection until it is next used, and is cleared at acquire time. The stale-context guard stays as defence in depth, and a CI test with a real SQL Server proves the behaviour (pool size 1, two users in sequence, no 51000, second user's context visible). The `ResettingConnectionPool` class is not ported.
5. **Migrations:** in-app migrator commands `migrate`, `seed-test`, `run-sql <file>` in `Crm.Web`, reading the SQL files as embedded resources, same table, same checksum (SHA-256 of the file bytes after CRLF→LF), same `GO` rule, same policy assertion. Not DbUp.
6. **Authentication:** Microsoft.Identity.Web (OIDC auth-code + PKCE, confidential client, single tenant), **`response_mode=query`** and callback path **`/auth/redirect`** (unchanged, so the Entra app registrations need no change), no tokens saved, no downstream API. App roles → role; groups claim → team via `crm.usp_UpsertUserOnLogin`; overage fail-closed; tenant check; deactivated users (50011) to `/no-access`. The test-access-code mode (ADR-0006) is ported with the same rules and guards.
7. **Sessions:** option A: cookie authentication + `ITicketStore` over `app.Sessions` on the system pool. Idle 60 min rolling (enforced in the store), absolute 10 h, `IsActive` re-check every 5 minutes in `OnValidatePrincipal`. Antiforgery and TempData use the built-in cookie mechanisms. ASP.NET Core Data Protection keys are persisted (Azure: `/home`; MonsterASP: `App_Data`). `SESSION_SECRET` is retired.
8. **Cross-cutting:** built-in antiforgery (403 on failure, as today), built-in rate limiter (same limits), CSP and other headers from a small middleware, HSTS, forwarded headers by hop count (`TRUST_PROXY`).
9. **Configuration:** same environment variable names where the meaning is unchanged (`DB_CONNECTION_STRING`, `DB_AUTH_MODE`, `AUTH_MODE`, `LIVE`, …), bound through `IConfiguration` and validated once at startup with the same refusals (test-access-code or sql-password when live; test-access-code with `ENTRA_CLIENT_SECRET`). `NODE_ENV`, `PORT`, `DEMO_MODE` and `SESSION_SECRET` are dropped.
10. **Testing:** xUnit v3 + `WebApplicationFactory`; services are pure unit tests; data access, RLS and HTTP flows run against a **real SQL Server** (`mcr.microsoft.com/mssql/server:2022-latest` as a GitHub Actions service container, the same image locally via Docker). No fake SQL driver. `db/tests/rls-integration.sql` runs in CI on every push.
11. **Hosting:**
    - MonsterASP: IIS **in-process**, framework-dependent publish for `net10.0`, `web.config` produced by publish from a committed template without secrets; secrets injected by the deploy job as `web.config` environment variables (option A above).
    - Azure: App Service **Linux**, .NET 10 runtime stack, app settings + Key Vault references, managed identity to Azure SQL (`Authentication=Active Directory Managed Identity`), `LIVE=true`. (sign-off: Linux kept for cost; Windows would mirror the IIS host more closely.)
12. **Dependencies (all MIT or Apache-2.0):** Microsoft.Identity.Web, Microsoft.Data.SqlClient, Dapper, Azure.Monitor.OpenTelemetry.AspNetCore (Azure only); tests: xunit.v3, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing, AngleSharp (HTML assertions). **Not allowed:** FluentAssertions 8+ (commercial licence), Moq, EF Core, DbUp.

### Sub-decisions needing product owner sign-off
| # | Decision | Recommendation |
|---|---|---|
| P1 | Drop `DEMO_MODE` (in-memory store with emulated RLS) and the `NODE_ENV=test` bypass in .NET. Local dev uses SQL Server in Docker and test-access-code mode; tests sign in through the real test-access-code flow. | **Yes.** Removes about 500 lines of emulation and the last auth-bypass switch; tests then exercise real RLS. |
| P2 | Deploy .NET to the MonsterASP test site from increment 1 onward, replacing the Node deployment there. | **Yes.** The Node app cannot run on the host's Node 16 anyway; the site holds test data only. |
| P3 | Delete the Node app at cutover (list in dotnet.md §16). | **Yes, when the cutover criteria pass.** One-way in practice. |
| P4 | Azure App Service OS stays **Linux**. | **Yes** (cheaper plans; app is OS-neutral; globalization is pinned in code, §11). |
| P5 | FX rate input precision: ADR-0005 says "up to 8 decimals", the Node code and its tests accept 6. | Port **6** (current behaviour); change both docs and code together if 8 is wanted. |

## Consequences
- **Easier:** first-class hosting on IIS and Azure App Service; Microsoft-supported Entra integration (and a later move from client secret to a managed-identity federated credential is configuration-only in Microsoft.Identity.Web); built-in antiforgery, rate limiting and Data Protection replace hand-written middleware; real-SQL tests in CI close the long-standing "RLS script never ran" gap.
- **Harder:**
  - A full rewrite of about 5,300 lines of TypeScript plus views and tests; parity must be proven, not assumed (port the behaviours listed in `tests/`).
  - Two OSs (Windows/IIS test, Linux Azure): globalization and time zones must not depend on the host (locale profiles are pinned in code; IANA time zones checked at startup).
  - Data Protection keys become operational state: losing them signs everyone out and invalidates open forms.
- **Risks and mitigations:**
  - *MonsterASP lacks the .NET 10 hosting bundle:* increment 0 spike; fallback is a self-contained `win-x64` publish (needs only the ASP.NET Core Module v2 that any hosting bundle installs).
  - *Host without ICU (IANA time zones fail on Windows):* startup self-check; fallback NodaTime (Apache-2.0), dotnet.md §11.
  - *Behaviour drift during the rewrite:* Node frozen to security fixes only; every Node test file mapped to a .NET test class (dotnet.md §14.4); UX/QA/security review per increment.
  - *Session invalidation at cutover:* expected; `app.Sessions` is emptied and users sign in again.
- **Cost:** no new paid services. Azure plan prices unchanged (Linux). SQL Server Developer edition in CI is free for dev/test.
- **Reversibility:** until cutover, fully reversible (Node stays deployable from git). After cutover, reverting means restoring the Node tree from git history and re-validating it.
