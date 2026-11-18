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
   - `fn_RowAccess`: FILTER, read. Admin, owner, Manager of same team, or (`team` mode AND same team).
   - `fn_RowUpdate`: BEFORE UPDATE, existing row. Admin, owner, Manager of same team, or (`team` mode AND same team). Same rule as read today, kept as a separate function so the two can diverge by migration. (S1)
   - `fn_RowDelete`: BEFORE DELETE, existing row. Admin, owner, or Manager of same team only. Visibility mode never grants delete. (S1)
   - `fn_RowWriteTarget`: AFTER INSERT and AFTER UPDATE, resulting row. (S2)
   - `fn_AuditInsert`: AuditLog AFTER INSERT.
   - `fn_HasContext`: config tables, read.
   - `fn_AdminWrite`: config tables, Admin-only writes.
3. **Visibility mode is read inside the predicate from `crm.Settings`** (`visibility.mode`) by both `fn_RowAccess` and `fn_RowUpdate`. It is not a context key. (S3)
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
   - Because it also runs AFTER UPDATE, `TeamId` can only change when Admin makes the change.
   - **Owner reassignment rule (non-Admin, both visibility modes):** after an INSERT or UPDATE, `TeamId` must equal the caller's `TeamId`, and `OwnerId` must be the caller or a user with `crm.Users.TeamId` = caller's `TeamId` and `IsActive = 1`. In `team` mode this lets a User reassign a team-mate's record to themselves or to another active team-mate, never to an inactive user or someone outside the team. A record still owned by a deactivated team-mate can be updated by a non-Admin only if the same update reassigns it to an active team-mate.

### Sub-decisions (decided by the user 2026-11-18)
| # | Decision | Outcome | Alternative not chosen |
|---|---|---|---|
| S1 | In `team` mode, Users can **read and update** team-mates' records, but **not delete** them. Delete stays with owner, Manager (same team) and Admin. | **Accepted.** Implemented by splitting the former `fn_RowModify` into `fn_RowUpdate` (BEFORE UPDATE) and `fn_RowDelete` (BEFORE DELETE). | Read-only team mode (the architect's original recommendation). |
| S2 | Users may create a record for, or hand a record over to, an **active team-mate**. Nobody but Admin can move records across teams. | **Accepted.** | Users can only own their own records; Owner picker for Managers and Admins only. |
| S3 | Visibility mode is read from `crm.Settings` inside the predicate, not from a `Visibility` context key | **Accepted.** | A context key set by the app. Cheaper per row, but it needs a cache plus invalidation across instances, and it adds a trusted input. |

### Predicate T-SQL (changed by S1; full migration in overview §5.3–5.4)
```sql
CREATE FUNCTION sec.fn_RowUpdate(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR @OwnerId = c.UserId
         OR (    @TeamId = c.TeamId
             AND (   c.Role = N'Manager'
                  OR EXISTS (SELECT 1 FROM crm.Settings AS s
                             WHERE s.SettingKey = 'visibility.mode'
                               AND s.SettingValue = N'team'))));
GO
CREATE FUNCTION sec.fn_RowDelete(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR @OwnerId = c.UserId
         OR (c.Role = N'Manager' AND @TeamId = c.TeamId));
GO
-- Per entity table (Accounts, Contacts, Opportunities, Activities) in sec.CrmRowPolicy:
--   ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.<T>,
--   ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.<T> AFTER INSERT,
--   ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.<T> AFTER UPDATE,
--   ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.<T> BEFORE UPDATE,
--   ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.<T> BEFORE DELETE,
```

## Consequences
- **Security:**
  - Data access holds even if an app query is buggy.
  - Admin config writes are blocked in the DB even if a route check is missed.
  - Remaining trust: the app must set the context correctly. It must not let user input influence `UserId`, `Role` or `TeamId`; they come only from the server-side session populated at login.
  - **Team-mode edit weakens ownership (accepted risk):** in `team` mode any team member can change Amount, Stage or Owner on a colleague's deal. Mitigations: audited fields record `ChangedBy` (overview §7), every row carries `ModifiedBy`, and the record page shows "Last modified by". Admin can switch back to `private` at any time with immediate effect. Revisit if forecast integrity becomes an issue.
- **Performance:** each predicate adds an OR-heavy filter. This is acceptable at CRM scale (about 10^5 rows). `(TeamId, OwnerId)` indexes exist on every entity table. If plans degrade, add per-role query hints or filtered indexes; no redesign is needed.
- **Operations:**
  - Schema-bound predicates block `ALTER TABLE` on the protected tables and on `crm.Settings` and `crm.Users`.
  - Migrations that touch those tables must switch the policy `STATE = OFF` or drop and recreate it within the migration transaction.
  - A migration must never leave a policy off. The migrate script asserts at the end that both policies have `is_enabled = 1` in `sys.security_policies`.
- **Testing:** RLS cannot run in the sandbox (no SQL Server). `db/tests/rls-integration.sql` (17 cases, overview §5.5, including team-mode update allowed, delete blocked, team change blocked, and owner reassignment limited to active team-mates) is a **release gate**. It must pass against the Azure SQL dev database before any production deploy.
- **Hidden rows:**
  - Reads, updates and deletes on rows filtered out by RLS see zero rows, and the app returns 404 (no existence leak).
  - Blocked writes raise error 33504, which the app maps to 403.
  - Foreign key checks ignore RLS, so the app checks that referenced IDs are visible before writing.
- **Reversibility:** predicates and policies are versioned migrations. Changing a rule is a new migration plus a re-run of the integration test.
