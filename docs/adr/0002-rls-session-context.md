# ADR-0002: Row-Level Security in Azure SQL driven by SESSION_CONTEXT

- **Status:** Accepted (user-approved 2026-11-18). The core approach (RLS with SESSION_CONTEXT, the visibility rules, fail closed) is the product owner's decision in the plan. The sub-decisions S1–S3 below were decided by the user in the 2nd round on 2026-11-18; S1 was changed from the original recommendation (team mode now allows update).
- **Date:** 2026-11-18
- **One-way door:** **Partly.**
  - Putting authorisation in the database is costly to undo. Every query and test assumes it.
  - The predicate details are reversible: they can be changed by a migration, with `ALTER SECURITY POLICY`.

## Context
- Access rules:
  - Admin sees everything.
  - Manager sees own and own-team records.
  - User sees own records, or own-team records when Admin sets visibility to `team`.
  - In `team` mode a User may also **update** team-mates' records, but not delete them and not move them to another team (user decision 2026-11-18).
- Enforcement must hold even if an app query forgets a `WHERE` clause.
- Constraints:
  - The app connects as **one** managed identity (`crm_app`), not as each end user.
  - The app uses a connection pool.
  - Users are Entra identities with no SQL login of their own.

## Options considered
| Option | Pros | Cons |
|---|---|---|
| **A. RLS with predicates reading `SESSION_CONTEXT` set per request (`@read_only = 1`)** | Enforced by the engine on every statement. Works with one app identity and a pool. Read-only keys cannot be overwritten later in the session, even by injected SQL. | The app must set the context correctly and reliably clear it before reuse. Policies tie the schema to the predicates. |
| B. App-side filtering only (WHERE clauses in repositories) | Simple | A single missed filter leaks data. Fails the "full RLS in the DB" requirement. |
| C. One SQL user per end user (`EXECUTE AS USER` / per-user tokens) | The DB knows the real user | Breaks pooling. Needs a SQL principal per Entra user. Complex on-behalf-of token flow. |
| D. Views per role plus DENY on base tables | No RLS features needed | Logic is duplicated in every view. Writes are hard to guard. Brittle. |

## Decision
Option A, concretely (T-SQL in [overview §5](../architecture/overview.md#5-row-level-security-migration-002_rlssql-adr-0002)):

1. **Context keys.** `UserId` (int), `Role` (`Admin`, `Manager` or `User`) and `TeamId` (int or NULL) are set with `sp_set_session_context … @read_only = 1` in the first batch of a per-request transaction (`withRlsContext`).
2. **Predicate functions.** Inline, schema-bound table-valued functions live in schema `sec`, which is owned by dbo. `crm_app` has no permissions on `sec`.
   - `fn_RowAccess`: FILTER, read.
   - `fn_RowModify`: BEFORE UPDATE and BEFORE DELETE, existing row. **[sign-off]**
   - `fn_RowWriteTarget`: AFTER INSERT and AFTER UPDATE, resulting row. **[sign-off]**
   - `fn_AuditInsert`: AuditLog AFTER INSERT.
   - `fn_HasContext`: config tables, read.
   - `fn_AdminWrite`: config tables, Admin-only writes.
3. **Visibility mode is read inside the predicate from `crm.Settings`** (`visibility.mode`). It is not a context key. **[sign-off]**
   - There is one source of truth, so changes are immediate on every instance.
   - The app supplies one less trusted input.
   - Admin-only writes are enforced by `fn_AdminWrite`.
   - A missing row or an unknown value means `private`.
4. **Fail closed.**
   - A missing `UserId`, or a `Role` outside the allowed set, gives zero rows and blocks every write.
   - A NULL `TeamId` never matches, so the user sees only own rows.
5. **Connection hygiene.** This is a deviation from the plan.
   - `mssql` 12.x does **not** call `sp_reset_connection` when a connection is released. `ConnectionPool.release()` only returns it to the tarn pool, as verified in `mssql@12.7.14` `lib/base/connection-pool.js`.
   - We therefore subclass the pool (`ResettingConnectionPool`). Its `release()` calls tedious `Connection.reset()`, which runs `sp_reset_connection`, before returning the connection. If the reset fails, the connection is closed, so the pool destroys it.
   - As defence in depth, the context batch starts with `IF SESSION_CONTEXT(N'UserId') IS NOT NULL … THROW 51000`. If a reset is ever skipped, the request fails rather than inheriting another user's identity. A non-read-only key would be overwritten silently, so this guard matters.
6. **Pool separation.**
   - `rlsPool` is used only by `withRlsContext`. A lint rule enforces this.
   - `systemPool`, which never sets context, serves the session store, the login upsert procedure and public branding reads.
   - Login writes `crm.Users` only via `crm.usp_UpsertUserOnLogin`, using ownership chaining. `crm_app` has no INSERT or UPDATE on `crm.Users`.
7. **Ownership integrity.**
   - The app always derives `TeamId` from the owner's `crm.Users.TeamId`.
   - `fn_RowWriteTarget` rejects rows whose owner is not self or an active member of the caller's team, or whose `TeamId` is not the caller's team. Admin is exempt.

### Sub-decisions needing product owner sign-off
| # | Decision | Recommendation | Alternative |
|---|---|---|---|
| S1 | In `team` mode, Users can **read** team-mates' records but **not edit or delete** them | Yes. This matches "delete allowed for owner, Manager, Admin". Editing others' work is a Manager action. | Let `team`-mode Users edit (not delete) team records. That would add a third predicate variant. |
| S2 | Users may create a record for, or hand a record over to, an **active team-mate**. Nobody but Admin can move records across teams. | Yes. The UI spec shows an Owner picker for all roles. | Users can only own their own records. The Owner picker is then shown to Managers and Admins only. |
| S3 | Visibility mode is read from `crm.Settings` inside the predicate, not from a `Visibility` context key | Yes (reasons above) | A context key set by the app. Cheaper per row, but it needs a cache plus invalidation across instances, and it adds a trusted input. |

## Consequences
- **Security:**
  - Data access holds even if an app query is buggy.
  - Admin config writes are blocked in the DB even if a route check is missed.
  - Remaining trust: the app must set the context correctly. It must not let user input influence `UserId`, `Role` or `TeamId`; they come only from the server-side session populated at login.
- **Performance:** each predicate adds an OR-heavy filter. This is acceptable at CRM scale (about 10^5 rows). `(TeamId, OwnerId)` indexes exist on every entity table. If plans degrade, add per-role query hints or filtered indexes; no redesign is needed.
- **Operations:**
  - Schema-bound predicates block `ALTER TABLE` on the protected tables and on `crm.Settings` and `crm.Users`.
  - Migrations that touch those tables must switch the policy `STATE = OFF` or drop and recreate it within the migration transaction.
  - A migration must never leave a policy off. The migrate script asserts at the end that both policies have `is_enabled = 1` in `sys.security_policies`.
- **Testing:** RLS cannot run in the sandbox (no SQL Server). `db/tests/rls-integration.sql` (11 cases, overview §5.5) is a **release gate**. It must pass against the Azure SQL dev database before any production deploy.
- **Hidden rows:**
  - Reads, updates and deletes on rows filtered out by RLS see zero rows, and the app returns 404 (no existence leak).
  - Blocked writes raise error 33504, which the app maps to 403.
  - Foreign key checks ignore RLS, so the app checks that referenced IDs are visible before writing.
- **Reversibility:** predicates and policies are versioned migrations. Changing a rule is a new migration plus a re-run of the integration test.
