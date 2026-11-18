# Checklist: interim TEST site on MonsterASP.NET

Site: https://crmtest.runasp.net. Database `db81053` on `db81053.databaseasp.net`. Sign-in by shared access code. **Test data only, never real data.**
Background: `docs/architecture/overview.md` section 12.3.

How it works: every push to `claude/tender-darwin-4quuzo` runs GitHub Actions (lint, typecheck, tests), builds a ready-to-run package and force-pushes it as
one commit to the branch `deploy`. MonsterASP pulls `deploy`. MonsterASP does not run `npm install`, so the package already contains everything.
Never edit `deploy` by hand. Passwords are never in git: they live in a file `crm.env` that you upload once.

Status of `deploy` (checked at 2a643d5): web.config, iisnode-entry.js, dist/, db/ and production node_modules at the root; about 62 MB;
no src/, tests/, .env or source-map files outside third-party libraries.

## One-time setup

1. **GitHub.** Nothing to do: Actions are enabled and the `deploy` branch exists. To rebuild it any time: GitHub > Actions > "Deploy to test (MonsterASP)" > Run workflow.
2. **MonsterASP Git deploy.** In the control panel, open the site's Git/deployment settings, choose this GitHub repository and the branch **`deploy`** (not main), then run a deploy/sync.
3. **Node version.** In the panel's Node.js setting choose the newest LTS offered (must be 20 or newer).
4. **Create `crm.env`.** On your PC make a text file named `crm.env` (copy `deploy/crm.env.example` from the repo) and fill in the three secrets. Generate each secret by running this once per secret (needs Node installed) and pasting the output:
   ```
   node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"
   ```
   Contents (`DB_PASSWORD` is the password MonsterASP gave you for the database; `SESSION_SECRET` and `TEST_ACCESS_CODE` are two different generated values; the access code is what testers type, so share it only with testers):
   ```
   NODE_ENV=production
   AUTH_MODE=test-access-code
   REDIRECT_URI=https://crmtest.runasp.net/auth/redirect
   TRUST_PROXY=1
   LOG_LEVEL=info
   DB_AUTH_MODE=sql-password
   DB_SERVER=db81053.databaseasp.net
   DB_NAME=db81053
   DB_USER=db81053
   DB_PASSWORD=<database password>
   SESSION_SECRET=<generated value 1>
   TEST_ACCESS_CODE=<generated value 2>
   ```
   (There is no `BASE_URL` setting; the app derives it from `REDIRECT_URI`. Add `DB_TRUST_SERVER_CERT=true` only if the database connection later fails with a certificate error.)
5. **Upload `crm.env`** with the panel's file manager (or FTP) into the folder **one level above `wwwroot`**; if that is not writable, into the site folder (`wwwroot`). After the next deploy check the file is still there.
   If the panel has an "Environment variables" page you may enter the same names there instead of using the file.
6. **Allow your PC to reach the database.** In the MonsterASP panel, database section, add your public IP (find it at https://ifconfig.me) to the allowed/remote-access list.
7. **Run migrations and test users from your PC** (needs Node **22.9 or newer**, and the repo checked out on branch `claude/tender-darwin-4quuzo`).
   macOS/Linux:
   ```
   npm ci
   export DB_AUTH_MODE=sql-password DB_SERVER=db81053.databaseasp.net DB_NAME=db81053 DB_USER=db81053
   export DB_PASSWORD='<database password>'
   npm run db:migrate
   npm run db:seed:test
   ```
   Windows PowerShell: same, but `$env:DB_AUTH_MODE='sql-password'; $env:DB_SERVER='db81053.databaseasp.net'; $env:DB_NAME='db81053'; $env:DB_USER='db81053'; $env:DB_PASSWORD='<database password>'`.
   Success looks like `apply 001...004` then `Security policies CrmRowPolicy and CrmConfigPolicy are enabled.` and `Seeded test users and teams.`
   Add `DB_TRUST_SERVER_CERT=true` if you get a certificate error. Do not paste the scripts into MonsterASP's web SQL tool instead unless this fails: that skips the migration bookkeeping.
8. **Confirm both security policies are enabled** (SSMS, Azure Data Studio or the web SQL tool, connected as `db81053`):
   ```sql
   SELECT name, is_enabled FROM sys.security_policies;
   SELECT COUNT(*) AS predicates FROM sys.security_predicates;
   ```
   Expect two rows, `CrmRowPolicy` and `CrmConfigPolicy`, both `is_enabled = 1`, and predicates greater than 0. If a policy is missing or 0, **stop**: the host may not allow RLS; send the error to the developer and do not use the site.
9. **Run the row-level security test.** Open `db/tests/rls-integration.sql` in SSMS/Azure Data Studio connected as `db81053` and execute the whole file, or from a terminal:
   ```
   sqlcmd -S db81053.databaseasp.net -d db81053 -U db81053 -P '<database password>' -b -i db/tests/rls-integration.sql
   ```
   You do not need to create a user: the script itself creates a temporary `WITHOUT LOGIN` user in `crm_app_role`, impersonates it with `EXECUTE AS USER`, and rolls everything back.
   Read the final result table: one row per case, column `Pass` / PASS or FAIL. **Every row must be PASS.** Any FAIL, an error, or `sqlcmd` exiting non-zero means do not use the site; send the full output to the developer.
   This script had never been run before, so a first-run syntax slip is possible.
10. **Post-deploy checks.**
    - Open https://crmtest.runasp.net/healthz: it must show `ok`.
    - Open the home page, enter the access code, pick a test user, open a list page. The banner "TEST ENVIRONMENT" should show.
    - **Client IP check (rate limits depend on it).** The app currently does not display or log `req.ip`, so test indirectly: from your phone on mobile data, enter a wrong access code 5 times (it then blocks). Then, from a laptop on a different network, try one wrong code: it must still say "wrong code", not "too many attempts". If the laptop is blocked too, all users share one IP (the `TRUST_PROXY` value is wrong); try `TRUST_PROXY=2`, restart the site, repeat. **Open item for the developer:** add `ip: req.ip` to the request log line in `src/middleware/requestContext.ts` so this can be read from the log directly.

## Each deploy

1. Push to `claude/tender-darwin-4quuzo`, or run the workflow manually. Wait for green.
2. MonsterASP pulls `deploy` (press Deploy/Sync in the panel if it is not automatic). `BUILD-INFO.txt` on the site shows which commit is live.
3. Repeat the `/healthz` and sign-in checks. If a new migration was added, run `npm run db:migrate` from your PC (step 7; it skips applied ones).

## Rollback

- Ask the developer to `git revert` the bad commit and push; the workflow rebuilds `deploy` and MonsterASP redeploys. Or check out the last good commit on a branch and run the workflow from it (add that branch to the workflow trigger).
- Emergency: in the MonsterASP panel, stop the site (or use its Git history to redeploy an earlier deploy commit if offered).
- Database: migrations are not reverted automatically; restore from the host's backup, or, since this is test data, wipe and re-run steps 7-9.
- The rollback has not been exercised yet: try it once after the first good deploy.

## Troubleshooting

Turn on logs only when needed: edit `web.config` on the host and set `loggingEnabled="true"`; logs appear in the `iisnode` folder (`*-stdout-*.txt`, `*-stderr-*.txt`). Set it back to `false` afterwards. Secrets are never logged.

| Symptom | Cause / action |
|---|---|
| 500.1001 / 500.1000 (iisnode) | Node crashed at start. Read the log: usually `Invalid configuration:` (names the bad setting), Node too old, or missing `crm.env`. |
| Log: `config: no crm.env found` | File not in `..\crm.env` or `.\crm.env`; re-upload (step 5). |
| 500.19 / 500.21 | Host rejected a `web.config` section (e.g. `webSocket`, `requestFiltering`); send the error to the developer. |
| 403.14 / 404 for everything | Wrong branch or folder: the site root must contain `web.config` and `iisnode-entry.js`. |
| Database login failed | Wrong `DB_PASSWORD`, your IP/host IP not allowed, or certificate (`DB_TRUST_SERVER_CERT=true`). |
| Everyone signed out | `SESSION_SECRET` changed; expected. |
| Everyone "too many attempts" | `TRUST_PROXY` wrong (step 10). |

## Test-only gaps (accepted)

- **App runs as the database owner** (the host gives one login). RLS still filters the app's queries, but the owner can switch policies off, so an injection could disable RLS. The RLS test proves the policies with a low-privilege user. If the host later allows a second login, create it with `db/manual/create-app-user.sql` and change `DB_USER`/`DB_PASSWORD`. Must not carry over to Azure.
- **Test data only.** Everything is disposable.
- **Shared access code.** Rotate by editing `TEST_ACCESS_CODE` in `crm.env` and restarting the site (touch `web.config` or use the panel's restart) when a tester leaves or the code leaks; max 5 wrong tries per 15 minutes per IP.
- **Shared host:** no staging, limited logs, backups per the host plan. Actions are pinned to major tags, not SHAs.
