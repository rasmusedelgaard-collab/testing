# Runbook: interim TEST site on MonsterASP.NET

Site: https://crmtest.runasp.net (Windows / IIS + iisnode). Database: MSSQL `db81053` on `db81053.databaseasp.net`.
Auth mode: `AUTH_MODE=test-access-code` (no Entra). **Test data only.** Design context: `docs/architecture/overview.md` section 12.3.

## How it works

1. A push to `claude/tender-darwin-4quuzo` (later `main`) runs `.github/workflows/deploy-test.yml`: lint, typecheck, tests, then
   `npm run package` and a **force-push of the package as one fresh orphan commit to branch `deploy`**.
2. MonsterASP git-deploys from `deploy`. MonsterASP does **not** run `npm install` or a build, which is why the package already
   contains `dist/` and production `node_modules/`.
3. `web.config` routes every request to `iisnode-entry.js`, which loads the settings file `crm.env` (not in git) and starts `dist/server.js`.

The `deploy` branch contains generated files only. Never edit it by hand and never merge it.

## Settings and secrets (never in git)

iisnode on a plain IIS host does not reliably expose `web.config` `<appSettings>` as environment variables (that is an Azure App
Service behaviour), and anything in `web.config` would be in git. So settings come from a separate file, `crm.env`, read by
`iisnode-entry.js`. Lookup order, first found wins, real environment variables always win:

1. the file named by the environment variable `CRM_ENV_FILE` (only if the control panel offers real environment variables)
2. `crm.env` **one level above the site folder** (preferred: survives a deploy that wipes the site folder)
3. `crm.env` inside the site folder (works only if the deploy does not delete untracked files; test this once, see below)

If the control panel has an "Environment variables" section for the site, you may set the variables there instead and skip the file.

Create `crm.env` from `crm.env.example` (also at `deploy/crm.env.example` in the repo):

| Name | Value | Secret |
|---|---|---|
| `NODE_ENV` | `production` | no |
| `AUTH_MODE` | `test-access-code` | no |
| `REDIRECT_URI` | `https://crmtest.runasp.net/auth/redirect` (drives HTTPS cookie flags) | no |
| `DB_AUTH_MODE` | `sql-password` | no |
| `DB_SERVER` | `db81053.databaseasp.net` | no |
| `DB_NAME` | `db81053` | no |
| `DB_USER` | `db81053` | no |
| `DB_PASSWORD` | the database password | **yes** |
| `SESSION_SECRET` | 32+ random characters (comma-separated list allowed for rotation) | **yes** |
| `TEST_ACCESS_CODE` | shared sign-in code, 16+ characters | **yes** |
| `DB_TRUST_SERVER_CERT` | `true` only if connections fail with a certificate error | no |
| `LOG_LEVEL` | optional, default `info` | no |

Generate secrets: `node -e "console.log(require('crypto').randomBytes(48).toString('base64url'))"`.
Do not set `DEMO_MODE`; the app refuses it with `NODE_ENV=production`.

## One-time setup

1. **GitHub connection.** In the MonsterASP panel, point the site's Git deployment at this repository, branch **`deploy`**.
   The branch exists only after the first workflow run: run **Actions > Deploy to test (MonsterASP) > Run workflow** first.
2. **Node version.** In the panel's Node.js setting for the site choose the newest LTS offered (code needs Node 20+; CI tests 20 and 22).
   After the first deploy, `iisnode\` logs (see Troubleshooting) or the startup error will show the version if it is too old.
3. **Settings file.** Fill in `crm.env` on your PC and upload it with the panel's file manager/FTP to the folder **above** `wwwroot`
   (or into the site folder if the parent is not writable). Then confirm it survives a deploy: re-run the workflow and check the file is still there.
4. **Database migrations 001-004 and test users** (the database must be empty or only contain this app's tables). Choose one:
   - **From your PC (recommended; verifies policies automatically).** Needs the DB host to accept your IP (check the remote-access / IP allowlist in the panel).
     ```bash
     # needs Node 22.9+ on your PC (the db scripts use --env-file-if-exists)
     git checkout claude/tender-darwin-4quuzo && npm ci
     export DB_AUTH_MODE=sql-password DB_SERVER=db81053.databaseasp.net DB_NAME=db81053 DB_USER=db81053
     export DB_PASSWORD='<password>'          # PowerShell: $env:DB_PASSWORD='...'
     # export DB_TRUST_SERVER_CERT=true       # only if you get a certificate error
     npm run db:migrate                       # applies 001-004, records checksums, then asserts both security policies are ENABLED
     npm run db:seed:test                     # db/seed/test-users.sql (test users and teams)
     ```
   - **Via MonsterASP's web SQL tool.** Paste and run, in order, `db/migrations/001_schema.sql`, `002_rls.sql`, `003_grants.sql`, `004_seed.sql`, then
     `db/seed/test-users.sql`. The scripts contain `GO` separators; if the tool does not support `GO`, run each batch (text between `GO` lines) separately.
     This path does not create `dbo.SchemaVersions`, so a later `npm run db:migrate` would try to re-apply 001: do not mix the two paths.
5. **Verify the security policies are enabled.** `CREATE SECURITY POLICY` needs `ALTER ANY SECURITY POLICY` plus `ALTER` on the schema; the database owner
   (`dbo`/`db_owner`) has these, so it should work with the owner login. **Not confirmed on this host (shared hosting can restrict features): confirm explicitly:**
   ```sql
   SELECT name, is_enabled FROM sys.security_policies;      -- expect sec.CrmRowPolicy and sec.CrmConfigPolicy with is_enabled = 1
   SELECT COUNT(*) FROM sys.security_predicates;            -- expect > 0
   ```
   If migration 002 fails or the policies are missing/disabled, **stop**: do not use the site; RLS is the core data protection. Report the error.
   (`npm run db:migrate` already fails with "Security policy not enabled or missing" in that case.)
6. **RLS integration test (release gate).** Run `db/tests/rls-integration.sql` as the owner. It rolls back and leaves no data.
   ```bash
   sqlcmd -S db81053.databaseasp.net -d db81053 -U db81053 -P '<password>' -b -i db/tests/rls-integration.sql
   ```
   or paste it into the web SQL tool. Read the final result table: every row must say **PASS**; any **FAIL** (or `sqlcmd` exiting non-zero)
   means do not use the site and send the output to the developer. The script header notes it has not been run before: a syntax slip is possible on the first run.
7. Open https://crmtest.runasp.net/healthz (expect `ok`), then the home page, enter the access code and choose a test user.

## Each deploy

1. Push to `claude/tender-darwin-4quuzo` (or run the workflow manually). Wait for the workflow to go green; it force-pushes `deploy`.
2. MonsterASP pulls `deploy` (automatically via webhook, or press Deploy/Sync in the panel if it is manual).
3. Check `/healthz`, sign in, open one list page. `BUILD-INFO.txt` in the site folder shows the commit that is live.
4. New migrations: the deploy does **not** run them. Run `npm run db:migrate` from your PC before or right after the deploy (it skips applied files and refuses changed ones).
   Only reversible or approved one-way migrations should be shipped.

## Rollback

The `deploy` branch has a single commit, so the previous one is only available if you recorded it:
- **Fast path:** re-run the workflow on the previous good commit: Actions > Deploy to test > Run workflow, choose that commit's branch/ref
  (for an older SHA, create a temporary branch at it and run the workflow from there, adding the branch to the workflow trigger or via `workflow_dispatch` on that branch). A fresh `deploy` commit is built and MonsterASP redeploys it.
- **Panel path:** the MonsterASP panel's Git deployment history may allow redeploying a previous deploy commit; GitHub also keeps the old commit reachable by SHA for a time (`git fetch origin <sha>`), so `git push -f origin <sha>:deploy` restores it.
- **Database:** migrations are not auto-reverted. Ship backwards-compatible migrations; restore from the host's database backup (panel) if needed. This is test data, so the simplest recovery is re-running migrations on a fresh database.
- The rollback has not been exercised yet: do one trial (deploy twice, redeploy the first) after the first successful deploy.

## Troubleshooting

| Symptom | Likely cause and action |
|---|---|
| 500.1001 / 500.1000 iisnode "failed to start" | Node crashed at start. Enable logging (below). Usual causes: invalid configuration (message lists which variable), Node too old, missing `crm.env`. |
| Log says `Invalid configuration:` | A setting in `crm.env` is missing or malformed; the message names it (values are not printed). |
| `config: no crm.env found` in the log | The file is not in `..\crm.env` or `.\crm.env`; re-upload (see Settings and secrets). |
| 500.19 / 500.21 from IIS | `web.config` rejected: the host may not allow a section (e.g. `webSocket` or `requestFiltering`). Remove the offending element and tell DevOps. |
| 404 for everything / 403.14 | Git deploy pointed at the wrong branch or folder: site root must contain `web.config` and `iisnode-entry.js`. |
| DB errors / login failed | Wrong `DB_PASSWORD`, IP restrictions on the DB host, or certificate (`DB_TRUST_SERVER_CERT=true`). |
| Login loops / session lost | `REDIRECT_URI` not https, or `SESSION_SECRET` changed (all sessions end; expected). |
| Syntax errors on startup | Node older than 20 on the host. Select a newer Node in the panel. |

**iisnode logs:** in `web.config` set `loggingEnabled="true"`. That change would be overwritten by the next deploy, so edit the file directly on the host with the panel's
file manager for a debugging session, then restore `false` (or redeploy). Logs appear in the `iisnode\` folder (`*-stdout-*.txt`, `*-stderr-*.txt`) and contain pino JSON lines; secrets are never logged by the app.

To pin a Node binary instead of using the panel setting, add `nodeProcessCommandLine="&quot;<full path to node.exe>&quot;"` to the `<iisnode>` element in `deploy/iis/web.config`
(an example is in the file's comment).

## Test-only gaps (accepted for this interim host)

- **App runs as the database owner.** The host provides one login only (`db81053`). RLS predicates still filter the app's queries, but the owner can
  `ALTER SECURITY POLICY ... STATE = OFF`, drop predicates and read `dbo` tables, so an injection or a compromised app could switch RLS off. The RLS proof is the
  integration script, which impersonates a `WITHOUT LOGIN` user in `crm_app_role`. If the host later allows a second login, create it and a user with `db/manual/create-app-user.sql`
  and switch `DB_USER`/`DB_PASSWORD` in `crm.env`. This must not carry over to Azure.
- **Test data only.** No customer or personal data, ever. Everything here is disposable.
- **Shared access code.** One code for all testers, with a rate limit of 5 wrong attempts per 15 minutes per IP. Rotate by editing `TEST_ACCESS_CODE` in `crm.env` (the app reads it at start, so
  touch `web.config` or press the panel's restart/recycle to apply) whenever a tester leaves or the code is shared outside the group. Rotating `SESSION_SECRET` signs everyone out.
- **Shared-host limits.** No staging slot, no auto-rollback, limited log retention, DB backups per the host's plan. Not suitable for any load or availability test.
- **Actions pinned by major tag** (`@v4`), not commit SHA; the deploy workflow's `GITHUB_TOKEN` has `contents: write` only in the publish job.
