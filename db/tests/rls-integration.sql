-- =====================================================================================================
-- db/tests/rls-integration.sql
-- Row-Level Security integration test for the CRM (docs/architecture/overview.md 5.5, ADR-0002).
--
-- RUN ONLY AGAINST A DEV/TEST DATABASE after migrations 001-004 (npm run db:migrate).
-- How:   npm run db:run-sql -- db/tests/rls-integration.sql       (one connection; prints PASS/FAIL per case)
--        or  sqlcmd -S <server> -d <database> -G -i db/tests/rls-integration.sql  (GO-separated batches, one session)
-- Run as: dbo / db_owner (it creates a WITHOUT LOGIN user and impersonates it with EXECUTE AS USER).
--
-- Layout: the script is a list of small GO-separated batches that share ONE session.
--   * batch 1 creates #ids; batch 2 opens ONE transaction and seeds data, storing every generated id in #ids;
--   * each test case is its own batch: it loads the ids it needs from #ids, takes a savepoint, runs, prints ONE result
--     row (Case, Result PASS/FAIL, Description, Detail) and rolls back to the savepoint;
--   * the last batches roll the whole transaction back, so no data is left behind.
-- Because each batch is compiled on its own, a typo breaks one case (reported with its line number), not the whole run.
-- Session context is set with @read_only = 0 here ONLY because the test switches identities in one session;
-- the app always uses @read_only = 1 (case 17 proves the read-only behaviour).
-- The test principal is a member of crm_app_role, i.e. it has exactly the grants of the app.
-- =====================================================================================================
SET NOCOUNT ON;
SET XACT_ABORT OFF;

IF DATABASE_PRINCIPAL_ID('crm_app_role') IS NULL THROW 50001, 'crm_app_role is missing: run migration 003 first', 1;

IF OBJECT_ID('tempdb..#ids') IS NOT NULL DROP TABLE #ids;
CREATE TABLE #ids (K varchar(30) NOT NULL PRIMARY KEY, Id int NULL, G uniqueidentifier NULL);
GO
-- batch 2: one transaction for the whole run, seed data, ids stored in #ids
DECLARE @A1 int, @M1 int, @U1 int, @U2 int, @U3 int, @U4 int, @N1 int, @N2 int, @T1 int, @T2 int;
DECLARE @G1 uniqueidentifier = NEWID(), @G2 uniqueidentifier = NEWID(), @DemoOid uniqueidentifier = NEWID();
DECLARE @Stage int, @SharedAcc int, @PicklistId int;

DECLARE @Acc_A1 int;
DECLARE @Acc_M1 int;
DECLARE @Acc_U1 int;
DECLARE @Acc_U2 int;
DECLARE @Acc_U3 int;
DECLARE @Acc_U4 int;
DECLARE @Acc_N1 int;
DECLARE @Acc_N2 int;
DECLARE @Con_A1 int;
DECLARE @Con_M1 int;
DECLARE @Con_U1 int;
DECLARE @Con_U2 int;
DECLARE @Con_U3 int;
DECLARE @Con_U4 int;
DECLARE @Con_N1 int;
DECLARE @Con_N2 int;
DECLARE @Opp_A1 int;
DECLARE @Opp_M1 int;
DECLARE @Opp_U1 int;
DECLARE @Opp_U2 int;
DECLARE @Opp_U3 int;
DECLARE @Opp_U4 int;
DECLARE @Opp_N1 int;
DECLARE @Opp_N2 int;
DECLARE @Act_A1 int;
DECLARE @Act_M1 int;
DECLARE @Act_U1 int;
DECLARE @Act_U2 int;
DECLARE @Act_U3 int;
DECLARE @Act_U4 int;
DECLARE @Act_N1 int;
DECLARE @Act_N2 int;

BEGIN TRAN;

-- ---------- seed (as Admin context; RLS applies to dbo as well) ----------
EXEC sp_set_session_context N'UserId', 1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;

INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team 1', @G1);
SET @T1 = SCOPE_IDENTITY();
INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team 2', @G2);
SET @T2 = SCOPE_IDENTITY();

INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST A1', 'Admin', NULL, 'none', 1); SET @A1 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST M1', 'Manager', @T1, 'ok', 1); SET @M1 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST U1', 'User', @T1, 'ok', 1); SET @U1 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST U2', 'User', @T1, 'ok', 1); SET @U2 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST U3', 'User', @T2, 'ok', 1); SET @U3 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST U4', 'User', @T1, 'ok', 0); SET @U4 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST N1', 'User', NULL, 'none', 1); SET @N1 = SCOPE_IDENTITY();
INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST N2', 'User', NULL, 'none', 1); SET @N2 = SCOPE_IDENTITY();

EXEC sp_set_session_context N'UserId', @A1, 0;
SELECT TOP (1) @Stage = StageId FROM crm.PipelineStages WHERE StageType = 'open' ORDER BY SortOrder;
IF @Stage IS NULL BEGIN INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST open', 1, 10, 'open'); SET @Stage = SCOPE_IDENTITY(); END
SELECT TOP (1) @PicklistId = PicklistId FROM crm.Picklists ORDER BY PicklistId;
IF @PicklistId IS NULL BEGIN INSERT crm.Picklists (Code, Name) VALUES ('rlstest', N'RLSTEST'); SET @PicklistId = SCOPE_IDENTITY(); END
INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST seed value', 32000);
INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('AAA', 'BBB', '2000-01-01', 1.5, @A1);

INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST shared', @A1, NULL, @A1, @A1);
SET @SharedAcc = SCOPE_IDENTITY();

INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @A1, NULL, @A1, @A1); SET @Acc_A1 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @A1, NULL, @A1, @A1); SET @Con_A1 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @A1, NULL, @A1, @A1); SET @Opp_A1 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @A1, NULL, @A1, @A1); SET @Act_A1 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @M1, @T1, @A1, @A1); SET @Acc_M1 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @M1, @T1, @A1, @A1); SET @Con_M1 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @M1, @T1, @A1, @A1); SET @Opp_M1 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @M1, @T1, @A1, @A1); SET @Act_M1 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T1, @A1, @A1); SET @Acc_U1 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T1, @A1, @A1); SET @Con_U1 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U1, @T1, @A1, @A1); SET @Opp_U1 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U1, @T1, @A1, @A1); SET @Act_U1 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @A1, @A1); SET @Acc_U2 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @A1, @A1); SET @Con_U2 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U2, @T1, @A1, @A1); SET @Opp_U2 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U2, @T1, @A1, @A1); SET @Act_U2 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T2, @A1, @A1); SET @Acc_U3 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T2, @A1, @A1); SET @Con_U3 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U3, @T2, @A1, @A1); SET @Opp_U3 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U3, @T2, @A1, @A1); SET @Act_U3 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @A1, @A1); SET @Acc_U4 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @A1, @A1); SET @Con_U4 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U4, @T1, @A1, @A1); SET @Opp_U4 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U4, @T1, @A1, @A1); SET @Act_U4 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @N1, NULL, @A1, @A1); SET @Acc_N1 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @N1, NULL, @A1, @A1); SET @Con_N1 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @N1, NULL, @A1, @A1); SET @Opp_N1 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @N1, NULL, @A1, @A1); SET @Act_N1 = SCOPE_IDENTITY();
INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @N2, NULL, @A1, @A1); SET @Acc_N2 = SCOPE_IDENTITY();
INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @N2, NULL, @A1, @A1); SET @Con_N2 = SCOPE_IDENTITY();
INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @N2, NULL, @A1, @A1); SET @Opp_N2 = SCOPE_IDENTITY();
INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @N2, NULL, @A1, @A1); SET @Act_N2 = SCOPE_IDENTITY();
INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'C', @U1, @T1, @A1, NEWID());

-- the test principal: exactly the app's grants
CREATE USER rlstest_app WITHOUT LOGIN;
ALTER ROLE crm_app_role ADD MEMBER rlstest_app;

INSERT #ids (K, Id) VALUES ('A1', @A1), ('M1', @M1), ('U1', @U1), ('U2', @U2), ('U3', @U3), ('U4', @U4), ('N1', @N1), ('N2', @N2), ('T1', @T1), ('T2', @T2), ('Stage', @Stage), ('SharedAcc', @SharedAcc), ('PicklistId', @PicklistId), ('Acc_A1', @Acc_A1), ('Acc_M1', @Acc_M1), ('Acc_U1', @Acc_U1), ('Acc_U2', @Acc_U2), ('Acc_U3', @Acc_U3), ('Acc_U4', @Acc_U4), ('Acc_N1', @Acc_N1), ('Acc_N2', @Acc_N2), ('Con_A1', @Con_A1), ('Con_M1', @Con_M1), ('Con_U1', @Con_U1), ('Con_U2', @Con_U2), ('Con_U3', @Con_U3), ('Con_U4', @Con_U4), ('Con_N1', @Con_N1), ('Con_N2', @Con_N2), ('Opp_A1', @Opp_A1), ('Opp_M1', @Opp_M1), ('Opp_U1', @Opp_U1), ('Opp_U2', @Opp_U2), ('Opp_U3', @Opp_U3), ('Opp_U4', @Opp_U4), ('Opp_N1', @Opp_N1), ('Opp_N2', @Opp_N2), ('Act_A1', @Act_A1), ('Act_M1', @Act_M1), ('Act_U1', @Act_U1), ('Act_U2', @Act_U2), ('Act_U3', @Act_U3), ('Act_U4', @Act_U4), ('Act_N1', @Act_N1), ('Act_N2', @Act_N2);
INSERT #ids (K, G) VALUES ('G1', @G1), ('G2', @G2), ('DemoOid', @DemoOid);
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
-- ---------- 1. no context: zero rows everywhere ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.Accounts;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Accounts (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.Contacts;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Contacts (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.Opportunities;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Opportunities (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.Activities;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Activities (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.AuditLog;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.AuditLog;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.AuditLog (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.FxRates;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.FxRates;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.FxRates (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.PipelineStages;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.PipelineStages;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.PipelineStages (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n2 = COUNT(*) FROM crm.PicklistValues;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.PicklistValues;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.PicklistValues (Admin sees rows: control)' AS Description, CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0; EXEC sp_set_session_context N'Role', N'Root', 0; EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1b' AS [Case], CASE WHEN @err = 0 AND @n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'unknown Role value fails closed (0 rows)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts U1 private: sees only own row' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 3 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts U1 team: sees U1+U2+U4 rows (not U3)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts U1 private: sees only own row' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 3 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts U1 team: sees U1+U2+U4 rows (not U3)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities U1 private: sees only own row' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 3 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities U1 team: sees U1+U2+U4 rows (not U3)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities U1 private: sees only own row' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @err = 0 AND @n = 3 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities U1 team: sees U1+U2+U4 rows (not U3)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @err = 0 AND @n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @U1 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @U1 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Accounts WHERE AccountId = @Acc_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @err = 0 AND @rc = 1 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Accounts WHERE AccountId = @Acc_U2;
  SELECT '6' AS [Case], CASE WHEN @err = 33504 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err; SET @err = 0;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2 WHERE AccountId = @Acc_U1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @n = 33504 AND @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U1 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U2 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  SET @e3 = 0; SET @e4 = 0;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U3 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U4 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @n = 1 AND @n2 = 1 AND @e3 = 33504 AND @e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @n, N' back_U2=', @n2, N' to_U3=', @e3, N' to_U4=', @e4) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @U1 WHERE AccountId = @Acc_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @M1 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @M1 WHERE AccountId = @Acc_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  SET @e5 = 0; SET @d1 = -1; SET @d2 = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U3; SET @d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2; SET @d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 0 AND @e5 = 33504 AND @d1 = 1 AND @d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @err, N' upd_U2=', @n, N' upd_U3=', @n2, N' team_move=', @e5, N' del_U2=', @d1, N' del_U3=', @d2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2, OwnerId = @U3 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @Acc_N1 int = (SELECT Id FROM #ids WHERE K = 'Acc_N1'),
  @Acc_N2 int = (SELECT Id FROM #ids WHERE K = 'Acc_N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @n;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @N1 WHERE AccountId = @Acc_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d3 = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @N1 WHERE AccountId = @Acc_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @u3 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @err = 0 AND @n2 = 1 AND @n = 0 AND @d3 = 0 AND @u3 = 1 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @err, N' visible=', @n2, N' upd_other=', @n, N' del_other=', @d3, N' upd_own=', @u3, N' del_own=', @rc) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @U1 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @U1 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Contacts WHERE ContactId = @Con_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @err = 0 AND @rc = 1 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Contacts WHERE ContactId = @Con_U2;
  SELECT '6' AS [Case], CASE WHEN @err = 33504 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U1 int = (SELECT Id FROM #ids WHERE K = 'Con_U1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err; SET @err = 0;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2 WHERE ContactId = @Con_U1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @n = 33504 AND @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U1 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U2 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  SET @e3 = 0; SET @e4 = 0;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U3 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U4 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @n = 1 AND @n2 = 1 AND @e3 = 33504 AND @e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @n, N' back_U2=', @n2, N' to_U3=', @e3, N' to_U4=', @e4) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U3 int = (SELECT Id FROM #ids WHERE K = 'Con_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @U1 WHERE ContactId = @Con_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2'),
  @Con_U3 int = (SELECT Id FROM #ids WHERE K = 'Con_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @M1 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @M1 WHERE ContactId = @Con_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  SET @e5 = 0; SET @d1 = -1; SET @d2 = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U3; SET @d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2; SET @d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 0 AND @e5 = 33504 AND @d1 = 1 AND @d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @err, N' upd_U2=', @n, N' upd_U3=', @n2, N' team_move=', @e5, N' del_U2=', @d1, N' del_U3=', @d2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2, OwnerId = @U3 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @Con_N1 int = (SELECT Id FROM #ids WHERE K = 'Con_N1'),
  @Con_N2 int = (SELECT Id FROM #ids WHERE K = 'Con_N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @n;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @N1 WHERE ContactId = @Con_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d3 = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @N1 WHERE ContactId = @Con_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @u3 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @err = 0 AND @n2 = 1 AND @n = 0 AND @d3 = 0 AND @u3 = 1 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @err, N' visible=', @n2, N' upd_other=', @n, N' del_other=', @d3, N' upd_own=', @u3, N' del_own=', @rc) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @U1 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @U1 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OpportunityId = @Opp_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @err = 0 AND @rc = 1 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
  SELECT '6' AS [Case], CASE WHEN @err = 33504 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U1 int = (SELECT Id FROM #ids WHERE K = 'Opp_U1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err; SET @err = 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2 WHERE OpportunityId = @Opp_U1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @n = 33504 AND @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U1 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U2 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  SET @e3 = 0; SET @e4 = 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U3 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U4 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @n = 1 AND @n2 = 1 AND @e3 = 33504 AND @e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @n, N' back_U2=', @n2, N' to_U3=', @e3, N' to_U4=', @e4) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U3 int = (SELECT Id FROM #ids WHERE K = 'Opp_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @U1 WHERE OpportunityId = @Opp_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2'),
  @Opp_U3 int = (SELECT Id FROM #ids WHERE K = 'Opp_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @M1 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @M1 WHERE OpportunityId = @Opp_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  SET @e5 = 0; SET @d1 = -1; SET @d2 = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U3; SET @d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2; SET @d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 0 AND @e5 = 33504 AND @d1 = 1 AND @d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @err, N' upd_U2=', @n, N' upd_U3=', @n2, N' team_move=', @e5, N' del_U2=', @d1, N' del_U3=', @d2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2, OwnerId = @U3 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @Opp_N1 int = (SELECT Id FROM #ids WHERE K = 'Opp_N1'),
  @Opp_N2 int = (SELECT Id FROM #ids WHERE K = 'Opp_N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @n;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @N1 WHERE OpportunityId = @Opp_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d3 = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @N1 WHERE OpportunityId = @Opp_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @u3 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @err = 0 AND @n2 = 1 AND @n = 0 AND @d3 = 0 AND @u3 = 1 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @err, N' visible=', @n2, N' upd_other=', @n, N' del_other=', @d3, N' upd_own=', @u3, N' del_own=', @rc) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @U1 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @U1 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Activities WHERE ActivityId = @Act_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @err = 0 AND @rc = 1 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @n = COUNT(*) FROM crm.Activities WHERE ActivityId = @Act_U2;
  SELECT '6' AS [Case], CASE WHEN @err = 33504 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U1 int = (SELECT Id FROM #ids WHERE K = 'Act_U1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err; SET @err = 0;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2 WHERE ActivityId = @Act_U1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @n = 33504 AND @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U1 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U2 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @err * 10 + @rc; SET @err = 0; SET @rc = -1;
  SET @e3 = 0; SET @e4 = 0;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U3 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U4 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @n = 1 AND @n2 = 1 AND @e3 = 33504 AND @e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @n, N' back_U2=', @n2, N' to_U3=', @e3, N' to_U4=', @e4) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U3 int = (SELECT Id FROM #ids WHERE K = 'Act_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @U1 WHERE ActivityId = @Act_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @err = 0 AND @n = 0 AND @rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U3, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U1, @T2, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U2, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', @U4, @T1, @U1, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2'),
  @Act_U3 int = (SELECT Id FROM #ids WHERE K = 'Act_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @M1 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @M1 WHERE ActivityId = @Act_U3;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  SET @e5 = 0; SET @d1 = -1; SET @d2 = -1;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U3; SET @d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2; SET @d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 0 AND @e5 = 33504 AND @d1 = 1 AND @d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @err, N' upd_U2=', @n, N' upd_U3=', @n2, N' team_move=', @e5, N' del_U2=', @d1, N' del_U3=', @d2) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2, OwnerId = @U3 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @Act_N1 int = (SELECT Id FROM #ids WHERE K = 'Act_N1'),
  @Act_N2 int = (SELECT Id FROM #ids WHERE K = 'Act_N2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @n;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @N1 WHERE ActivityId = @Act_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_N2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d3 = @rc; SET @rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @N1 WHERE ActivityId = @Act_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @u3 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_N1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @err = 0 AND @n2 = 1 AND @n = 0 AND @d3 = 0 AND @u3 = 1 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @err, N' visible=', @n2, N' upd_other=', @n, N' del_other=', @d3, N' upd_own=', @u3, N' del_own=', @rc) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
-- ---------- 14. config tables: Admin-only writes ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @M1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @A1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @U1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @M1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @A1);
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @err = 0 AND @rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: Admin succeeds' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
-- ---------- 15. AuditLog insert guard ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T1, @U2, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot write an audit row with ChangedBy = U2' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'U', @U2, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 (team mode) can audit a change to U2 record (ChangedBy = U1)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U3, 'U', @U3, @T2, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot audit a record of another team' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.AuditLog SET Action = 'D';
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no UPDATE grant (append-only)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
-- ---------- 16. app role cannot change security or read migration state ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    ALTER SECURITY POLICY sec.CrmRowPolicy WITH (STATE = OFF);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @n = COUNT(*) FROM sys.security_policies WHERE name = N'CrmRowPolicy' AND is_enabled = 1;
  SELECT '16' AS [Case], CASE WHEN @err <> 0 AND @n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot ALTER SECURITY POLICY (policy still enabled)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM dbo.SchemaVersions;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @err = 229 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot SELECT dbo.SchemaVersions (permission denied, 229)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Users (EntraOid, TenantId, DisplayName, Role) VALUES (NEWID(), NEWID(), N'x', 'Admin');
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @err = 229 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot INSERT crm.Users (229)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Users SET Role = 'Admin' WHERE UserId = @U1;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @err = 229 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot UPDATE crm.Users (229)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @n = COUNT(*) FROM sec.fn_RowAccess(1, 1);
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role has no direct access to schema sec (error 229/208)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
-- ---------- 18. crm.usp_UpsertUserOnLogin team resolution (exactly one team group) ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  DELETE FROM @P;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @j = CONCAT(N'["', @G1, N'"]');
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '18a' AS [Case], CASE WHEN @err = 0 AND (SELECT TeamId FROM @P) = @T1 AND (SELECT TeamStatus FROM @P) = 'ok' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: one linked group -> that team, status ok' AS Description, CONCAT(N'err=', @err, N' team=', (SELECT TeamId FROM @P), N' status=', (SELECT TeamStatus FROM @P)) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @G2 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G2'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  DELETE FROM @P;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @j = CONCAT(N'["', @G1, N'","', @G2, N'"]');
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '18b' AS [Case], CASE WHEN @err = 0 AND (SELECT TeamId FROM @P) IS NULL AND (SELECT TeamStatus FROM @P) = 'multiple' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: two linked groups -> TeamId NULL, status multiple (fail closed, no lowest-id rule)' AS Description, CONCAT(N'err=', @err, N' team=', (SELECT TeamId FROM @P), N' status=', (SELECT TeamStatus FROM @P)) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  DELETE FROM @P;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @j = N'["00000000-0000-0000-0000-000000000099"]';
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '18c' AS [Case], CASE WHEN @err = 0 AND (SELECT TeamId FROM @P) IS NULL AND (SELECT TeamStatus FROM @P) = 'none' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: no linked group -> TeamId NULL, status none' AS Description, CONCAT(N'err=', @err, N' team=', (SELECT TeamId FROM @P), N' status=', (SELECT TeamStatus FROM @P)) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  DELETE FROM @P;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @j = N'[]';
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = 1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '18d' AS [Case], CASE WHEN @err = 0 AND (SELECT TeamId FROM @P) IS NULL AND (SELECT TeamStatus FROM @P) = 'overage' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: groups overage -> TeamId NULL, status overage' AS Description, CONCAT(N'err=', @err, N' team=', (SELECT TeamId FROM @P), N' status=', (SELECT TeamStatus FROM @P)) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  DELETE FROM @P;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @j = CONCAT(N'["', @G1, N'"]');
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = 1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '18e' AS [Case], CASE WHEN @err = 0 AND (SELECT TeamId FROM @P) IS NULL AND (SELECT TeamStatus FROM @P) = 'overage' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: overage flag wins even if a group is present' AS Description, CONCAT(N'err=', @err, N' team=', (SELECT TeamId FROM @P), N' status=', (SELECT TeamStatus FROM @P)) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
-- ---------- 15b. AuditLog: owner = caller must ALSO carry the caller's team; no UPDATE/DELETE even for dbo/Admin ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T2, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot write an audit row owned by self but carrying another team (33504)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 can write an audit row for own record with own team' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_N1 int = (SELECT Id FROM #ids WHERE K = 'Acc_N1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, @T1, @N1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no-team user cannot claim a team on an audit row (33504)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @Acc_N1 int = (SELECT Id FROM #ids WHERE K = 'Acc_N1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, NULL, @N1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @err = 0 AND @rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no-team user can audit own no-team record' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.AuditLog SET Action = Action;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SELECT '15b' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: UPDATE is blocked for everyone, even dbo with Admin context (always-false block predicate, 33504)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    DELETE FROM crm.AuditLog;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SELECT '15b' AS [Case], CASE WHEN @err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: DELETE is blocked for everyone, even dbo with Admin context (always-false block predicate, 33504)' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
-- ---------- 20. team mode: User takes over a team-mate record, then deletes it as owner; both steps audited ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U1, ModifiedBy = @U1 WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d1 = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d2 = @rc; SET @rc = -1;
  BEGIN TRY
    SELECT @u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'account' AND EntityId = @Acc_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 1 AND @d1 = 1 AND @d2 = 1 AND @u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @err, N' takeover=', @n, N' audit1=', @n2, N' delete=', @d1, N' audit2=', @d2, N' audit_rows=', @u3) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U1, ModifiedBy = @U1 WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('contact', @Con_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d1 = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('contact', @Con_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d2 = @rc; SET @rc = -1;
  BEGIN TRY
    SELECT @u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'contact' AND EntityId = @Con_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 1 AND @d1 = 1 AND @d2 = 1 AND @u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @err, N' takeover=', @n, N' audit1=', @n2, N' delete=', @d1, N' audit2=', @d2, N' audit_rows=', @u3) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U1, ModifiedBy = @U1 WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('opportunity', @Opp_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d1 = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('opportunity', @Opp_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d2 = @rc; SET @rc = -1;
  BEGIN TRY
    SELECT @u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'opportunity' AND EntityId = @Opp_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 1 AND @d1 = 1 AND @d2 = 1 AND @u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @err, N' takeover=', @n, N' audit1=', @n2, N' delete=', @d1, N' audit2=', @d2, N' audit_rows=', @u3) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U1, ModifiedBy = @U1 WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('activity', @Act_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @n2 = @rc; SET @rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2;
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d1 = @rc; SET @rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('activity', @Act_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SET @d2 = @rc; SET @rc = -1;
  BEGIN TRY
    SELECT @u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'activity' AND EntityId = @Act_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @err = 0 AND @n = 1 AND @n2 = 1 AND @d1 = 1 AND @d2 = 1 AND @u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @err, N' takeover=', @n, N' audit1=', @n2, N' delete=', @d1, N' audit2=', @d2, N' audit_rows=', @u3) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
DECLARE @j nvarchar(max), @o uniqueidentifier, @err int = 0, @rc int = -1, @n int = -1, @n2 int, @e3 int, @e4 int, @e5 int, @d1 int, @d2 int, @d3 int, @u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
-- ---------- 19. usp_UpsertUserOnLogin does not reactivate a deactivated user ----------
SAVE TRANSACTION sp_case;
  SET @err = 0; SET @rc = -1; SET @n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @o = EntraOid FROM crm.Users WHERE UserId = @U1;
  UPDATE crm.Users SET IsActive = 0 WHERE UserId = @U1;
  DELETE FROM @P;
  BEGIN TRY
    INSERT @P EXEC crm.usp_UpsertUserOnLogin @EntraOid = @o, @TenantId = @o, @DisplayName = N'RLSTEST U1', @Email = NULL, @Role = 'User', @GroupIdsJson = N'[]', @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
  SELECT @n = IsActive FROM crm.Users WHERE UserId = @U1;
  SELECT '19' AS [Case], CASE WHEN @err = 50011 AND @n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'deactivated user: login procedure throws 50011 and does not set IsActive = 1' AS Description, CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n) AS Detail;
END TRY BEGIN CATCH
  REVERT;
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
GO
-- everything done: undo ALL seed data and test writes
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SELECT 'ROLLBACK' AS [Info], CASE WHEN @@TRANCOUNT = 0 THEN 'transaction rolled back, no data left behind' ELSE 'WARNING: a transaction is still open' END AS Detail;
GO
-- 17. SESSION_CONTEXT read-only behaviour (runs last: it locks the UserId key for this session)
DECLARE @err int = 0;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', 1, 1;
  EXEC sp_set_session_context N'UserId', 2, 1;
END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;
SELECT '17' AS [Case], CASE WHEN @err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'second sp_set_session_context(UserId, read_only=1) fails (expected error 15664)' AS Description, CONCAT(N'err=', @err) AS Detail;
GO
