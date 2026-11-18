-- Run ONCE per environment as an Entra admin / db owner, after the migrations. Not a migration: it depends on the environment.

-- Azure SQL with a managed identity (production, test): bind the user to the web app's identity.
-- CREATE USER [crm_app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<managed identity object (principal) id>';
-- ALTER ROLE crm_app_role ADD MEMBER [crm_app];

-- Developer running locally with DB_AUTH_MODE=default-azure (dev databases only):
-- CREATE USER [developer@contoso.com] FROM EXTERNAL PROVIDER;
-- ALTER ROLE crm_app_role ADD MEMBER [developer@contoso.com];

-- Hosts with SQL logins (DB_AUTH_MODE=sql-password), e.g. a second low-privilege login if the host allows one:
-- CREATE USER [crm_app] FOR LOGIN [crm_app_login];
-- ALTER ROLE crm_app_role ADD MEMBER [crm_app];
-- If only the owner login exists, the app runs as dbo: RLS still applies to it, but dbo can alter policies.
-- Document that as a test-only gap.
