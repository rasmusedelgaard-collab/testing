# Generates db/tests/rls-integration.sql (one self-contained batch per test case).
# Usage: python3 db/tests/generate-rls-integration.py db/tests/rls-integration.sql
# The .sql file is committed; edit this generator and regenerate rather than editing the 3000 lines by hand.
import sys, re
out=[]
batches=[]          # finished batches (each a list of lines); the file is joined with GO lines
def w(s=''): out.append(s)
def go():
    batches.append(list(out)); out.clear()


PEOPLE=[('A1','Admin',None),('M1','Manager','T1'),('U1','User','T1'),('U2','User','T1'),
        ('U3','User','T2'),('U4','User','T1'),('N1','User',None),('N2','User',None)]
ALLU=','.join('@'+p[0] for p in PEOPLE)
ENT=[
 dict(n='Accounts',s='Acc',pk='AccountId',upd="Name = N'RLSTEST upd'",
      ins="INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', {o}, {t}, {c}, {c})"),
 dict(n='Contacts',s='Con',pk='ContactId',upd="LastName = N'RLSTEST upd'",
      ins="INSERT crm.Contacts (LastName,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', {o}, {t}, {c}, {c})"),
 dict(n='Opportunities',s='Opp',pk='OpportunityId',upd="Amount = 999",
      ins="INSERT crm.Opportunities (Name,AccountId,StageId,Amount,CurrencyCode,CloseDate,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST ins', @SharedAcc, @Stage, 1, 'DKK', '2030-01-01', {o}, {t}, {c}, {c})"),
 dict(n='Activities',s='Act',pk='ActivityId',upd="Subject = N'RLSTEST upd'",
      ins="INSERT crm.Activities (ActivityType,Subject,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES ('task', N'RLSTEST ins', {o}, {t}, {c}, {c})"),
]
CONFIG_TABLES=['crm.Accounts','crm.Contacts','crm.Opportunities','crm.Activities','crm.AuditLog','crm.FxRates','crm.PipelineStages','crm.PicklistValues']

def as_user(u,role,team):
    w("  EXECUTE AS USER = N'rlstest_app';")
    w(f"  EXEC sp_set_session_context N'UserId', {u}, 0;")
    w(f"  EXEC sp_set_session_context N'Role', {('N'+repr(role)) if role else 'NULL'}, 0;")
    w(f"  EXEC sp_set_session_context N'TeamId', {team}, 0;")
def admin_ctx():
    w("  EXEC sp_set_session_context N'UserId', @A1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;")
def vis(mode):
    admin_ctx()
    w(f"  IF EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'visibility.mode') UPDATE crm.Settings SET SettingValue = N'{mode}' WHERE SettingKey = 'visibility.mode'")
    w(f"  ELSE INSERT crm.Settings (SettingKey, SettingValue) VALUES ('visibility.mode', N'{mode}');")
def reset(): w("  SET @err = 0; SET @rc = -1; SET @n = -1;")
def rec(cid,desc,cond,detail="CONCAT(N'err=', @err, N' rc=', @rc, N' n=', @n)"):
    desc=desc.replace("'","''")
    w(f"  SELECT '{cid}' AS [Case], CASE WHEN {cond} THEN 'PASS' ELSE 'FAIL' END AS Result, N'{desc}' AS Description, {detail} AS Detail;")
def dml(stmt, cap_rc=True):
    w("  BEGIN TRY")
    w(f"    {stmt};")
    if cap_rc: w("    SET @rc = @@ROWCOUNT;")
    w("  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;")
def case_begin(): w("SAVE TRANSACTION sp_case;"); reset(); w("BEGIN TRY")
def case_end(doom_expected=False):
    w("END TRY BEGIN CATCH")
    w("  WHILE USER_NAME() = N'rlstest_app' REVERT;   -- never leave an impersonation behind: the next batch could not even start")
    w("  SELECT 'EXC' AS [Case], 'FAIL' AS Result, N'unexpected error in this case (the other cases still ran)' AS Description, CONCAT(N'err=', ERROR_NUMBER(), N' line=', ERROR_LINE(), N' ', ERROR_MESSAGE()) AS Detail;")
    w("END CATCH;")
    w("WHILE USER_NAME() = N'rlstest_app' REVERT;")
    w("IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;")
    w("ELSE IF XACT_STATE() = -1 BEGIN   -- doomed transaction: only a full rollback is possible, so the run cannot continue")
    w("  ROLLBACK TRANSACTION;")
    if doom_expected:
        w("  SELECT 'ABORT' AS [Case], 'PASS' AS Result, N'expected: the procedure THROW under XACT_ABORT ON dooms the transaction; rolled back (this case runs last)' AS Description, N'' AS Detail;")
    else:
        w("  SELECT 'ABORT' AS [Case], 'FAIL' AS Result, N'the transaction became uncommittable; everything was rolled back and the remaining cases cannot run' AS Description, N'' AS Detail;")
    w("END")
    finish_case_batch()
INT_IDS=[p[0] for p in PEOPLE]+['T1','T2','Stage','SharedAcc','PicklistId']
GUID_IDS=['G1','G2','DemoOid']
def entity_ids():
    return [f"{e['s']}_{p[0]}" for e in ENT for p in PEOPLE]
def finish_case_batch():
    # every case batch is self-contained: it declares its scratch variables and loads the ids it uses from #ids
    body="\n".join(out); out.clear()
    # SQL Server variable names are case-INSENSITIVE: scratch variables must never collide with @U3, @N2, ... so they get an s_ prefix
    body=re.sub(r'@(err|rc|n2|n|e3|e4|e5|d1|d2|d3|u3|j|o2|o)\b', r'@s_\1', body)
    used=set(re.findall(r'@(\w+)',body))
    loads=[]
    for k in INT_IDS+entity_ids():
        if k in used: loads.append(f"@{k} int = (SELECT Id FROM #ids WHERE K = '{k}')")
    for k in GUID_IDS:
        if k in used: loads.append(f"@{k} uniqueidentifier = (SELECT G FROM #ids WHERE K = '{k}')")
    # First statement of every case batch: drop any impersonation a previous batch leaked (a WITHOUT LOGIN user cannot touch tempdb,
    # so even loading #ids would fail with error 916). Loop = nested EXECUTE AS levels. Guarded by the name so it can never spin.
    pre=["WHILE USER_NAME() = N'rlstest_app' REVERT;",
         "IF @@TRANCOUNT = 0 BEGIN SELECT 'SKIP' AS [Case], 'FAIL' AS Result, N'no open transaction (an earlier case doomed and rolled back the seed data); case not run' AS Description, N'' AS Detail; RETURN; END;"]
    pre+=["DECLARE @s_j nvarchar(max), @s_o uniqueidentifier, @s_o2 nvarchar(40), @s_err int = 0, @s_rc int = -1, @s_n int = -1, @s_n2 int, @s_e3 int, @s_e4 int, @s_e5 int, @s_d1 int, @s_d2 int, @s_d3 int, @s_u3 int;"]
    if loads: pre.append("DECLARE "+",\n  ".join(loads)+";")
    if '@P' in body: pre.append("DECLARE @P TABLE (UserId int, TeamId int, TeamStatus varchar(10), TeamMatches int);")
    out.extend(pre); out.extend(body.split("\n")); go()
def cnt(table, where): return f"SELECT @n = COUNT(*) FROM {table} WHERE {where}"

w("""-- =====================================================================================================
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
""")
go()
w("""-- batch 2: one transaction for the whole run, seed data, ids stored in #ids
DECLARE @A1 int, @M1 int, @U1 int, @U2 int, @U3 int, @U4 int, @N1 int, @N2 int, @T1 int, @T2 int;
DECLARE @G1 uniqueidentifier = NEWID(), @G2 uniqueidentifier = NEWID(), @DemoOid uniqueidentifier = NEWID();
DECLARE @Stage int, @SharedAcc int, @PicklistId int;
""")
for e in ENT:
    for p in PEOPLE: w(f"DECLARE @{e['s']}_{p[0]} int;")
w("""
BEGIN TRAN;

-- ---------- seed (as Admin context; RLS applies to dbo as well) ----------
EXEC sp_set_session_context N'UserId', 1, 0; EXEC sp_set_session_context N'Role', N'Admin', 0; EXEC sp_set_session_context N'TeamId', NULL, 0;

INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team 1', @G1);
SET @T1 = SCOPE_IDENTITY();
INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team 2', @G2);
SET @T2 = SCOPE_IDENTITY();
""")
tid={'T1':'@T1','T2':'@T2',None:'NULL'}
for tag,role,team in PEOPLE:
    active = 0 if tag=='U4' else 1
    w(f"INSERT crm.Users (EntraOid, TenantId, DisplayName, Role, TeamId, TeamStatus, IsActive) VALUES (NEWID(), NEWID(), N'RLSTEST {tag}', '{role}', {tid[team]}, '{'ok' if team else 'none'}', {active}); SET @{tag} = SCOPE_IDENTITY();")
w("""
EXEC sp_set_session_context N'UserId', @A1, 0;
SELECT TOP (1) @Stage = StageId FROM crm.PipelineStages WHERE StageType = 'open' ORDER BY SortOrder;
IF @Stage IS NULL BEGIN INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST open', 1, 10, 'open'); SET @Stage = SCOPE_IDENTITY(); END
SELECT TOP (1) @PicklistId = PicklistId FROM crm.Picklists ORDER BY PicklistId;
IF @PicklistId IS NULL BEGIN INSERT crm.Picklists (Code, Name) VALUES ('rlstest', N'RLSTEST'); SET @PicklistId = SCOPE_IDENTITY(); END
INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST seed value', 32000);
INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('AAA', 'BBB', '2000-01-01', 1.5, @A1);

INSERT crm.Accounts (Name,OwnerId,TeamId,CreatedBy,ModifiedBy) VALUES (N'RLSTEST shared', @A1, NULL, @A1, @A1);
SET @SharedAcc = SCOPE_IDENTITY();
""")
for tag,role,team in PEOPLE:
    t=tid[team]
    for e in ENT:
        stmt=e['ins'].format(o='@'+tag,t=t,c='@A1')
        w(f"{stmt}; SET @{e['s']}_{tag} = SCOPE_IDENTITY();")
w("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'C', @U1, @T1, @A1, NEWID());")
w("""
-- the test principal: exactly the app's grants
CREATE USER rlstest_app WITHOUT LOGIN;
GRANT CONNECT TO rlstest_app;
ALTER ROLE crm_app_role ADD MEMBER rlstest_app;
""")
names=INT_IDS+entity_ids()
w("INSERT #ids (K, Id) VALUES "+", ".join(f"('{k}', @{k})" for k in names)+";")
w("INSERT #ids (K, G) VALUES "+", ".join(f"('{k}', @{k})" for k in GUID_IDS)+";")
go()

# ---- case 1
w("-- ---------- 1. no context: zero rows everywhere ----------")
for t in CONFIG_TABLES:
    case_begin()
    admin_ctx()
    w(f"  SELECT @n2 = COUNT(*) FROM {t};")
    w("  EXECUTE AS USER = N'rlstest_app';")
    w("  EXEC sp_set_session_context N'UserId', NULL, 0; EXEC sp_set_session_context N'Role', NULL, 0; EXEC sp_set_session_context N'TeamId', NULL, 0;")
    dml(f"SELECT @n = COUNT(*) FROM {t}", cap_rc=False)
    w("  REVERT;")
    rec('1',f'no context returns 0 rows from {t} (Admin sees rows: control)',"@err = 0 AND @n = 0 AND @n2 > 0","CONCAT(N'err=', @err, N' rows_without_context=', @n, N' rows_as_admin=', @n2)")
    case_end()
# bad role fails closed
case_begin()
w("  EXECUTE AS USER = N'rlstest_app';")
w("  EXEC sp_set_session_context N'UserId', @U1, 0; EXEC sp_set_session_context N'Role', N'Root', 0; EXEC sp_set_session_context N'TeamId', @T1, 0;")
dml(cnt('crm.Accounts',f'OwnerId IN ({ALLU})'),cap_rc=False)
w("  REVERT;")
rec('1b','unknown Role value fails closed (0 rows)',"@err = 0 AND @n = 0")
case_end()

# ---- 2/3 reads
for e in ENT:
    t='crm.'+e['n']; wh=f'OwnerId IN ({ALLU})'
    if e['n']=='Accounts': wh+=' AND AccountId <> @SharedAcc'   # the shared account (owned by A1, used as Opportunity parent) is not one of the 8 per-user rows
    for mode,exp,desc in (('private',1,'sees only own row'),('team',4,'sees M1+U1+U2+U4 rows (own team, not U3)')):
        case_begin(); vis(mode); as_user('@U1','User','@T1'); dml(cnt(t,wh),cap_rc=False); w("  REVERT;")
        rec('2',f'{e["n"]} U1 {mode}: {desc}',f"@err = 0 AND @n = {exp}")
        case_end()
    case_begin(); vis('private'); as_user('@M1','Manager','@T1'); dml(cnt(t,wh),cap_rc=False); w("  REVERT;")
    rec('3',f'{e["n"]} M1 (private mode) sees team 1 rows (M1,U1,U2,U4)',"@err = 0 AND @n = 4"); case_end()
    case_begin(); vis('private'); as_user('@A1','Admin','NULL'); dml(cnt(t,wh),cap_rc=False); w("  REVERT;")
    rec('3',f'{e["n"]} A1 sees all 8 seeded rows',"@err = 0 AND @n = 8"); case_end()

# ---- 4..12 per entity
for e in ENT:
    t='crm.'+e['n']; pk=e['pk']; s=e['s']; nm=e['n']
    def row(tag): return f"@{s}_{tag}"
    # 4
    case_begin(); vis('private'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @U1 WHERE {pk} = {row('U2')}")
    w("  SET @n = @rc; SET @rc = -1;")
    dml(f"DELETE FROM {t} WHERE {pk} = {row('U2')}")
    w("  REVERT;")
    rec('4',f'{nm} private: U1 UPDATE (n) and DELETE (rc) of U2 row affect 0 rows',"@err = 0 AND @n = 0 AND @rc = 0"); case_end()
    # 5
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @U1 WHERE {pk} = {row('U2')}")
    w("  REVERT;"); admin_ctx()
    w(f"  SELECT @n = COUNT(*) FROM {t} WHERE {pk} = {row('U2')} AND ModifiedBy = @U1;")
    rec('5',f'{nm} team: U1 may UPDATE U2 row; ModifiedBy = U1',"@err = 0 AND @rc = 1 AND @n = 1"); case_end()
    # 6
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"DELETE FROM {t} WHERE {pk} = {row('U2')}"); w("  REVERT;"); admin_ctx()
    w(f"  SELECT @n = COUNT(*) FROM {t} WHERE {pk} = {row('U2')};")
    rec('6',f'{nm} team: U1 DELETE of U2 row blocked with 33504, row still exists',"@err = 33504 AND @n = 1"); case_end()
    # 7
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET TeamId = @T2 WHERE {pk} = {row('U2')}"); w("  SET @n = @err; SET @err = 0;")
    dml(f"UPDATE {t} SET TeamId = @T2 WHERE {pk} = {row('U1')}"); w("  REVERT;")
    rec('7',f'{nm} team: U1 cannot set TeamId=2 on U2 row (n) or own row (err): both 33504',"@n = 33504 AND @err = 33504"); case_end()
    # 8
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET OwnerId = @U1 WHERE {pk} = {row('U2')}"); w("  SET @n = @err * 10 + @rc; SET @err = 0; SET @rc = -1;")
    dml(f"UPDATE {t} SET OwnerId = @U2 WHERE {pk} = {row('U2')}"); w("  SET @n2 = @err * 10 + @rc; SET @err = 0; SET @rc = -1;")
    # to-U3 / to-U4 must fail
    w("  SET @e3 = 0; SET @e4 = 0;")
    w("  BEGIN TRY"); w(f"    UPDATE {t} SET OwnerId = @U3 WHERE {pk} = {row('U2')};"); w("  END TRY BEGIN CATCH SET @e3 = ERROR_NUMBER(); END CATCH;")
    w("  BEGIN TRY"); w(f"    UPDATE {t} SET OwnerId = @U4 WHERE {pk} = {row('U2')};"); w("  END TRY BEGIN CATCH SET @e4 = ERROR_NUMBER(); END CATCH;")
    w("  REVERT;")
    rec('8',f'{nm} team: owner reassignment U2->U1 ok, U1->U2 ok, ->U3 (other team) 33504, ->U4 (inactive) 33504',"@n = 1 AND @n2 = 1 AND @e3 = 33504 AND @e4 = 33504","CONCAT(N'to_U1=', @n, N' back_U2=', @n2, N' to_U3=', @e3, N' to_U4=', @e4)"); case_end()
    # 9
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @U1 WHERE {pk} = {row('U3')}"); w("  SET @n = @rc; SET @rc = -1;")
    dml(f"DELETE FROM {t} WHERE {pk} = {row('U3')}"); w("  REVERT;")
    rec('9',f'{nm} team: U1 UPDATE (n) / DELETE (rc) of other-team U3 row affect 0 rows',"@err = 0 AND @n = 0 AND @rc = 0"); case_end()
    # 10 inserts, both modes
    for mode in ('private','team'):
        for desc,o,tm,expect_ok in (('owner U3 (other team)','@U3','@T1',False),('TeamId 2','@U1','@T2',False),('owner U2 (active team-mate)','@U2','@T1',True),('owner U4 (inactive)','@U4','@T1',False)):
            case_begin(); vis(mode); as_user('@U1','User','@T1')
            dml(e['ins'].format(o=o,t=tm,c='@U1'))
            w("  REVERT;")
            cond="@err = 0 AND @rc = 1" if expect_ok else "@err = 33504"
            rec('10',f'{nm} {mode}: U1 INSERT with {desc} '+('succeeds' if expect_ok else 'fails with 33504'),cond); case_end()
    # 11
    case_begin(); vis('private'); as_user('@M1','Manager','@T1')
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @M1 WHERE {pk} = {row('U2')}"); w("  SET @n = @rc; SET @rc = -1;")
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @M1 WHERE {pk} = {row('U3')}"); w("  SET @n2 = @rc; SET @rc = -1;")
    w("  SET @e5 = 0; SET @d1 = -1; SET @d2 = -1;")
    w("  BEGIN TRY"); w(f"    UPDATE {t} SET TeamId = @T2 WHERE {pk} = {row('U2')};"); w("  END TRY BEGIN CATCH SET @e5 = ERROR_NUMBER(); END CATCH;")
    w("  BEGIN TRY"); w(f"    DELETE FROM {t} WHERE {pk} = {row('U3')}; SET @d2 = @@ROWCOUNT;"); w("  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;")
    w("  BEGIN TRY"); w(f"    DELETE FROM {t} WHERE {pk} = {row('U2')}; SET @d1 = @@ROWCOUNT;"); w("  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;")
    w("  REVERT;")
    rec('11',f'{nm} M1: UPDATE+DELETE of U2 row ok; other-team U3 row UPDATE/DELETE 0 rows; TeamId=2 -> 33504',"@err = 0 AND @n = 1 AND @n2 = 0 AND @e5 = 33504 AND @d1 = 1 AND @d2 = 0","CONCAT(N'err=', @err, N' upd_U2=', @n, N' upd_U3=', @n2, N' team_move=', @e5, N' del_U2=', @d1, N' del_U3=', @d2)"); case_end()
    # 12
    case_begin(); vis('private'); as_user('@A1','Admin','NULL')
    dml(f"UPDATE {t} SET TeamId = @T2, OwnerId = @U3 WHERE {pk} = {row('U2')}"); w("  REVERT;")
    rec('12',f'{nm} A1: may move U2 row to team 2 with owner U3',"@err = 0 AND @rc = 1"); case_end()
    # 13
    case_begin(); vis('team'); as_user('@N1','User','NULL')
    dml(cnt(t,f'OwnerId IN ({ALLU})'),cap_rc=False); w("  SET @n2 = @n;")
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @N1 WHERE {pk} = {row('N2')}"); w("  SET @n = @rc; SET @rc = -1;")
    dml(f"DELETE FROM {t} WHERE {pk} = {row('N2')}"); w("  SET @d3 = @rc; SET @rc = -1;")
    dml(f"UPDATE {t} SET {e['upd']}, ModifiedBy = @N1 WHERE {pk} = {row('N1')}"); w("  SET @u3 = @rc; SET @rc = -1;")
    dml(f"DELETE FROM {t} WHERE {pk} = {row('N1')}"); w("  REVERT;")
    rec('13',f'{nm} team mode, no-team user: sees 1 row, other no-team row untouchable, own row editable/deletable',"@err = 0 AND @n2 = 1 AND @n = 0 AND @d3 = 0 AND @u3 = 1 AND @rc = 1","CONCAT(N'err=', @err, N' visible=', @n2, N' upd_other=', @n, N' del_other=', @d3, N' upd_own=', @u3, N' del_own=', @rc)"); case_end()

# ---- 14 config writes
w("-- ---------- 14. config tables: Admin-only writes ----------")
CFG=[
 ('Settings insert',"INSERT crm.Settings (SettingKey, SettingValue) VALUES ('rlstest.key', N'x')"),
 ('Settings update',"UPDATE crm.Settings SET SettingValue = SettingValue WHERE SettingKey = 'visibility.mode'"),
 ('FxRates insert',"INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('CCC', 'DDD', '2000-02-01', 2, {u})"),
 ('PipelineStages insert',"INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType) VALUES (N'RLSTEST stage', 99, 10, 'open')"),
 ('PipelineStages update',"UPDATE crm.PipelineStages SET SortOrder = SortOrder WHERE StageId = @Stage"),
 ('PicklistValues insert',"INSERT crm.PicklistValues (PicklistId, Label, SortOrder) VALUES (@PicklistId, N'RLSTEST value', 32001)"),
 ('Teams insert',"INSERT crm.Teams (Name, EntraGroupId) VALUES (N'RLSTEST team x', NEWID())"),
 ('Teams update',"UPDATE crm.Teams SET Name = Name WHERE TeamId = @T1"),
 ('BrandingAssets insert',"INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) VALUES ('rlstest', 'image/png', 0x89504E47, CAST(0x00 AS binary(32)), {u})"),
]
for name,stmt in CFG:
    for who,role,team,uv in (('User U1','User','@T1','@U1'),('Manager M1','Manager','@T1','@M1')):
        case_begin(); as_user(uv,role,team); dml(stmt.format(u=uv)); w("  REVERT;")
        rec('14',f'{name}: {who} blocked with 33504',"@err = 33504"); case_end()
    case_begin(); as_user('@A1','Admin','NULL'); dml(stmt.format(u='@A1')); w("  REVERT;")
    rec('14',f'{name}: Admin succeeds',"@err = 0 AND @rc >= 1"); case_end()

# ---- 15 audit
w("-- ---------- 15. AuditLog insert guard ----------")
case_begin(); vis('team'); as_user('@U1','User','@T1')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T1, @U2, NEWID())")
w("  REVERT;"); rec('15','AuditLog: U1 cannot write an audit row with ChangedBy = U2',"@err = 33504"); case_end()
case_begin(); vis('team'); as_user('@U1','User','@T1')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U2, 'U', @U2, @T1, @U1, NEWID())")
w("  REVERT;"); rec('15','AuditLog: U1 (team mode) can audit a change to U2 record (ChangedBy = U1)',"@err = 0 AND @rc = 1"); case_end()
case_begin(); vis('team'); as_user('@U1','User','@T1')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U3, 'U', @U3, @T2, @U1, NEWID())")
w("  REVERT;"); rec('15','AuditLog: U1 cannot audit a record of another team',"@err = 33504"); case_end()
case_begin(); vis('team'); as_user('@U1','User','@T1')
dml("UPDATE crm.AuditLog SET Action = 'D'")
w("  REVERT;"); rec('15','AuditLog: no UPDATE grant (append-only)',"@err <> 0"); case_end()

# ---- 16 privileges (checked through permission metadata: a failing ALTER SECURITY POLICY dooms the shared transaction)
w("-- ---------- 16. app role cannot change security or read migration state ----------")
PERMS=[
 ('crm_app_role cannot ALTER SECURITY POLICY (object permission)',"HAS_PERMS_BY_NAME(N'sec.CrmRowPolicy', N'OBJECT', N'ALTER')",0),
 ('crm_app_role has no database-level ALTER ANY SECURITY POLICY',"HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY SECURITY POLICY')",0),
 ('crm_app_role cannot ALTER the other policy either',"HAS_PERMS_BY_NAME(N'sec.CrmConfigPolicy', N'OBJECT', N'ALTER')",0),
 ('crm_app_role cannot SELECT dbo.SchemaVersions',"HAS_PERMS_BY_NAME(N'dbo.SchemaVersions', N'OBJECT', N'SELECT')",0),
 ('crm_app_role cannot INSERT crm.Users',"HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'INSERT')",0),
 ('crm_app_role cannot UPDATE crm.Users',"HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'UPDATE')",0),
 ('crm_app_role cannot DELETE crm.Users',"HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'DELETE')",0),
 ('crm_app_role can SELECT crm.Users (control: the owner picker needs it)',"HAS_PERMS_BY_NAME(N'crm.Users', N'OBJECT', N'SELECT')",1),
 ('crm_app_role has no access to schema sec (predicate functions)',"HAS_PERMS_BY_NAME(N'sec.fn_RowAccess', N'OBJECT', N'SELECT')",0),
 ('crm_app_role cannot UPDATE or DELETE the append-only AuditLog',"CASE WHEN HAS_PERMS_BY_NAME(N'crm.AuditLog', N'OBJECT', N'UPDATE') = 0 AND HAS_PERMS_BY_NAME(N'crm.AuditLog', N'OBJECT', N'DELETE') = 0 THEN 0 ELSE 1 END",0),
 ('crm_app_role cannot take ownership or control of crm',"HAS_PERMS_BY_NAME(N'crm.Accounts', N'OBJECT', N'CONTROL')",0),
]
for desc,expr,exp in PERMS:
    case_begin(); w("  EXECUTE AS USER = N'rlstest_app';"); w(f"  SELECT @n = {expr};"); w("  REVERT;")
    rec('16',desc,f"@n = {exp}","CONCAT(N'perm=', @n)"); case_end()

# ---- 18..21 login upsert proc
w("-- ---------- 18. crm.usp_UpsertUserOnLogin team resolution (exactly one team group) ----------")
for cid,desc,groups,over,exp_team,exp_status in (
  ('18a','one linked group -> that team, status ok',"CONCAT(N'[\"', @G1, N'\"]')",0,'@T1',"'ok'"),
  ('18b','two linked groups -> TeamId NULL, status multiple (fail closed, no lowest-id rule)',"CONCAT(N'[\"', @G1, N'\",\"', @G2, N'\"]')",0,None,"'multiple'"),
  ('18c','no linked group -> TeamId NULL, status none',"N'[\"00000000-0000-0000-0000-000000000099\"]'",0,None,"'none'"),
  ('18d','groups overage -> TeamId NULL, status overage',"N'[]'",1,None,"'overage'"),
  ('18e','overage flag wins even if a group is present',"CONCAT(N'[\"', @G1, N'\"]')",1,None,"'overage'"),
):
    case_begin()
    as_user('@U1','User','@T1')
    w("  BEGIN TRY")
    w(f"    SET @j = {groups};")
    # The procedure runs as the app principal; its own result set is not captured (a table variable lives in tempdb, which a
    # WITHOUT LOGIN user must not touch). The outcome is read back from crm.Users after REVERT.
    w(f"    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @DemoOid, @TenantId = @DemoOid, @DisplayName = N'RLSTEST login', @Email = NULL, @Role = 'User', @GroupIdsJson = @j, @GroupsOverage = {over};")
    w("  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;")
    w("  REVERT;")
    w("  SELECT @n = TeamId, @o2 = TeamStatus FROM crm.Users WHERE EntraOid = @DemoOid;")
    team_cond = f"@n = {exp_team}" if exp_team else "@n IS NULL"
    rec(cid,'usp_UpsertUserOnLogin: '+desc,f"@err = 0 AND {team_cond} AND @o2 = {exp_status}","CONCAT(N'err=', @err, N' team=', @n, N' status=', @o2)")
    case_end()

# ---- L1: audit insert null-safe team check, append-only block predicates
w("-- ---------- 15b. AuditLog: owner = caller must ALSO carry the caller's team; no UPDATE/DELETE even for dbo/Admin ----------")
case_begin(); as_user('@U1','User','@T1')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T2, @U1, NEWID())")
w("  REVERT;"); rec('15b','AuditLog: U1 cannot write an audit row owned by self but carrying another team (33504)',"@err = 33504"); case_end()
case_begin(); as_user('@U1','User','@T1')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_U1, 'U', @U1, @T1, @U1, NEWID())")
w("  REVERT;"); rec('15b','AuditLog: U1 can write an audit row for own record with own team',"@err = 0 AND @rc = 1"); case_end()
case_begin(); as_user('@N1','User','NULL')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, @T1, @N1, NEWID())")
w("  REVERT;"); rec('15b','AuditLog: no-team user cannot claim a team on an audit row (33504)',"@err = 33504"); case_end()
case_begin(); as_user('@N1','User','NULL')
dml("INSERT crm.AuditLog (EntityType, EntityId, Action, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('account', @Acc_N1, 'U', @N1, NULL, @N1, NEWID())")
w("  REVERT;"); rec('15b','AuditLog: no-team user can audit own no-team record',"@err = 0 AND @rc = 1"); case_end()
for verb,stmt in (('UPDATE',"UPDATE crm.AuditLog SET Action = Action"),('DELETE',"DELETE FROM crm.AuditLog")):
    case_begin(); admin_ctx(); dml(stmt)
    rec('15b',f'AuditLog: {verb} is blocked for everyone, even dbo with Admin context (always-false block predicate, 33504)',"@err = 33504"); case_end()

# ---- M4: takeover-then-delete is allowed by product-owner decision and must be audited
w("-- ---------- 20. team mode: User takes over a team-mate record, then deletes it as owner; both steps audited ----------")
ET={'Accounts':'account','Contacts':'contact','Opportunities':'opportunity','Activities':'activity'}
for e in ENT:
    t='crm.'+e['n']; pk=e['pk']; row='@'+e['s']+'_U2'; et=ET[e['n']]
    case_begin(); vis('team'); as_user('@U1','User','@T1')
    dml(f"UPDATE {t} SET OwnerId = @U1, ModifiedBy = @U1 WHERE {pk} = {row}"); w("  SET @n = @rc; SET @rc = -1;")
    dml(f"INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('{et}', {row}, 'U', 'OwnerId', CAST(@U2 AS nvarchar(20)), CAST(@U1 AS nvarchar(20)), @U1, @T1, @U1, NEWID())"); w("  SET @n2 = @rc; SET @rc = -1;")
    dml(f"DELETE FROM {t} WHERE {pk} = {row}"); w("  SET @d1 = @rc; SET @rc = -1;")
    dml(f"INSERT crm.AuditLog (EntityType, EntityId, Action, OldValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('{et}', {row}, 'D', N'RLSTEST takeover', @U1, @T1, @U1, NEWID())"); w("  SET @d2 = @rc; SET @rc = -1;")
    dml(f"SELECT @u3 = COUNT(*) FROM crm.AuditLog WHERE EntityType = '{et}' AND EntityId = {row} AND ChangedBy = @U1",cap_rc=False)
    w("  REVERT;")
    rec('20',f'{e["n"]} team mode: U1 takes over U2 row (rc=1), audit U row (rc=1), deletes it as owner (rc=1), audit D row (rc=1), 2 audit rows visible',"@err = 0 AND @n = 1 AND @n2 = 1 AND @d1 = 1 AND @d2 = 1 AND @u3 = 2","CONCAT(N'err=', @err, N' takeover=', @n, N' audit1=', @n2, N' delete=', @d1, N' audit2=', @d2, N' audit_rows=', @u3)"); case_end()

# ---- L8: login must not reactivate
w("-- ---------- 19. usp_UpsertUserOnLogin does not reactivate a deactivated user ----------")
case_begin(); admin_ctx()
w("  SELECT @o = EntraOid FROM crm.Users WHERE UserId = @U1;")
w("  UPDATE crm.Users SET IsActive = 0 WHERE UserId = @U1;")
w("  BEGIN TRY")
w("    EXEC crm.usp_UpsertUserOnLogin @EntraOid = @o, @TenantId = @o, @DisplayName = N'RLSTEST U1', @Email = NULL, @Role = 'User', @GroupIdsJson = N'[]', @GroupsOverage = 0;")
w("  END TRY BEGIN CATCH SET @err = ERROR_NUMBER(); END CATCH;")
w("  SELECT @n = IsActive FROM crm.Users WHERE UserId = @U1;")
rec('19','deactivated user: login procedure throws 50011 and does not set IsActive = 1',"@err = 50011 AND @n = 0"); case_end(doom_expected=True)

w("""-- everything done: undo ALL seed data and test writes
IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
SELECT 'ROLLBACK' AS [Info], CASE WHEN @@TRANCOUNT = 0 THEN 'transaction rolled back, no data left behind' ELSE 'WARNING: a transaction is still open' END AS Detail;
""")
go()
w("""-- 17. SESSION_CONTEXT read-only behaviour (runs last: it locks the UserId key for this session)
DECLARE @s_err int = 0;
BEGIN TRY
  EXEC sp_set_session_context N'UserId', 1, 1;
  EXEC sp_set_session_context N'UserId', 2, 1;
END TRY BEGIN CATCH SET @s_err = ERROR_NUMBER(); END CATCH;
SELECT '17' AS [Case], CASE WHEN @s_err <> 0 THEN 'PASS' ELSE 'FAIL' END AS Result, N'second sp_set_session_context(UserId, read_only=1) fails (expected error 15664)' AS Description, CONCAT(N'err=', @s_err) AS Detail;
""")
go()
open(sys.argv[1],'w').write("\nGO\n".join("\n".join(b).strip("\n") for b in batches)+"\nGO\n")
