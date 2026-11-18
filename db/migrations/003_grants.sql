-- 003_grants.sql: least-privilege role for the app. Derived from docs/architecture/overview.md section 6.
-- The login/user that belongs to the app is NOT created here (it differs per environment); see
-- db/manual/create-app-user.sql. The app is never db_owner and gets nothing on schemas sec or dbo.

IF DATABASE_PRINCIPAL_ID('crm_app_role') IS NULL
  CREATE ROLE crm_app_role AUTHORIZATION dbo;
GO

GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Accounts       TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Contacts       TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Opportunities  TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.Activities     TO crm_app_role;
GRANT SELECT, INSERT                 ON crm.AuditLog       TO crm_app_role;   -- append-only
GRANT SELECT                         ON crm.Users          TO crm_app_role;   -- writes via procedure only
GRANT SELECT                         ON crm.Picklists      TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.PicklistValues TO crm_app_role;   -- deactivate, never delete
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.PipelineStages TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.Settings       TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.FxRates        TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON crm.BrandingAssets TO crm_app_role;
GRANT SELECT, INSERT, UPDATE         ON crm.Teams          TO crm_app_role;
GRANT EXECUTE ON crm.usp_UpsertUserOnLogin                 TO crm_app_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON app.Sessions       TO crm_app_role;
GO
-- Deliberately NOT granted: anything on schema sec or dbo (incl. dbo.SchemaVersions), ALTER, CONTROL,
-- VIEW DEFINITION, ALTER ANY SECURITY POLICY, db_owner/db_ddladmin/db_datawriter/db_datareader membership.
