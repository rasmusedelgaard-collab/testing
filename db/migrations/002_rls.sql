-- 002_rls.sql: predicate functions and security policies. Derived from docs/architecture/overview.md 5.3 and 5.4 (ADR-0002).
-- Later migrations that alter crm.Settings, crm.Users or protected tables must switch the policies OFF and back ON (SCHEMABINDING).

-- Read access: FILTER predicate
CREATE FUNCTION sec.fn_RowAccess(@OwnerId int, @TeamId int)
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
         OR (    @TeamId = c.TeamId                       -- NULL team never matches
             AND (   c.Role = N'Manager'
                  OR EXISTS (SELECT 1 FROM crm.Settings AS s
                             WHERE s.SettingKey = 'visibility.mode'
                               AND s.SettingValue = N'team'))));
GO

-- Update an existing row: BEFORE UPDATE.
-- Admin, owner, Manager of the same team, or (visibility = 'team' AND same team).
-- Deliberately the same rule as fn_RowAccess today, but a separate function so read and
-- update rules can diverge later by migration without touching the other.
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
         OR (    @TeamId = c.TeamId                       -- NULL team never matches
             AND (   c.Role = N'Manager'
                  OR EXISTS (SELECT 1 FROM crm.Settings AS s
                             WHERE s.SettingKey = 'visibility.mode'
                               AND s.SettingValue = N'team'))));
GO

-- Delete an existing row: BEFORE DELETE.
-- Admin, owner, or Manager of the same team. Visibility mode never grants delete.
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

-- Values a row may end up with: AFTER INSERT / AFTER UPDATE.
-- Non-Admin: resulting TeamId = caller's TeamId (no cross-team moves), and resulting
-- OwnerId = caller, or an ACTIVE user whose crm.Users.TeamId = caller's TeamId.
CREATE FUNCTION sec.fn_RowWriteTarget(@OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND (   c.Role = N'Admin'
         OR (    @OwnerId = c.UserId
             AND (@TeamId = c.TeamId OR (@TeamId IS NULL AND c.TeamId IS NULL)))
         OR (    @TeamId = c.TeamId
             AND EXISTS (SELECT 1 FROM crm.Users AS u
                         WHERE u.UserId = @OwnerId AND u.TeamId = @TeamId AND u.IsActive = 1)));
GO

-- Audit rows: ChangedBy must be the caller, and the row must be within the caller's write scope
CREATE FUNCTION sec.fn_AuditInsert(@ChangedBy int, @OwnerId int, @TeamId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  FROM (SELECT CAST(SESSION_CONTEXT(N'UserId') AS int)          AS UserId,
               CAST(SESSION_CONTEXT(N'Role')   AS nvarchar(10)) AS Role,
               CAST(SESSION_CONTEXT(N'TeamId') AS int)          AS TeamId) AS c
  WHERE c.UserId IS NOT NULL
    AND c.Role IN (N'Admin', N'Manager', N'User')
    AND @ChangedBy = c.UserId
    AND (c.Role = N'Admin' OR @OwnerId = c.UserId OR @TeamId = c.TeamId);
GO

-- Config tables: any valid context may read
CREATE FUNCTION sec.fn_HasContext(@RowId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  WHERE CAST(SESSION_CONTEXT(N'UserId') AS int) IS NOT NULL
    AND CAST(SESSION_CONTEXT(N'Role') AS nvarchar(10)) IN (N'Admin', N'Manager', N'User');
GO

-- Config tables: only Admin may write. @RowId is unused; it exists so every predicate binds to a column.
CREATE FUNCTION sec.fn_AdminWrite(@RowId int)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN
  SELECT 1 AS ok
  WHERE CAST(SESSION_CONTEXT(N'UserId') AS int) IS NOT NULL
    AND CAST(SESSION_CONTEXT(N'Role') AS nvarchar(10)) = N'Admin';
GO

CREATE SECURITY POLICY sec.CrmRowPolicy
  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Accounts,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Accounts AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Accounts AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Accounts BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Accounts BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Contacts,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Contacts AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Contacts AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Contacts BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Contacts BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Opportunities,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Opportunities AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Opportunities AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Opportunities BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Opportunities BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)      ON crm.Activities,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Activities AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_RowWriteTarget(OwnerId, TeamId) ON crm.Activities AFTER UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowUpdate(OwnerId, TeamId)      ON crm.Activities BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_RowDelete(OwnerId, TeamId)      ON crm.Activities BEFORE DELETE,

  -- AuditLog is append-only: crm_app has no UPDATE/DELETE grant, so only the insert is guarded
  ADD FILTER PREDICATE sec.fn_RowAccess(OwnerId, TeamId)                ON crm.AuditLog,
  ADD BLOCK  PREDICATE sec.fn_AuditInsert(ChangedBy, OwnerId, TeamId)   ON crm.AuditLog AFTER INSERT
WITH (STATE = ON, SCHEMABINDING = ON);
GO

CREATE SECURITY POLICY sec.CrmConfigPolicy
  -- crm.Settings: no FILTER (fn_RowAccess reads it; the sign-in page reads app.name). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(SettingId) ON crm.Settings BEFORE DELETE,

  -- crm.BrandingAssets: no FILTER (logo shown on the public sign-in page). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(AssetId) ON crm.BrandingAssets BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(FxRateId) ON crm.FxRates,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(FxRateId) ON crm.FxRates BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(StageId) ON crm.PipelineStages,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages BEFORE UPDATE,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(StageId) ON crm.PipelineStages BEFORE DELETE,

  ADD FILTER PREDICATE sec.fn_HasContext(ValueId) ON crm.PicklistValues,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(ValueId) ON crm.PicklistValues AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(ValueId) ON crm.PicklistValues BEFORE UPDATE,

  -- crm.Teams: no FILTER (usp_UpsertUserOnLogin resolves teams before any context exists). Admin-only writes.
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(TeamId) ON crm.Teams AFTER INSERT,
  ADD BLOCK  PREDICATE sec.fn_AdminWrite(TeamId) ON crm.Teams BEFORE UPDATE
WITH (STATE = ON, SCHEMABINDING = ON);
GO
