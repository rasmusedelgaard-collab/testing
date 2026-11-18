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
GRANT CONNECT TO rlstest_app;
ALTER ROLE crm_app_role ADD MEMBER rlstest_app;

INSERT #ids (K, Id) VALUES ('A1', @A1), ('M1', @M1), ('U1', @U1), ('U2', @U2), ('U3', @U3), ('U4', @U4), ('N1', @N1), ('N2', @N2), ('T1', @T1), ('T2', @T2), ('Stage', @Stage), ('SharedAcc', @SharedAcc), ('PicklistId', @PicklistId), ('Acc_A1', @Acc_A1), ('Acc_M1', @Acc_M1), ('Acc_U1', @Acc_U1), ('Acc_U2', @Acc_U2), ('Acc_U3', @Acc_U3), ('Acc_U4', @Acc_U4), ('Acc_N1', @Acc_N1), ('Acc_N2', @Acc_N2), ('Con_A1', @Con_A1), ('Con_M1', @Con_M1), ('Con_U1', @Con_U1), ('Con_U2', @Con_U2), ('Con_U3', @Con_U3), ('Con_U4', @Con_U4), ('Con_N1', @Con_N1), ('Con_N2', @Con_N2), ('Opp_A1', @Opp_A1), ('Opp_M1', @Opp_M1), ('Opp_U1', @Opp_U1), ('Opp_U2', @Opp_U2), ('Opp_U3', @Opp_U3), ('Opp_U4', @Opp_U4), ('Opp_N1', @Opp_N1), ('Opp_N2', @Opp_N2), ('Act_A1', @Act_A1), ('Act_M1', @Act_M1), ('Act_U1', @Act_U1), ('Act_U2', @Act_U2), ('Act_U3', @Act_U3), ('Act_U4', @Act_U4), ('Act_N1', @Act_N1), ('Act_N2', @Act_N2);
INSERT #ids (K, G) VALUES ('G1', @G1), ('G2', @G2), ('DemoOid', @DemoOid);
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
-- ---------- 1. no context: zero rows everywhere ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.Accounts;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Accounts (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.Contacts;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Contacts (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.Opportunities;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Opportunities (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.Activities;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.Activities (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.AuditLog;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.AuditLog;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.AuditLog (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.FxRates;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.FxRates;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.FxRates (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.PipelineStages;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.PipelineStages;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.PipelineStages (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n2 = COUNT(*) FROM crm.PicklistValues;
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.PicklistValues;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_n2 > 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'no context returns 0 rows from crm.PicklistValues (Admin sees rows: control)' AS Description, CONCAT(N'err=', @s_err, N' rows_without_context=', @s_n, N' rows_as_admin=', @s_n2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0; EXEC sp_set_session_context N'Role', N'Root', 0; EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '1b' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'unknown Role value fails closed (0 rows)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2) AND AccountId <> @SharedAcc;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts U1 private: sees only own row' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2) AND AccountId <> @SharedAcc;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts U1 team: sees M1+U1+U2+U4 rows (own team, not U3)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2) AND AccountId <> @SharedAcc;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2) AND AccountId <> @SharedAcc;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts U1 private: sees only own row' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts U1 team: sees M1+U1+U2+U4 rows (own team, not U3)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities U1 private: sees only own row' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities U1 team: sees M1+U1+U2+U4 rows (own team, not U3)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities U1 private: sees only own row' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '2' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities U1 team: sees M1+U1+U2+U4 rows (own team, not U3)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 4 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities M1 (private mode) sees team 1 rows (M1,U1,U2,U4)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @N2 int = (SELECT Id FROM #ids WHERE K = 'N2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'private');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '3' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 8 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities A1 sees all 8 seeded rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE AccountId = @Acc_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE AccountId = @Acc_U2;
  SELECT '6' AS [Case], CASE WHEN @s_err = 33504 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err; SET @s_err = 0;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2 WHERE AccountId = @Acc_U1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @s_n = 33504 AND @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U2 WHERE AccountId = @Acc_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  SET @s_e3 = 0; SET @s_e4 = 0;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U3 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @s_e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Accounts SET OwnerId = @U4 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @s_e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @s_n = 1 AND @s_n2 = 1 AND @s_e3 = 33504 AND @s_e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @s_n, N' back_U2=', @s_n2, N' to_U3=', @s_e3, N' to_U4=', @s_e4) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @M1 WHERE AccountId = @Acc_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  SET @s_e5 = 0; SET @s_d1 = -1; SET @s_d2 = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET TeamId = @T2 WHERE AccountId = @Acc_U2;
  END TRY BEGIN CATCH SET @s_e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U3; SET @s_d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2; SET @s_d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 0 AND @s_e5 = 33504 AND @s_d1 = 1 AND @s_d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @s_err, N' upd_U2=', @s_n, N' upd_U3=', @s_n2, N' team_move=', @s_e5, N' del_U2=', @s_d1, N' del_U3=', @s_d2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Accounts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_n;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @N1 WHERE AccountId = @Acc_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Accounts SET Name = N'RLSTEST upd', ModifiedBy = @N1 WHERE AccountId = @Acc_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_u3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @s_err = 0 AND @s_n2 = 1 AND @s_n = 0 AND @s_d3 = 0 AND @s_u3 = 1 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @s_err, N' visible=', @s_n2, N' upd_other=', @s_n, N' del_other=', @s_d3, N' upd_own=', @s_u3, N' del_own=', @s_rc) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE ContactId = @Con_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE ContactId = @Con_U2;
  SELECT '6' AS [Case], CASE WHEN @s_err = 33504 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U1 int = (SELECT Id FROM #ids WHERE K = 'Con_U1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err; SET @s_err = 0;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2 WHERE ContactId = @Con_U1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @s_n = 33504 AND @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U2 WHERE ContactId = @Con_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  SET @s_e3 = 0; SET @s_e4 = 0;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U3 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @s_e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Contacts SET OwnerId = @U4 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @s_e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @s_n = 1 AND @s_n2 = 1 AND @s_e3 = 33504 AND @s_e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @s_n, N' back_U2=', @s_n2, N' to_U3=', @s_e3, N' to_U4=', @s_e4) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U3 int = (SELECT Id FROM #ids WHERE K = 'Con_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2'),
  @Con_U3 int = (SELECT Id FROM #ids WHERE K = 'Con_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @M1 WHERE ContactId = @Con_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  SET @s_e5 = 0; SET @s_d1 = -1; SET @s_d2 = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET TeamId = @T2 WHERE ContactId = @Con_U2;
  END TRY BEGIN CATCH SET @s_e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U3; SET @s_d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2; SET @s_d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 0 AND @s_e5 = 33504 AND @s_d1 = 1 AND @s_d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @s_err, N' upd_U2=', @s_n, N' upd_U3=', @s_n2, N' team_move=', @s_e5, N' del_U2=', @s_d1, N' del_U3=', @s_d2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Contacts WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_n;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @N1 WHERE ContactId = @Con_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Contacts SET LastName = N'RLSTEST upd', ModifiedBy = @N1 WHERE ContactId = @Con_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_u3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @s_err = 0 AND @s_n2 = 1 AND @s_n = 0 AND @s_d3 = 0 AND @s_u3 = 1 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @s_err, N' visible=', @s_n2, N' upd_other=', @s_n, N' del_other=', @s_d3, N' upd_own=', @s_u3, N' del_own=', @s_rc) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OpportunityId = @Opp_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
  SELECT '6' AS [Case], CASE WHEN @s_err = 33504 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U1 int = (SELECT Id FROM #ids WHERE K = 'Opp_U1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err; SET @s_err = 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2 WHERE OpportunityId = @Opp_U1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @s_n = 33504 AND @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U2 WHERE OpportunityId = @Opp_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  SET @s_e3 = 0; SET @s_e4 = 0;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U3 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @s_e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Opportunities SET OwnerId = @U4 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @s_e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @s_n = 1 AND @s_n2 = 1 AND @s_e3 = 33504 AND @s_e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @s_n, N' back_U2=', @s_n2, N' to_U3=', @s_e3, N' to_U4=', @s_e4) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U3 int = (SELECT Id FROM #ids WHERE K = 'Opp_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage'),
  @SharedAcc int = (SELECT Id FROM #ids WHERE K = 'SharedAcc');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2'),
  @Opp_U3 int = (SELECT Id FROM #ids WHERE K = 'Opp_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @M1 WHERE OpportunityId = @Opp_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  SET @s_e5 = 0; SET @s_d1 = -1; SET @s_d2 = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET TeamId = @T2 WHERE OpportunityId = @Opp_U2;
  END TRY BEGIN CATCH SET @s_e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U3; SET @s_d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2; SET @s_d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 0 AND @s_e5 = 33504 AND @s_d1 = 1 AND @s_d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @s_err, N' upd_U2=', @s_n, N' upd_U3=', @s_n2, N' team_move=', @s_e5, N' del_U2=', @s_d1, N' del_U3=', @s_d2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Opportunities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_n;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @N1 WHERE OpportunityId = @Opp_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Opportunities SET Amount = 999, ModifiedBy = @N1 WHERE OpportunityId = @Opp_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_u3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @s_err = 0 AND @s_n2 = 1 AND @s_n = 0 AND @s_d3 = 0 AND @s_u3 = 1 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @s_err, N' visible=', @s_n2, N' upd_other=', @s_n, N' del_other=', @s_d3, N' upd_own=', @s_u3, N' del_own=', @s_rc) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '4' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Activities WHERE ActivityId = @Act_U2 AND ModifiedBy = @U1;
  SELECT '5' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 may UPDATE U2 row; ModifiedBy = U1' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_n = COUNT(*) FROM crm.Activities WHERE ActivityId = @Act_U2;
  SELECT '6' AS [Case], CASE WHEN @s_err = 33504 AND @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 DELETE of U2 row blocked with 33504, row still exists' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U1 int = (SELECT Id FROM #ids WHERE K = 'Act_U1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err; SET @s_err = 0;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2 WHERE ActivityId = @Act_U1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '7' AS [Case], CASE WHEN @s_n = 33504 AND @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U2 WHERE ActivityId = @Act_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_err * 10 + @s_rc; SET @s_err = 0; SET @s_rc = -1;
  SET @s_e3 = 0; SET @s_e4 = 0;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U3 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @s_e3 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    UPDATE crm.Activities SET OwnerId = @U4 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @s_e4 = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '8' AS [Case], CASE WHEN @s_n = 1 AND @s_n2 = 1 AND @s_e3 = 33504 AND @s_e4 = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504' AS Description, CONCAT(N'to_U1=', @s_n, N' back_U2=', @s_n2, N' to_U3=', @s_e3, N' to_U4=', @s_e4) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U3 int = (SELECT Id FROM #ids WHERE K = 'Act_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '9' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 0 AND @s_rc = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities private: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U3 (other team) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with TeamId 2 fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U2 (active team-mate) succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U4 int = (SELECT Id FROM #ids WHERE K = 'U4'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '10' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team: U1 INSERT with owner U4 (inactive) fails with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2'),
  @Act_U3 int = (SELECT Id FROM #ids WHERE K = 'Act_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @M1 WHERE ActivityId = @Act_U3;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  SET @s_e5 = 0; SET @s_d1 = -1; SET @s_d2 = -1;
  BEGIN TRY
    UPDATE crm.Activities SET TeamId = @T2 WHERE ActivityId = @Act_U2;
  END TRY BEGIN CATCH SET @s_e5 = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U3; SET @s_d2 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2; SET @s_d1 = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '11' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 0 AND @s_e5 = 33504 AND @s_d1 = 1 AND @s_d2 = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504' AS Description, CONCAT(N'err=', @s_err, N' upd_U2=', @s_n, N' upd_U3=', @s_n2, N' team_move=', @s_e5, N' del_U2=', @s_d1, N' del_U3=', @s_d2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '12' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities A1: may move U2 row to team 2 with owner U3' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
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
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'team' WHERE SettingKey = 'visibility.mode'
  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'team');
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    SELECT @s_n = COUNT(*) FROM crm.Activities WHERE OwnerId IN (@A1,@M1,@U1,@U2,@U3,@U4,@N1,@N2);
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_n;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @N1 WHERE ActivityId = @Act_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_N2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    UPDATE crm.Activities SET Subject = N'RLSTEST upd', ModifiedBy = @N1 WHERE ActivityId = @Act_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_u3 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_N1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '13' AS [Case], CASE WHEN @s_err = 0 AND @s_n2 = 1 AND @s_n = 0 AND @s_d3 = 0 AND @s_u3 = 1 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable' AS Description, CONCAT(N'err=', @s_err, N' visible=', @s_n2, N' upd_other=', @s_n, N' del_other=', @s_d3, N' upd_own=', @s_u3, N' del_own=', @s_rc) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
-- ---------- 14. config tables: Admin-only writes ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode';
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Settings update: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @U1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @M1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, @A1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'FxRates insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open');
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @Stage int = (SELECT Id FROM #ids WHERE K = 'Stage');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PipelineStages update: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @PicklistId int = (SELECT Id FROM #ids WHERE K = 'PicklistId');
DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'PicklistValues insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Teams update: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @U1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: User U1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @M1 int = (SELECT Id FROM #ids WHERE K = 'M1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @M1, 0;
  EXEC sp_set_session_context N'Role', N'Manager', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @M1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: Manager M1 blocked with 33504' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @A1, 0;
  EXEC sp_set_session_context N'Role', N'Admin', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), @A1);
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '14' AS [Case], CASE WHEN @s_err = 0 AND @s_rc >= 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'BrandingAssets insert: Admin succeeds' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
-- ---------- 15. AuditLog insert guard ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot write an audit row with ChangedBy = U2' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 (team mode) can audit a change to U2 record (ChangedBy = U1)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U3 int = (SELECT Id FROM #ids WHERE K = 'U3'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U3 int = (SELECT Id FROM #ids WHERE K = 'Acc_U3');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot audit a record of another team' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15' AS [Case], CASE WHEN @s_err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no UPDATE grant (append-only)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
-- ---------- 16. app role cannot change security or read migration state ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'sec.CrmRowPolicy', N'OBJECT', N'ALTER');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot ALTER SECURITY POLICY (object permission)' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY SECURITY POLICY');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role has no database-level ALTER ANY SECURITY POLICY' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'sec.CrmConfigPolicy', N'OBJECT', N'ALTER');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot ALTER the other policy either' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'dbo.SchemaVersions', N'OBJECT', N'SELECT');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot SELECT dbo.SchemaVersions' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'INSERT');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot INSERT crm.Users' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'UPDATE');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot UPDATE crm.Users' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'DELETE');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot DELETE crm.Users' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'SELECT');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role can SELECT crm.Users (control: the owner picker needs it)' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'sec.fn_RowAccess', N'OBJECT', N'SELECT');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role has no access to schema sec (predicate functions)' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = CASE WHEN HAS_PERMS_BY_NAME(N'crm.AuditLog', N'OBJECT', N'UPDATE') = 0 AND HAS_PERMS_BY_NAME(N'crm.AuditLog', N'OBJECT', N'DELETE') = 0 THEN 0 ELSE 1 END;
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot UPDATE or DELETE the append-only AuditLog' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  SELECT @s_n = HAS_PERMS_BY_NAME(N'crm.Accounts', N'OBJECT', N'CONTROL');
  REVERT;
  SELECT '16' AS [Case], CASE WHEN @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'crm_app_role cannot take ownership or control of crm' AS Description, CONCAT(N'perm=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
-- ---------- 18. crm.usp_UpsertUserOnLogin team resolution (exactly one team group) ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @s_j = CONCAT(N'["', @G1, N'"]');
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @s_j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @s_n = TeamId, @s_o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;
  SELECT '18a' AS [Case], CASE WHEN @s_err = 0 AND @s_n = @T1 AND @s_o2 = 'ok' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: one linked group -> that team, status ok' AS Description, CONCAT(N'err=', @s_err, N' team=', @s_n, N' status=', @s_o2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @G2 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G2'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @s_j = CONCAT(N'["', @G1, N'","', @G2, N'"]');
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @s_j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @s_n = TeamId, @s_o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;
  SELECT '18b' AS [Case], CASE WHEN @s_err = 0 AND @s_n IS NULL AND @s_o2 = 'multiple' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: two linked groups -> TeamId NULL, status multiple (fail closed, no lowest-id rule)' AS Description, CONCAT(N'err=', @s_err, N' team=', @s_n, N' status=', @s_o2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @s_j = N'["00000000-0000-0000-0000-000000000099"]';
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @s_j, @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @s_n = TeamId, @s_o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;
  SELECT '18c' AS [Case], CASE WHEN @s_err = 0 AND @s_n IS NULL AND @s_o2 = 'none' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: no linked group -> TeamId NULL, status none' AS Description, CONCAT(N'err=', @s_err, N' team=', @s_n, N' status=', @s_o2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @s_j = N'[]';
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @s_j, @GroupsOverage = 1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @s_n = TeamId, @s_o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;
  SELECT '18d' AS [Case], CASE WHEN @s_err = 0 AND @s_n IS NULL AND @s_o2 = 'overage' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: groups overage -> TeamId NULL, status overage' AS Description, CONCAT(N'err=', @s_err, N' team=', @s_n, N' status=', @s_o2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @G1 uniqueidentifier = (SELECT G FROM #ids WHERE K = 'G1'),
  @DemoOid uniqueidentifier = (SELECT G FROM #ids WHERE K = 'DemoOid');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    SET @s_j = CONCAT(N'["', @G1, N'"]');
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @s_j, @GroupsOverage = 1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT @s_n = TeamId, @s_o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;
  SELECT '18e' AS [Case], CASE WHEN @s_err = 0 AND @s_n IS NULL AND @s_o2 = 'overage' THEN 'PASS' ELSE 'FAIL' END AS Result, N'usp_UpsertUserOnLogin: overage flag wins even if a group is present' AS Description, CONCAT(N'err=', @s_err, N' team=', @s_n, N' status=', @s_o2) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @T2 int = (SELECT Id FROM #ids WHERE K = 'T2'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
-- ---------- 15b. AuditLog: owner = caller must ALSO carry the caller's team; no UPDATE/DELETE even for dbo/Admin ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T2, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 cannot write an audit row owned by self but carrying another team (33504)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U1 int = (SELECT Id FROM #ids WHERE K = 'Acc_U1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @U1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', @T1, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: U1 can write an audit row for own record with own team' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_N1 int = (SELECT Id FROM #ids WHERE K = 'Acc_N1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, @T1, @N1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no-team user cannot claim a team on an audit row (33504)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @N1 int = (SELECT Id FROM #ids WHERE K = 'N1'),
  @Acc_N1 int = (SELECT Id FROM #ids WHERE K = 'Acc_N1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXECUTE AS USER = N'rlstest_app';
  EXEC sp_set_session_context N'UserId', @N1, 0;
  EXEC sp_set_session_context N'Role', N'User', 0;
  EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, NULL, @N1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 0 AND @s_rc = 1 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: no-team user can audit own no-team record' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    UPDATE crm.AuditLog SET Action = Action;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: UPDATE is blocked for everyone, even dbo with Admin context (always-false block predicate, 33504)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  BEGIN TRY
    DELETE FROM crm.AuditLog;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SELECT '15b' AS [Case], CASE WHEN @s_err = 33504 THEN 'PASS' ELSE 'FAIL' END AS Result, N'AuditLog: DELETE is blocked for everyone, even dbo with Admin context (always-false block predicate, 33504)' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Acc_U2 int = (SELECT Id FROM #ids WHERE K = 'Acc_U2');
-- ---------- 20. team mode: User takes over a team-mate record, then deletes it as owner; both steps audited ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Accounts WHERE AccountId = @Acc_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d1 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    SELECT @s_u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'account' AND EntityId = @Acc_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 1 AND @s_d1 = 1 AND @s_d2 = 1 AND @s_u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Accounts team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @s_err, N' takeover=', @s_n, N' audit1=', @s_n2, N' delete=', @s_d1, N' audit2=', @s_d2, N' audit_rows=', @s_u3) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Con_U2 int = (SELECT Id FROM #ids WHERE K = 'Con_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('contact', @Con_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Contacts WHERE ContactId = @Con_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d1 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('contact', @Con_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    SELECT @s_u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'contact' AND EntityId = @Con_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 1 AND @s_d1 = 1 AND @s_d2 = 1 AND @s_u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Contacts team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @s_err, N' takeover=', @s_n, N' audit1=', @s_n2, N' delete=', @s_d1, N' audit2=', @s_d2, N' audit_rows=', @s_u3) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Opp_U2 int = (SELECT Id FROM #ids WHERE K = 'Opp_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('opportunity', @Opp_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Opportunities WHERE OpportunityId = @Opp_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d1 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('opportunity', @Opp_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    SELECT @s_u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'opportunity' AND EntityId = @Opp_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 1 AND @s_d1 = 1 AND @s_d2 = 1 AND @s_u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Opportunities team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @s_err, N' takeover=', @s_n, N' audit1=', @s_n2, N' delete=', @s_d1, N' audit2=', @s_d2, N' audit_rows=', @s_u3) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1'),
  @U2 int = (SELECT Id FROM #ids WHERE K = 'U2'),
  @T1 int = (SELECT Id FROM #ids WHERE K = 'T1'),
  @Act_U2 int = (SELECT Id FROM #ids WHERE K = 'Act_U2');
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
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
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('activity', @Act_U2, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_n2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    DELETE FROM crm.Activities WHERE ActivityId = @Act_U2;
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d1 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('activity', @Act_U2, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID());
    SET @s_rc = @@ROWCOUNT;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SET @s_d2 = @s_rc; SET @s_rc = -1;
  BEGIN TRY
    SELECT @s_u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = 'activity' AND EntityId = @Act_U2 AND ChangedBy = @U1;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  REVERT;
  SELECT '20' AS [Case], CASE WHEN @s_err = 0 AND @s_n = 1 AND @s_n2 = 1 AND @s_d1 = 1 AND @s_d2 = 1 AND @s_u3 = 2 THEN 'PASS' ELSE 'FAIL' END AS Result, N'Activities team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible' AS Description, CONCAT(N'err=', @s_err, N' takeover=', @s_n, N' audit1=', @s_n2, N' delete=', @s_d1, N' audit2=', @s_d2, N' audit_rows=', @s_u3) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;
END
GO
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;
DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;
DECLARE @A1 int = (SELECT Id FROM #ids WHERE K = 'A1'),
  @U1 int = (SELECT Id FROM #ids WHERE K = 'U1');
-- ---------- 19. usp_UpsertUserOnLogin does not reactivate a deactivated user ----------
SAVE TRANSACTION sp_case;
  SET @s_err = 0; SET @s_rc = -1; SET @s_n = -1;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;
  SELECT @s_o = EntraOid FROM crm.Users WHERE UserId = @U1;
  UPDATE crm.Users SET IsActive = 0 WHERE UserId = @U1;
  BEGIN TRY
    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @s_o, @TenantId = @s_o, @DisplayName = N'RLSTEST U1', @Email = NULL, @Role = 'User', @GroupIdsJson = N'[]', @GroupsOverage = 0;
  END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
  SELECT @s_n = IsActive FROM crm.Users WHERE UserId = @U1;
  SELECT '19' AS [Case], CASE WHEN @s_err = 50011 AND @s_n = 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'deactivated user: login procedure throws 50011 and does not set IsActive = 1' AS Description, CONCAT(N'err=', @s_err, N' rc=', @s_rc, N' n=', @s_n) AS Detail;
END TRY BEGIN CATCH
  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start
  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;
END CATCH;
WHILE USER_NAME() = N'rlstest_app' REVERT;
IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;
ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue
  ROLLBACK TRANSACTION;
  SELECT 'ABORT' AS [Case], 'PASS' AS Result, N'expected: the procedure THROW under XACT_ABORT ON dooms the transaction; rolled back (this case runs last)' AS Description, N'' AS Detail;
END
GO
-- everything done: undo ALL seed data and test writes
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SELECT 'ROLLBACK' AS [Info], CASE WHEN @@TRANCOUNT = 0 THEN 'transaction rolled back, no data left behind' ELSE 'WARNING: a transaction is still open' END AS Detail;
GO
-- 17. SESSION_CONTEXT read-only behaviour (runs last: it locks the UserId key for this session)
DECLARE @s_err int = 0;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', 1, 1;
  EXEC sp_set_session_context N'UserId', 2, 1;
END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
SELECT '17' AS [Case], CASE WHEN @s_err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'second sp_set_session_context(UserId, read_only=1) fails (expected error 15664)' AS Description, CONCAT(N'err=', @s_err) AS Detail;
GO
