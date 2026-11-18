# Checklist: interim TEST site on MonsterASP.NET (.NET 10)

Site: https://crmtest.runasp.net. Database `db81053` on `db81053.databaseasp.net`. Sign-in by shared access code. **Test data only.**
Background: `docs/adr/0007-dotnet-10-rewrite.md`, `docs/architecture/dotnet.md` section 15.1. The Node app no longer runs here.

How it works: the GitHub workflow "Deploy .NET to test (MonsterASP)" (run manually) publishes the app, writes the secrets into the published
`web.config` inside the deploy job only (never in git or in a downloadable artifact), uploads it with Web Deploy and smoke-tests it.
Nothing is uploaded by hand.

## One-time setup

1. **MonsterASP panel:** set the website to **.NET 10 / ASP.NET Core** (in-process IIS). If .NET 10 is not offered, run the workflow with **self_contained** ticked (publishes a self-contained win-x64 build) and tell the developer (open item in dotnet.md section 17).
2. **Web Deploy** must be enabled on the site. The GitHub Environment `test` already holds the secrets `MONSTERASP_WEBDEPLOY_SERVER`, `_SITE`, `_USER`, `_PASSWORD` (a comma in the password breaks Web Deploy and is rejected by the workflow).
3. **GitHub Environment `test`** (Settings > Environments > test), restricted to the deployment branch. Secrets used: `DB_CONNECTION_STRING`
   (`Server=db81053.databaseasp.net;Database=db81053;User Id=db81053;Password=<database password>;Encrypt=True;TrustServerCertificate=False;`, single line)
   and `TEST_ACCESS_CODE` (at least 24 characters, at least 10 distinct; what testers type). `SESSION_SECRET` is no longer used and can be deleted.
   Optional variables (defaults in brackets): `TEST_ADMIN_NAME` + `TEST_ADMIN_EMAIL` [unset; both or neither], `SITE_URL` [https://crmtest.runasp.net],
   `TRUST_PROXY` [0; verify, see below], `DB_TRUST_SERVER_CERT` [false], `DATA_PROTECTION_KEYS_PATH` [unset], `MONSTERASP_WEBDEPLOY_ALLOW_UNTRUSTED` [false].
4. **Database access:** the host's firewall must allow whoever runs the migrator (your PC, step 6).

## Deploy

5. GitHub > Actions > **Deploy .NET to test (MonsterASP)** > Run workflow. Defaults are right for a normal deploy (it requires a green ".NET CI" run for the commit). Options: `self_contained` (fallback), `require_green_ci`, `run_migrations` / `seed_test` / `migrator` (below), `debug_stdout` (debug only).
6. **Tables and test users (first time, and after each new migration).** Preferred, from your PC (the DB firewall blocks GitHub's changing IPs), with the .NET 10 SDK and the repo checked out:
   ```
   export DB_AUTH_MODE=sql-password DB_CONNECTION_STRING='<same string as the secret>'
   dotnet run --project dotnet/src/Crm.Web -c Release -- migrate
   dotnet run --project dotnet/src/Crm.Web -c Release -- seed-test    # optional TEST_ADMIN_NAME / TEST_ADMIN_EMAIL for the named Admin
   ```
   Until the .NET migrator exists, `npm ci && npm run db:migrate && npm run db:seed:test` with the same variables does the same. From GitHub: tick `run_migrations` (and `seed_test`, choose `migrator`); expect failure if the DB firewall rejects the runner.
7. **Row-level security test** (after migrations): Actions > **Node DB tasks on test (manual)** > Run workflow with `run_rls_test` ticked. Every row must be PASS. The same script runs on every push in .NET CI against a throwaway SQL Server.

## Checks after a deploy

- The workflow's smoke test already requires `/healthz` = 200 `ok` (retried 2 minutes), `/readyz` = 200 (database reachable, migrated) and `/signin` = 200. Open https://crmtest.runasp.net/healthz yourself too.
- Enter the access code, pick a test user: the "TEST ENVIRONMENT" banner shows.
- As an Admin open **Settings > Users & teams**: "Your connection: IP ..." must show **your real public IP**. If it shows an internal address, set variable `TRUST_PROXY` (try 1) and redeploy; a wrong value makes the sign-in rate limit block everybody at once.

## Rollback

Actions > pick the last good run of the .NET deploy > Re-run all jobs (redeploys that run's commit), or `git revert` the bad commit and run the workflow again. Sessions survive a redeploy (Data Protection keys in `App_Data` are skipped by Web Deploy). Database changes are not reverted automatically: restore from MonsterASP's backup or, as it is test data, wipe and re-run step 6. Not yet exercised: try once after the first good deploy.

## Troubleshooting

Set `debug_stdout` and redeploy: logs appear in `logs\stdout*` on the host (file manager); redeploy without it afterwards. Secrets are never logged.

| Symptom | Cause / action |
|---|---|
| Workflow: "Secret ... is not set" / format error | Fix in Settings > Environments > test. |
| Web Deploy: 401 / ERROR_USER_UNAUTHORIZED | Wrong user/password/site name, or Web Deploy not enabled. |
| Web Deploy: certificate / trust error | Set variable `MONSTERASP_WEBDEPLOY_ALLOW_UNTRUSTED=true`. |
| Web Deploy: file in use | Retried 3 times with app_offline; rerun. |
| HTTP 500.30 | App stopped at startup: invalid configuration (the log names keys, never values) or DB unreachable. |
| HTTP 500.31 / 500.32 | .NET 10 runtime missing or wrong bitness: rerun with `self_contained`. |
| `/readyz` 503 | Database unreachable, migrations not applied (step 6), or time zone check failed. |
| "too many attempts" for everyone | `TRUST_PROXY` wrong (checks above). |
| Everyone signed out | Data Protection keys lost (`App_Data` deleted); expected once, no data lost. |

## Test-only gaps (accepted)

- **App runs as the database owner** (host gives one login). RLS still filters the app's queries, but the owner can switch policies off; the RLS test proves the policies with a low-privilege user. Must not carry over to Azure.
- **Secrets are readable on the host** (`web.config` environment variables) by anyone with panel/FTP access, and by repo admins via the Environment. Rotate `TEST_ACCESS_CODE` (edit the secret, redeploy) when a tester leaves.
- **Shared host:** no staging, limited logs, backups per the host plan. Actions are pinned to major tags, not commit SHAs. The workflow has not been run yet; the first real run is the test.
