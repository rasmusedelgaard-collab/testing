# Checklist: interim TEST site on MonsterASP.NET

Site: https://crmtest.runasp.net. Database `db81053` on `db81053.databaseasp.net`. Sign-in by shared access code. **Test data only.**
Background: `docs/architecture/overview.md` section 12.3.

How it works: a push to `claude/tender-darwin-4quuzo` (or a manual run) starts the GitHub workflow "Deploy to test (MonsterASP)":
checks (lint, typecheck, tests) -> build a ready-to-run package -> Web Deploy upload to MonsterASP. MonsterASP does not run `npm install`,
so the package contains everything. Passwords are kept in **GitHub Secrets**; the workflow writes them into a file `crm.env` inside the uploaded
package at deploy time. `crm.env` is never in git, never in a downloadable artifact, and the web server refuses to serve it.
(The old `deploy` branch is no longer used and can be deleted on GitHub.)

## One-time setup

1. **Find the Web Deploy details in MonsterASP.** *Not verified: I could not reach help.monsterasp.net. From memory it is in the panel under your website > "Web Deploy" / "Publish profile" (download the profile file).* Open the profile in a text editor and note four values:
   `publishUrl` (looks like `siteXXXX.siteasp.net:8172`), `msdeploySite` (the site name), `userName`, `userPWD`. If the panel has a Web Deploy on/off switch, turn it on. If you cannot find it, ask MonsterASP support: "Web Deploy (msdeploy) server, site name, user and password for crmtest.runasp.net".
2. **Node version.** In the MonsterASP panel set the site's Node.js version to the newest LTS offered (20 or newer).
3. **Create the GitHub Environment.** GitHub repo > Settings > Environments > New environment > name it exactly `test`.
4. **Add the secrets.** In Settings > Environments > test > Environment secrets > Add secret, one by one (names exactly as shown):

   | Secret | Value |
   |---|---|
   | `MONSTERASP_WEBDEPLOY_SERVER` | `publishUrl` host, e.g. `siteXXXX.siteasp.net` (`:8172` optional) |
   | `MONSTERASP_WEBDEPLOY_SITE` | `msdeploySite` |
   | `MONSTERASP_WEBDEPLOY_USER` | `userName` |
   | `MONSTERASP_WEBDEPLOY_PASSWORD` | `userPWD` (a password containing a comma may break Web Deploy; ask MonsterASP to reset it if so) |
   | `DB_PASSWORD` | the password MonsterASP gave you for database `db81053` |
   | `SESSION_SECRET` | generated value (below) |
   | `TEST_ACCESS_CODE` | generated value (below); this is what testers type, share it only with testers |

   Generate `SESSION_SECRET` and `TEST_ACCESS_CODE` (run twice, needs Node on your PC, paste each output into its secret):
   ```
   node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"
   ```
5. **Optional Environment variables** (Settings > Environments > test > Variables). Defaults in brackets, so normally add none:
   `DB_SERVER` [db81053.databaseasp.net], `DB_NAME` [db81053], `DB_USER` [db81053], `SITE_URL` [https://crmtest.runasp.net], `TRUST_PROXY` [1],
   `DB_TRUST_SERVER_CERT` [false; set `true` only if the DB connection fails with a certificate error], `MONSTERASP_WEBDEPLOY_ALLOW_UNTRUSTED` [false; set `true` only if the deploy fails with a certificate/trust error].
6. **Allow database access.** In the MonsterASP panel (database section) add your PC's public IP (see https://ifconfig.me) to the allowed list, for step 8.
7. **Run the workflow.** GitHub > Actions > "Deploy to test (MonsterASP)" > Run workflow. Wait for all jobs to go green. (Later every push to the branch does this automatically.)
8. **Create the tables and test users (once, and after each new migration).** From your PC, with Node **22.9 or newer** and this repo checked out on the branch:
   ```
   npm ci
   export DB_AUTH_MODE=sql-password DB_SERVER=db81053.databaseasp.net DB_NAME=db81053 DB_USER=db81053
   export DB_PASSWORD='<database password>'
   npm run db:migrate
   npm run db:seed:test
   ```
   Windows PowerShell: `$env:DB_AUTH_MODE='sql-password'; $env:DB_SERVER='db81053.databaseasp.net'; $env:DB_NAME='db81053'; $env:DB_USER='db81053'; $env:DB_PASSWORD='<database password>'`.
   Success: `apply 001` to `004`, `Security policies CrmRowPolicy and CrmConfigPolicy are enabled.`, `Seeded test users and teams.`
   *Alternative from GitHub:* Run workflow with **run_migrations** ticked. It runs the same two commands on a GitHub server before deploying, but GitHub's server IPs change on every run and are almost certainly blocked by MonsterASP's database firewall, so expect it to fail; use your PC.
9. **Confirm both security policies are enabled** (SSMS or Azure Data Studio, or MonsterASP's web SQL tool, as `db81053`):
   ```sql
   SELECT name, is_enabled FROM sys.security_policies;
   SELECT COUNT(*) AS predicates FROM sys.security_predicates;
   ```
   Expect `CrmRowPolicy` and `CrmConfigPolicy`, both `is_enabled = 1`, and predicates above 0. Otherwise **stop** and send the output to the developer (the host may not support RLS for this login).
10. **Run the row-level security test.** Open `db/tests/rls-integration.sql` in SSMS/Azure Data Studio as `db81053` and execute the whole file, or:
    ```
    sqlcmd -S db81053.databaseasp.net -d db81053 -U db81053 -P '<database password>' -b -i db/tests/rls-integration.sql
    ```
    The script creates its own temporary `WITHOUT LOGIN` user in `crm_app_role`, impersonates it, and rolls everything back; you run nothing extra. Read the result table at the end: **every row must say PASS**. Any FAIL, error or non-zero `sqlcmd` exit means do not use the site; send the full output to the developer. (First run ever: a syntax slip in the script is possible.)
11. **Post-deploy checks.**
    - https://crmtest.runasp.net/healthz shows `ok`.
    - Open the home page, enter the access code, choose a test user; the "TEST ENVIRONMENT" banner shows.
    - Sign in as an Admin test user and open **Settings > Users & teams**: the line "Your connection: IP ... (X-Forwarded-For hops trusted: 1)" must show **your real public IP**, not an internal 10.x/127.x address. If it shows an internal address, change variable `TRUST_PROXY` (try 2) and re-run the workflow. A wrong value makes the sign-in rate limit block everybody at once.

## Each deploy

Push to the branch (or Run workflow). Green = deployed. The workflow puts the site offline for a few seconds (`app_offline.htm`) while files sync. Repeat the `/healthz` check. New migration files: repeat step 8.

## Rollback

GitHub > Actions > pick the last good run of "Deploy to test" > Re-run all jobs (this redeploys that run's commit), or ask the developer to `git revert` the bad commit and push. Database changes are not reverted automatically: restore from MonsterASP's backup, or, being test data, wipe and repeat steps 8-10. Not yet exercised: try it once after the first good deploy.

## Troubleshooting

Logs: set `loggingEnabled="true"` in `web.config` on the host (file manager), read `iisnode\*-stdout-*.txt` / `*-stderr-*.txt`, set it back to `false`. The deploy keeps the `iisnode` folder. Secrets are never logged.

| Symptom | Cause / action |
|---|---|
| Workflow: "Secret ... is not set" | Add it in Settings > Environments > test (step 4). |
| Web Deploy: 401 / ERROR_USER_UNAUTHORIZED | Wrong user/password/site name, or Web Deploy not enabled in the panel. |
| Web Deploy: certificate / trust error | Set variable `MONSTERASP_WEBDEPLOY_ALLOW_UNTRUSTED=true`. |
| Web Deploy: connection timed out | Wrong server/port (default 8172) or host blocks it; ask MonsterASP support. |
| Site 500.1001 / 500.1000 | Node crashed at start: read the iisnode log (`Invalid configuration:` names the setting; Node too old; DB unreachable). |
| 500.19 / 500.21 | Host rejected a `web.config` section (e.g. `webSocket`); send the error to the developer. |
| "too many attempts" for everyone | `TRUST_PROXY` wrong (step 11). |
| Everyone signed out | `SESSION_SECRET` changed; expected. |

## Test-only gaps (accepted)

- **App runs as the database owner** (host gives one login). RLS still filters the app's queries, but the owner can switch policies off. The RLS test proves the policies with a low-privilege user. If the host later allows a second login, create it with `db/manual/create-app-user.sql` and change `DB_USER` and the `DB_PASSWORD` secret. Must not carry over to Azure.
- **Test data only.** Everything is disposable.
- **Secrets are readable on the host** (`crm.env` in the site folder) by anyone with panel/FTP access, and by repo admins via the Environment. Rotate `TEST_ACCESS_CODE` (edit the secret, re-run the workflow) when a tester leaves; max 5 wrong tries per 15 minutes per IP. Changing `SESSION_SECRET` signs everyone out.
- **Shared host:** no staging, limited logs, backups per the host plan. Actions are pinned to major tags, not commit SHAs. The Web Deploy step is untested until the first real run.
